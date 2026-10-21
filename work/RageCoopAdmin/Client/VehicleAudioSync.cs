using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using GTA;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

// The installed RageCoop already transports RadioStation in its full vehicle
// packets, but never applies it. Horn start/stop is already handled by RageCoop.
internal static class VehicleAudioSync
{
    private const string PatchId = "friendslegacy.ragecoop.vehicleradio";
    private static readonly long PassengerCheckInterval = Math.Max(1, Stopwatch.Frequency / 4);
    private static ConditionalWeakTable<SyncedVehicle, AppliedRadio> _applied = new();
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    private static AccessTools.FieldRef<SyncedVehicle, byte>? _station;
    private static bool _failed;

    internal static bool Install()
    {
        if (_harmony != null) { return !_failed; }
        MethodInfo? target = FindUpdateMethod();
        FieldInfo? station = FindStationField();
        if (target == null || station == null) { return false; }

        var harmony = new Harmony(PatchId);
        _station = AccessTools.FieldRefAccess<SyncedVehicle, byte>(station);
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(VehicleAudioSync), nameof(AfterVehicleUpdated)));
        }
        catch
        {
            harmony.Unpatch(target, HarmonyPatchType.Postfix, PatchId);
            _station = null;
            throw;
        }

        _applied = new ConditionalWeakTable<SyncedVehicle, AppliedRadio>();
        _failed = false;
        _harmony = harmony;
        _target = target;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony != null && _target != null)
        {
            _harmony.Unpatch(_target, HarmonyPatchType.Postfix, PatchId);
        }

        _harmony = null;
        _target = null;
        _station = null;
        _failed = false;
        _applied = new ConditionalWeakTable<SyncedVehicle, AppliedRadio>();
    }

    internal static MethodInfo? FindUpdateMethod()
    {
        MethodInfo? method = typeof(SyncedVehicle).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        return method?.ReturnType == typeof(void) ? method : null;
    }

    internal static FieldInfo? FindStationField()
    {
        FieldInfo? field = typeof(SyncedVehicle).GetField("RadioStation", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.FieldType == typeof(byte) ? field : null;
    }

    private static void AfterVehicleUpdated(SyncedVehicle __instance)
    {
        if (_failed || _station == null || !API.IsOnServer || __instance.IsLocal || __instance.LastFullSynced == 0)
        {
            return;
        }

        try
        {
            ApplyRadio(__instance);
        }
        catch (Exception ex)
        {
            // A radio failure must not abort RageCoop's vehicle update loop.
            // Disable this optional patch and retain the actual error in the log.
            _failed = true;
            API.Logger.Warning("Coop Control: radio synchronization disabled: " + ex);
        }
    }

    private static void ApplyRadio(SyncedVehicle synced)
    {
        Vehicle? vehicle = synced.MainVehicle;
        if (vehicle == null) { return; }

        byte station = _station!(synced);
        AppliedRadio applied = _applied.GetValue(synced, _ => new AppliedRadio());
        long now = Stopwatch.GetTimestamp();
        bool changed = applied.Handle != vehicle.Handle || applied.Station != station;
        if (!changed && now < applied.NextPassengerCheck) { return; }
        applied.NextPassengerCheck = now + PassengerCheckInterval;
        if (!vehicle.Exists() || !RuntimeEntityLookup.IsCurrentVehicle(synced)) { return; }
        if (changed)
        {
            applied.Handle = vehicle.Handle;
            applied.Station = station;
            applied.Name = station == byte.MaxValue ? "OFF" : Function.Call<string>(Hash.GET_RADIO_STATION_NAME, (int)station);
        }

        if (string.IsNullOrWhiteSpace(applied.Name)) { return; }
        if (!changed)
        {
            Ped? character = Game.Player.Character;
            Vehicle? current = character?.CurrentVehicle;
            if (current == null || current.Handle != vehicle.Handle ||
                NormalizeRadioIndex(Function.Call<int>(Hash.GET_PLAYER_RADIO_STATION_INDEX)) == station)
            {
                return;
            }
        }

        Function.Call(Hash.SET_VEH_RADIO_STATION, vehicle.Handle, applied.Name);
    }

    internal static byte NormalizeRadioIndex(int index) => index is >= 0 and < byte.MaxValue ? (byte)index : byte.MaxValue;

    private sealed class AppliedRadio
    {
        internal int Handle;
        internal byte Station = byte.MaxValue;
        internal string? Name;
        internal long NextPassengerCheck;
    }
}
