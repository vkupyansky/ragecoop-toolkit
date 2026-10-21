using System;
using System.Collections.Generic;
using System.Reflection;
using CoopControl.Shared;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// RageCoop's full packet only contains part of the appearance and can arrive
// after our reliable complete snapshot. Keep its cosmetic cache consistent;
// physics, health, ownership and the rest of Update remain untouched.
internal static class VehicleAppearanceRuntimeGuard
{
    private const string PatchId = "friendslegacy.ragecoop.appearance";
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    private static Func<SyncedVehicle, AppearanceSyncSnapshot?>? _lookup;
    private static Action<SyncedVehicle, bool>? _apply;
    private static readonly PropertyInfo? Colors = Property("Colors", typeof(byte[]));
    private static readonly PropertyInfo? Mods = Property("Mods", typeof(Dictionary<int, int>));
    private static readonly PropertyInfo? Plate = Property("LicensePlate", typeof(string));
    private static readonly PropertyInfo? Livery = Property("Livery", typeof(int));
    private static readonly FieldInfo? LastColors = Field("_lastVehicleColors", typeof(byte[]));
    private static readonly FieldInfo? LastMods = Field("_lastVehicleMods", typeof(Dictionary<int, int>));
    private static readonly FieldInfo? LastLivery = Field("_lastLivery", typeof(int));

    internal static bool IsSupported => FindTarget()?.ReturnType == typeof(void) && Colors != null && Mods != null && Plate != null &&
        Livery != null && LastColors != null && LastMods != null && LastLivery != null;

    internal static bool Install(Func<SyncedVehicle, AppearanceSyncSnapshot?> lookup, Action<SyncedVehicle, bool> apply)
    {
        if (_harmony != null) { return true; }
        MethodInfo? target = FindTarget();
        if (!IsSupported || target == null) { return false; }
        var harmony = new Harmony(PatchId);
        _lookup = lookup;
        _apply = apply;
        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(VehicleAppearanceRuntimeGuard), nameof(BeforeVehicleUpdate)),
                postfix: new HarmonyMethod(typeof(VehicleAppearanceRuntimeGuard), nameof(AfterVehicleUpdate)));
        }
        catch
        {
            harmony.Unpatch(target, HarmonyPatchType.All, PatchId);
            _lookup = null;
            _apply = null;
            throw;
        }

        _target = target;
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony != null && _target != null) { _harmony.Unpatch(_target, HarmonyPatchType.All, PatchId); }
        _harmony = null;
        _target = null;
        _lookup = null;
        _apply = null;
    }

    internal static MethodInfo? FindTarget() => typeof(SyncedVehicle).GetMethod("Update",
        BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

    private static void BeforeVehicleUpdate(SyncedVehicle __instance)
    {
        if (__instance.IsLocal || __instance.LastFullSynced < __instance.LastUpdated) { return; }
        AppearanceSyncSnapshot? snapshot = _lookup?.Invoke(__instance);
        if (snapshot != null) { SynchronizeCache(__instance, snapshot); }
    }

    private static void AfterVehicleUpdate(SyncedVehicle __instance)
    {
        AppearanceSyncSnapshot? snapshot = _lookup?.Invoke(__instance);
        if (snapshot == null) { return; }
        // The installed receive path writes full packets on the network thread.
        // If one raced the prefix and was actually applied by Update, the cached
        // "last applied" references reveal it. Restore only in that case.
        bool restore = NeedsRestore(__instance, snapshot);
        if (restore) { SynchronizeCache(__instance, snapshot); }
        _apply?.Invoke(__instance, restore);
    }

    internal static bool NeedsRestore(SyncedVehicle vehicle, AppearanceSyncSnapshot snapshot) =>
        !ReferenceEquals(LastColors!.GetValue(vehicle), snapshot.Colors) ||
        !ReferenceEquals(LastMods!.GetValue(vehicle), snapshot.Mods) ||
        (int)LastLivery!.GetValue(vehicle)! != snapshot.Appearance.Livery;

    internal static void SynchronizeCache(SyncedVehicle vehicle, AppearanceSyncSnapshot snapshot)
    {
        Colors!.SetValue(vehicle, snapshot.Colors);
        LastColors!.SetValue(vehicle, snapshot.Colors);
        Mods!.SetValue(vehicle, snapshot.Mods);
        LastMods!.SetValue(vehicle, snapshot.Mods);
        Plate!.SetValue(vehicle, snapshot.Appearance.Plate);
        Livery!.SetValue(vehicle, snapshot.Appearance.Livery);
        LastLivery!.SetValue(vehicle, snapshot.Appearance.Livery);
    }

    private static PropertyInfo? Property(string name, Type type)
    {
        PropertyInfo? property = typeof(SyncedVehicle).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return property?.PropertyType == type && property.GetSetMethod(true) != null ? property : null;
    }

    private static FieldInfo? Field(string name, Type type)
    {
        FieldInfo? field = typeof(SyncedVehicle).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.FieldType == type ? field : null;
    }
}

internal sealed class AppearanceSyncSnapshot
{
    internal AppearanceSyncSnapshot(VehicleAppearance appearance)
    {
        VehicleAppearanceCodec.Validate(appearance);
        Appearance = appearance;
        Colors = new[] { (byte)appearance.Primary, (byte)appearance.Secondary };
        Mods = new Dictionary<int, int>();
        for (int index = 0; index < appearance.Mods.Length; index++)
        {
            if (index < 17 || index > 22) { Mods.Add(index, appearance.Mods[index]); }
        }
    }

    internal VehicleAppearance Appearance { get; }
    internal byte[] Colors { get; }
    internal Dictionary<int, int> Mods { get; }
}
