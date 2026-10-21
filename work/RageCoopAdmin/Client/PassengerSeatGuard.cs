using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;
using GTA;
using GTA.Input;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

internal static class PassengerSeatGuard
{
    private const string PatchId = "friendslegacy.ragecoop.passengerseat";
    private const int EntryTimeoutMilliseconds = 10000;
    private static Harmony? _harmony;
    private static MethodInfo? _onKeyDown;
    private static MethodInfo? _onTick;
    private static MethodInfo? _displayInVehicle;
    private static FieldInfo? _seatField;
    private static PropertyInfo? _currentVehicleProperty;
    private static Func<bool>? _resourceInputBlocked;
    private static Vehicle? _entryVehicle;
    private static int _entryPlayer;
    private static int _entrySeat;
    private static long _entryStarted;
    private static bool _suppressUntilReleased;
    private static long _lastNoSeatNotice;
    private static bool _reportedRuntimeFailure;

    internal static bool Install(Func<bool> resourceInputBlocked)
    {
        if (_harmony != null) { return true; }
        if (resourceInputBlocked == null) { throw new ArgumentNullException(nameof(resourceInputBlocked)); }

        Type? main = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Main", false);
        MethodInfo? onKeyDown = main == null ? null : AccessTools.DeclaredMethod(main, "OnKeyDown", new[] { typeof(object), typeof(KeyEventArgs) });
        MethodInfo? onTick = main == null ? null : AccessTools.DeclaredMethod(main, "OnTick", new[] { typeof(object), typeof(EventArgs) });
        MethodInfo? displayInVehicle = AccessTools.DeclaredMethod(typeof(SyncedPed), "DisplayInVehicle", Type.EmptyTypes);
        FieldInfo? seat = AccessTools.Field(typeof(SyncedPed), "Seat");
        PropertyInfo? currentVehicle = AccessTools.Property(typeof(SyncedPed), "CurrentVehicle");
        if (onKeyDown?.ReturnType != typeof(void) || onTick?.ReturnType != typeof(void) ||
            displayInVehicle?.ReturnType != typeof(void) || seat?.FieldType != typeof(VehicleSeat) ||
            currentVehicle?.PropertyType != typeof(SyncedVehicle) ||
            !RuntimeEntityLookup.CanIdentifyPlayerPed ||
            FindPassengerComparison(onKeyDown, PatchProcessor.GetOriginalInstructions(onKeyDown)) < 0)
        {
            return false;
        }

        _seatField = seat;
        _currentVehicleProperty = currentVehicle;
        _resourceInputBlocked = resourceInputBlocked;
        _reportedRuntimeFailure = false;
        var harmony = new Harmony(PatchId);
        try
        {
            harmony.Patch(onKeyDown, transpiler: new HarmonyMethod(typeof(PassengerSeatGuard), nameof(DisablePassengerShortcut)));
            // The resource OnTick runs after the engine's OnTick/DoSync. Input
            // must be redirected before either can observe a driver entry task.
            harmony.Patch(onTick, prefix: new HarmonyMethod(typeof(PassengerSeatGuard), nameof(ProcessEnterControl)) { priority = Priority.First });
            harmony.Patch(displayInVehicle, prefix: new HarmonyMethod(typeof(PassengerSeatGuard), nameof(PreserveLocalDriver)));
        }
        catch
        {
            harmony.Unpatch(onKeyDown, HarmonyPatchType.Transpiler, PatchId);
            harmony.Unpatch(onTick, HarmonyPatchType.Prefix, PatchId);
            harmony.Unpatch(displayInVehicle, HarmonyPatchType.Prefix, PatchId);
            _seatField = null;
            _currentVehicleProperty = null;
            _resourceInputBlocked = null;
            throw;
        }

        _onKeyDown = onKeyDown;
        _onTick = onTick;
        _displayInVehicle = displayInVehicle;
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony == null) { return; }
        if (_onKeyDown != null) { _harmony.Unpatch(_onKeyDown, HarmonyPatchType.Transpiler, PatchId); }
        if (_onTick != null) { _harmony.Unpatch(_onTick, HarmonyPatchType.Prefix, PatchId); }
        if (_displayInVehicle != null) { _harmony.Unpatch(_displayInVehicle, HarmonyPatchType.Prefix, PatchId); }
        _harmony = null;
        _onKeyDown = null;
        _onTick = null;
        _displayInVehicle = null;
        _seatField = null;
        _currentVehicleProperty = null;
        _resourceInputBlocked = null;
        ResetEntry();
        _suppressUntilReleased = false;
    }

    internal static bool HideOriginalSetting() => _harmony == null || PassengerSeatSetting.Hide();

    internal static int FindPassengerComparison(MethodInfo target, IList<CodeInstruction> instructions)
    {
        Type? main = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Main", false);
        Type? settings = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Settings", false);
        MethodInfo? getter = settings == null ? null : AccessTools.PropertyGetter(settings, "PassengerKey");
        FieldInfo? field = main == null ? null : AccessTools.Field(main, "Settings");
        if (target.DeclaringType != main || target.Name != "OnKeyDown" || getter?.ReturnType != typeof(Keys) ||
            field?.FieldType != settings) { return -1; }
        int match = -1;
        for (int index = 3; index + 4 < instructions.Count; index++)
        {
            if (!instructions[index].Calls(getter)) { continue; }
            if (match >= 0 || instructions[index - 3].opcode != OpCodes.Ldarg_2 ||
                !instructions[index - 2].Calls(AccessTools.PropertyGetter(typeof(KeyEventArgs), nameof(KeyEventArgs.KeyCode))) ||
                !instructions[index - 1].LoadsField(field) || instructions[index + 1].opcode != OpCodes.Ceq ||
                !instructions[index + 2].IsStloc() || !instructions[index + 3].IsLdloc() ||
                instructions[index + 2].LocalIndex() != instructions[index + 3].LocalIndex() ||
                (instructions[index + 4].opcode != OpCodes.Brfalse && instructions[index + 4].opcode != OpCodes.Brfalse_S) ||
                instructions[index + 4].operand is not System.Reflection.Emit.Label end) { return -1; }
            int endIndex = instructions.ToList().FindIndex(code => code.labels.Contains(end));
            if (endIndex <= index + 4 || instructions.Skip(endIndex).Any(code => code.opcode != OpCodes.Nop && code.opcode != OpCodes.Ret)) { return -1; }
            match = index + 1;
        }
        return match;
    }

    internal static IEnumerable<CodeInstruction> DisablePassengerShortcut(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        var instructions = original.Select(code => new CodeInstruction(code)).ToList();
        int comparison = __originalMethod is MethodInfo method ? FindPassengerComparison(method, instructions) : -1;
        if (comparison < 0) { throw new InvalidOperationException("Unsupported RageCoop passenger shortcut comparison."); }
        // Leave chat, pause, menu and player-list processing in its original
        // order. Only the dedicated PassengerKey equality can no longer match.
        instructions[comparison].opcode = OpCodes.Call;
        instructions[comparison].operand = AccessTools.DeclaredMethod(typeof(PassengerSeatGuard), nameof(IgnorePassengerKey));
        return instructions;
    }

    private static bool IgnorePassengerKey(Keys pressed, Keys configured) => false;

    private static void ProcessEnterControl()
    {
        try
        {
            if (!API.IsOnServer)
            {
                ResetEntry();
                _suppressUntilReleased = false;
                return;
            }
            if (API.IsChatFocused || API.IsMenuVisible || _resourceInputBlocked?.Invoke() != false) { return; }
            Ped player = Game.Player.Character;
            if (!player.Exists() || player.IsDead || player.IsInVehicle())
            {
                ResetEntry();
                _suppressUntilReleased = false;
                return;
            }

            // Read the action, not a physical key: GTA's remaps, keyboard F
            // and Xbox Y all use the same path. Read disabled input as well
            // because this guard suppresses the ordinary carjack each frame.
            bool pressed = Controls.IsControlPressed(ControlType.PlayerControl, ControlAction.Enter) ||
                Controls.IsDisabledControlPressed(ControlType.PlayerControl, ControlAction.Enter);
            bool justPressed = Controls.IsControlJustPressed(ControlType.PlayerControl, ControlAction.Enter) ||
                Controls.IsDisabledControlJustPressed(ControlType.PlayerControl, ControlAction.Enter);
            if (!pressed) { _suppressUntilReleased = false; }
            if (_suppressUntilReleased)
            {
                DisableEnter();
                return;
            }

            Vehicle? entering = player.VehicleTryingToEnter;
            if (_entryVehicle != null)
            {
                bool matching = _entryPlayer == player.Handle && _entryVehicle.Exists() &&
                    (entering?.Handle == _entryVehicle.Handle ||
                        (entering == null && Stopwatch.GetTimestamp() - _entryStarted < Stopwatch.Frequency / 2));
                if (matching)
                {
                    DisableEnter();
                    Ped? occupant = _entryVehicle.GetPedOnSeat((VehicleSeat)_entrySeat);
                    if (justPressed || (occupant != null && occupant.Exists() && occupant.Handle != player.Handle) ||
                        Stopwatch.GetTimestamp() - _entryStarted >= Stopwatch.Frequency * EntryTimeoutMilliseconds / 1000)
                    {
                        player.Task.ClearAll();
                        ResetEntry();
                        _suppressUntilReleased = pressed;
                    }
                    return;
                }
                ResetEntry();
            }

            // A target chosen by GTA takes priority over the nearby fallback,
            // including aircraft with a distant model origin. Redirect a
            // driver task that started before this frame's input was disabled.
            if (entering != null && entering.Exists())
            {
                if (HasPlayerDriver(entering) && Function.Call<int>(Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER, player.Handle) == (int)VehicleSeat.Driver)
                {
                    DisableEnter();
                    player.Task.ClearAll();
                    BeginPassengerEntry(player, entering);
                }
                return;
            }
            if (!justPressed) { return; }
            Vehicle? vehicle = FindNearbyVehicle(player.Position);
            if (vehicle == null || !vehicle.Exists() || !HasPlayerDriver(vehicle)) { return; }
            DisableEnter();
            BeginPassengerEntry(player, vehicle);
        }
        catch (Exception ex) { ReportRuntimeFailure(ex); }
    }

    private static bool HasPlayerDriver(Vehicle vehicle)
    {
        if (vehicle.IsSeatFree(VehicleSeat.Driver)) { return false; }
        Ped? driver = vehicle.GetPedOnSeat(VehicleSeat.Driver);
        return driver != null && driver.Exists() && RuntimeEntityLookup.IsCurrentPlayerPed(driver.Handle);
    }

    private static Vehicle? FindNearbyVehicle(Vector3 position)
    {
        Vehicle? nearest = null;
        float nearestBody = float.PositiveInfinity;
        float nearestCenter = float.PositiveInfinity;
        // One pool lookup per press. The model's local bounds include large
        // aircraft whose origin is far from their entrance. A nearby origin
        // alone does not authorize walking towards a distant vehicle.
        foreach (Vehicle candidate in World.GetNearbyVehicles(position, 50f, Array.Empty<Model>()))
        {
            if (!candidate.Exists()) { continue; }
            candidate.Model.GetDimensions(out Vector3 minimum, out Vector3 maximum);
            Vector3 local = candidate.GetPositionOffset(position);
            float body = DistanceToBoundsSquared(local, minimum, maximum);
            float center = local.LengthSquared();
            if (body > 3f * 3f || body > nearestBody || (body == nearestBody && center >= nearestCenter)) { continue; }
            nearest = candidate;
            nearestBody = body;
            nearestCenter = center;
        }
        return nearest;
    }

    internal static float DistanceToBoundsSquared(Vector3 position, Vector3 minimum, Vector3 maximum)
    {
        if (!IsFinite(position) || !IsFinite(minimum) || !IsFinite(maximum) ||
            minimum.X >= maximum.X || minimum.Y >= maximum.Y || minimum.Z >= maximum.Z)
        {
            return float.PositiveInfinity;
        }
        float x = Math.Max(minimum.X - position.X, Math.Max(0f, position.X - maximum.X));
        float y = Math.Max(minimum.Y - position.Y, Math.Max(0f, position.Y - maximum.Y));
        float z = Math.Max(minimum.Z - position.Z, Math.Max(0f, position.Z - maximum.Z));
        return x * x + y * y + z * z;
    }

    private static bool IsFinite(Vector3 position) =>
        !float.IsNaN(position.X) && !float.IsInfinity(position.X) &&
        !float.IsNaN(position.Y) && !float.IsInfinity(position.Y) &&
        !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);

    private static void BeginPassengerEntry(Ped player, Vehicle vehicle)
    {
        int seat = FindPassengerSeat(vehicle.PassengerCapacity,
            index => Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, vehicle.Handle, index, true));
        if (seat < 0)
        {
            _suppressUntilReleased = true;
            long now = Stopwatch.GetTimestamp();
            if (_lastNoSeatNotice == 0 || now - _lastNoSeatNotice >= Stopwatch.Frequency * 2)
            {
                _lastNoSeatNotice = now;
                API.LocalChatMessage("Совместная игра", "Свободных пассажирских мест нет");
            }
            return;
        }
        _entryVehicle = vehicle;
        _entryPlayer = player.Handle;
        _entrySeat = seat;
        _entryStarted = Stopwatch.GetTimestamp();
        player.Task.EnterVehicle(vehicle, (VehicleSeat)seat, EntryTimeoutMilliseconds, 2f, (EnterVehicleFlags)0);
    }

    internal static int FindPassengerSeat(int passengerCapacity, Func<int, bool> isSeatFree)
    {
        for (int seat = 0; seat < passengerCapacity; seat++)
        {
            if (isSeatFree(seat)) { return seat; }
        }
        return -1;
    }

    private static void DisableEnter() => Controls.DisableControlActionThisFrame(ControlType.PlayerControl, ControlAction.Enter);

    private static void ResetEntry()
    {
        _entryVehicle = null;
        _entryPlayer = 0;
        _entrySeat = -1;
        _entryStarted = 0;
    }

    private static bool PreserveLocalDriver(SyncedPed __instance)
    {
        if (!API.IsOnServer || !__instance.IsPlayer || (__instance.Speed != 4 && __instance.Speed != 5)) { return true; }
        try
        {
            if (_seatField?.GetValue(__instance) is not VehicleSeat seat || seat != VehicleSeat.Driver) { return true; }
            var syncedVehicle = _currentVehicleProperty?.GetValue(__instance) as SyncedVehicle;
            Vehicle? remoteVehicle = syncedVehicle?.MainVehicle;
            Ped local = Game.Player.Character;
            Vehicle? localVehicle = local.CurrentVehicle;
            if (remoteVehicle == null || localVehicle == null || !remoteVehicle.Exists() ||
                !localVehicle.Exists() || local.SeatIndex != VehicleSeat.Driver ||
                remoteVehicle.Handle != localVehicle.Handle) { return true; }
            // A stale remote seat packet must not evict the real local driver.
            return false;
        }
        catch (Exception ex)
        {
            ReportRuntimeFailure(ex);
            return true;
        }
    }

    private static void ReportRuntimeFailure(Exception ex)
    {
        if (_reportedRuntimeFailure) { return; }
        _reportedRuntimeFailure = true;
        API.Logger.Warning("Coop Control: passenger seat guard failed: " + ex.GetType().Name);
    }
}
