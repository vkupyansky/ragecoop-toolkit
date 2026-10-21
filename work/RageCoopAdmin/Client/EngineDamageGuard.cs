using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GTA;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Core;
using Player = RageCoop.Client.Player;

namespace RageCoopAdmin.Client;

// A clean authoritative damage bit must also restore a part that was broken by
// an earlier packet. Apply each transition once; unchanged snapshots never repair
// new local collision damage every frame. Keep the remote resurrection grace
// period on the game thread and revoke it whenever the original car/owner leaves.
internal static class EngineDamageGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginedamage";
    private static readonly Type? DamageType = typeof(Logger).Assembly.GetType("RageCoop.Core.VehicleDamageModel", false);
    private static readonly PropertyInfo? BrokenDoors = DamageType?.GetProperty("BrokenDoors");
    private static readonly PropertyInfo? BrokenWindows = DamageType?.GetProperty("BrokenWindows");
    private static readonly PropertyInfo? BurstedTires = DamageType?.GetProperty("BurstedTires");
    private static ConditionalWeakTable<Vehicle, AppliedDamage> _applied = new ConditionalWeakTable<Vehicle, AppliedDamage>();
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static ConditionalWeakTable<SyncedVehicle, PendingRepair> _pendingRepairs = new ConditionalWeakTable<SyncedVehicle, PendingRepair>();
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static Dictionary<int, SyncedVehicle>? _vehicles;
    private static Dictionary<int, Player>? _players;
    private static FieldInfo? _localPlayer;
    private static object? _vehiclesLock;
    private static Func<SyncedEntity, Player>? _readOwner;
    private static Func<SyncedVehicle, bool>? _readDead;
    private static Harmony? _harmony;

    internal static MethodInfo? FindUpdateTarget() => typeof(SyncedVehicle).GetMethod("Update", Instance, null, Type.EmptyTypes, null);
    internal static MethodInfo? FindOwnerTarget() => typeof(SyncedEntity).GetProperty("OwnerID", Instance)?.GetSetMethod(true);
    internal static MethodInfo? FindCaptureTarget() => typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.VehicleExtensions", false)?
        .GetMethod("GetVehicleDamageModel", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vehicle) }, null);

    internal static MethodInfo? FindTarget()
    {
        Type? damage = DamageType;
        return damage == null ? null : typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.VehicleExtensions", false)?
            .GetMethod("SetDamageModel", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Vehicle), damage, typeof(bool) }, null);
    }

    internal static bool HasSupportedContract(MethodInfo target)
    {
        List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(target);
        bool Calls(Type? type, string name) => instructions.Count(code => code.operand is MethodInfo method && method.DeclaringType == type && method.Name == name) == 1;
        return target.ReturnType == typeof(void) && DamageType?.IsValueType == true &&
            BrokenDoors?.PropertyType == typeof(byte) && BrokenWindows?.PropertyType == typeof(byte) &&
            Calls(DamageType, "get_BrokenDoors") && Calls(DamageType, "get_BrokenWindows") &&
            Calls(typeof(VehicleDoor), nameof(VehicleDoor.Break)) && Calls(typeof(VehicleWindow), nameof(VehicleWindow.Smash)) &&
            !instructions.Any(code => code.operand is MethodInfo method && method.Name == "Repair");
    }

    internal static bool Install()
    {
        if (_harmony != null) { return true; }
        MethodInfo? target = FindTarget(), capture = FindCaptureTarget();
        MethodInfo? update = FindUpdateTarget(), owner = FindOwnerTarget();
        Type? pool = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", false);
        Type? players = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.PlayerList", false);
        Type? host = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Main", false);
        FieldInfo? vehicles = pool?.GetField("VehiclesByID", Static), playerMap = players?.GetField("Players", Static),
            vehiclesLock = pool?.GetField("VehiclesLock", Static), localPlayer = host?.GetField("LocalPlayerID", Static);
        MethodInfo? readOwner = typeof(SyncedEntity).GetProperty("Owner", Instance)?.GetGetMethod(true),
            readDead = typeof(SyncedVehicle).GetProperty("IsDead", Instance)?.GetGetMethod(true);
        if (target == null || !HasSupportedContract(target) || capture == null || !HasSupportedTyreContract(capture, target) ||
            update?.ReturnType != typeof(void) || owner?.ReturnType != typeof(void) ||
            !owner.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(int) }) ||
            !TryFindDeferredRepair(PatchProcessor.GetOriginalInstructions(update), out _) ||
            vehicles?.FieldType != typeof(Dictionary<int, SyncedVehicle>) || playerMap?.FieldType != typeof(Dictionary<int, Player>) ||
            vehiclesLock?.FieldType != typeof(object) || localPlayer?.FieldType != typeof(int) ||
            readOwner?.ReturnType != typeof(Player) || readDead?.ReturnType != typeof(bool)) { return false; }
        _vehicles = (Dictionary<int, SyncedVehicle>)vehicles.GetValue(null)!;
        _players = (Dictionary<int, Player>)playerMap.GetValue(null)!;
        _vehiclesLock = vehiclesLock.GetValue(null)!;
        _localPlayer = localPlayer;
        _readOwner = (Func<SyncedEntity, Player>)Delegate.CreateDelegate(typeof(Func<SyncedEntity, Player>), readOwner);
        _readDead = (Func<SyncedVehicle, bool>)Delegate.CreateDelegate(typeof(Func<SyncedVehicle, bool>), readDead);
        var harmony = new Harmony(PatchId);
        try
        {
            Targets.Add(target);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(EngineDamageGuard), nameof(BeforeDamage)),
                postfix: new HarmonyMethod(typeof(EngineDamageGuard), nameof(AfterDamage)),
                transpiler: new HarmonyMethod(typeof(EngineDamageGuard), nameof(ReplaceTyreShift)));
            Targets.Add(capture);
            harmony.Patch(capture, transpiler: new HarmonyMethod(typeof(EngineDamageGuard), nameof(ReplaceTyreShift)));
            Targets.Add(update);
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(EngineDamageGuard), nameof(ReplaceDeferredRepair)),
                postfix: new HarmonyMethod(typeof(EngineDamageGuard), nameof(CompletePendingRepair)));
            Targets.Add(owner);
            harmony.Patch(owner, prefix: new HarmonyMethod(typeof(EngineDamageGuard), nameof(CancelOwnerRepair)));
        }
        catch
        {
            foreach (MethodInfo method in Targets) { harmony.Unpatch(method, HarmonyPatchType.All, PatchId); }
            Targets.Clear();
            throw;
        }
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
        {
            foreach (MethodInfo method in Targets) { _harmony.Unpatch(method, HarmonyPatchType.All, PatchId); }
        }
        Targets.Clear();
        _harmony = null;
        _applied = new ConditionalWeakTable<Vehicle, AppliedDamage>();
        _pendingRepairs = new ConditionalWeakTable<SyncedVehicle, PendingRepair>();
        _vehicles = null;
        _players = null;
        _vehiclesLock = null;
        _localPlayer = null;
        _readOwner = null;
        _readDead = null;
    }

    internal static bool HasSupportedTyreContract(MethodInfo capture, MethodInfo apply)
    {
        int[] boneIds = Enum.GetValues(typeof(VehicleWheelBoneId)).Cast<VehicleWheelBoneId>().Select(value => (int)value).OrderBy(value => value).ToArray();
        bool Bone(int id, string name) => Enum.GetName(typeof(VehicleWheelBoneId), id) == name;
        return boneIds.SequenceEqual(new[] { -1, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }) &&
            Bone(-1, nameof(VehicleWheelBoneId.Invalid)) &&
            Bone(11, nameof(VehicleWheelBoneId.WheelLeftFront)) && Bone(12, nameof(VehicleWheelBoneId.WheelRightFront)) &&
            Bone(13, nameof(VehicleWheelBoneId.WheelLeftRear)) && Bone(14, nameof(VehicleWheelBoneId.WheelRightRear)) &&
            Bone(15, nameof(VehicleWheelBoneId.WheelLeftMiddle1)) && Bone(16, nameof(VehicleWheelBoneId.WheelRightMiddle1)) &&
            Bone(17, nameof(VehicleWheelBoneId.WheelLeftMiddle2)) && Bone(18, nameof(VehicleWheelBoneId.WheelRightMiddle2)) &&
            Bone(19, nameof(VehicleWheelBoneId.WheelLeftMiddle3)) && Bone(20, nameof(VehicleWheelBoneId.WheelRightMiddle3)) &&
            capture.IsStatic && capture.ReturnType == DamageType &&
            capture.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Vehicle) }) &&
            apply.IsStatic && apply.ReturnType == typeof(void) &&
            apply.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Vehicle), DamageType!, typeof(bool) }) &&
            BurstedTires?.PropertyType == typeof(short) && BurstedTires.CanRead && BurstedTires.CanWrite &&
            TryFindTyreShift(PatchProcessor.GetOriginalInstructions(capture), true, out _) &&
            TryFindTyreShift(PatchProcessor.GetOriginalInstructions(apply), false, out _);
    }

    internal static bool TryFindTyreShift(IList<CodeInstruction> codes, bool capture, out int at)
    {
        at = -1;
        int[] reads = Enumerable.Range(0, codes.Count).Where(index => codes[index].operand is MethodInfo method &&
            method.DeclaringType == typeof(VehicleWheel) && method.Name == "get_BoneId").ToArray();
        if (reads.Length != 1) { return false; }
        int index = reads[0];
        MethodInfo getter = (MethodInfo)codes[index].operand;
        bool OneCall(Type? type, string name) => codes.Count(code => code.operand is MethodInfo method && method.DeclaringType == type && method.Name == name) == 1;
        if (getter.ReturnType != typeof(VehicleWheelBoneId) || getter.GetParameters().Length != 0 || getter.IsStatic ||
            index < 2 || index + 6 >= codes.Count || codes[index].opcode != OpCodes.Callvirt ||
            codes[index - 2].opcode != OpCodes.Ldc_I4_1 || !codes[index - 1].IsLdloc() ||
            codes[index + 1].opcode != OpCodes.Ldc_I4_S || !Equals(codes[index + 1].operand, (sbyte)31) ||
            codes[index + 2].opcode != OpCodes.And || codes[index + 3].opcode != OpCodes.Shl ||
            codes[index + 4].opcode != OpCodes.Conv_I2 || codes[index + 5].opcode != (capture ? OpCodes.Or : OpCodes.And) ||
            codes[index + 6].opcode != (capture ? OpCodes.Conv_I2 : OpCodes.Ldc_I4_0) ||
            Enumerable.Range(index, 7).Any(offset => codes[offset].labels.Count != 0 || codes[offset].blocks.Count != 0) ||
            !OneCall(DamageType, capture ? "set_BurstedTires" : "get_BurstedTires") ||
            !OneCall(typeof(VehicleWheelCollection), capture ? "GetAllWheels" : "GetEnumerator") ||
            (capture ? !OneCall(typeof(VehicleWheel), "get_IsBursted") :
                !OneCall(typeof(VehicleWheel), "Puncture") || !OneCall(typeof(VehicleWheel), "Burst") || !OneCall(typeof(VehicleWheel), "Fix"))) { return false; }
        at = index;
        return true;
    }

    internal static IEnumerable<CodeInstruction> ReplaceTyreShift(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        bool capture = __originalMethod == FindCaptureTarget();
        if ((!capture && __originalMethod != FindTarget()) || !TryFindTyreShift(codes, capture, out int at))
        {
            throw new InvalidOperationException("Unsupported RageCoop vehicle tyre mask.");
        }
        // Retain all five representable upstream bits, including signed bit 15.
        // Bone IDs 16–20 use the previously unused low bits of the same Int16.
        codes[at].opcode = OpCodes.Call;
        codes[at].operand = typeof(EngineDamageGuard).GetMethod(nameof(TyreBitIndex), Static)!;
        return codes;
    }

    private static int TyreBitIndex(VehicleWheel wheel)
    {
        int bone = (int)wheel.BoneId;
        if (bone >= 11 && bone <= 15) { return bone; }
        if (bone >= 16 && bone <= 20) { return bone - 16; }
        throw new InvalidOperationException("Unsupported SHVDN vehicle wheel bone ID.");
    }

    internal static bool TryFindDeferredRepair(IList<CodeInstruction> codes, out int start)
    {
        start = -1;
        int[] delays = Enumerable.Range(0, codes.Count).Where(index => codes[index].operand is MethodInfo method &&
            method.DeclaringType?.FullName == "RageCoop.Client.Main" && method.Name == "Delay").ToArray();
        if (delays.Length != 1) { return false; }
        MethodInfo delayMethod = (MethodInfo)codes[delays[0]].operand;
        if (!delayMethod.IsStatic || delayMethod.ReturnType != typeof(void) ||
            !delayMethod.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Action), typeof(int) })) { return false; }
        int at = delays[0] - 4;
        if (at < 0 || codes[at].opcode != OpCodes.Ldarg_0 || codes[at + 1].opcode != OpCodes.Ldftn ||
            codes[at + 1].operand is not MethodInfo callback || callback.DeclaringType != typeof(SyncedVehicle) ||
            callback.IsStatic || callback.ReturnType != typeof(void) || callback.GetParameters().Length != 0 ||
            codes[at + 2].opcode != OpCodes.Newobj || codes[at + 2].operand is not ConstructorInfo constructor ||
            constructor.DeclaringType != typeof(Action) || codes[at + 3].opcode != OpCodes.Ldc_I4 ||
            codes[at + 3].operand is not int delay || delay != 1000 ||
            Enumerable.Range(at + 1, 4).Any(index => codes[index].labels.Count != 0 || codes[index].blocks.Count != 0)) { return false; }
        List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(callback);
        bool OneCall(Type type, string name) => body.Count(code => code.operand is MethodInfo method && method.DeclaringType == type && method.Name == name) == 1;
        if (!OneCall(typeof(Vehicle), "Repair") || !OneCall(typeof(SyncedVehicle), "get_IsDead") || !OneCall(typeof(Entity), "get_IsDead")) { return false; }
        start = at;
        return true;
    }

    internal static IEnumerable<CodeInstruction> ReplaceDeferredRepair(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        if (!TryFindDeferredRepair(codes, out int at)) { throw new InvalidOperationException("Unsupported RageCoop deferred vehicle repair."); }
        // Keep one pending grace period on the game thread. The installed Delay
        // starts another sleeping task for every snapshot and captures mutable
        // state that can outlive the remote vehicle or resource session.
        codes.RemoveRange(at + 1, 4);
        codes.Insert(at + 1, new CodeInstruction(OpCodes.Call,
            typeof(EngineDamageGuard).GetMethod(nameof(TrackPendingRepair), Static)!));
        return codes;
    }

    private static void TrackPendingRepair(SyncedVehicle vehicle)
    {
        lock (_vehiclesLock!)
        {
            if (_pendingRepairs.TryGetValue(vehicle, out PendingRepair? pending))
            {
                if (MatchesCurrentRemote(vehicle, pending)) { return; }
                _pendingRepairs.Remove(vehicle);
            }
            var current = new PendingRepair(vehicle, _readOwner!(vehicle));
            if (MatchesCurrentRemote(vehicle, current)) { _pendingRepairs.Add(vehicle, current); }
        }
    }

    private static bool MatchesCurrentRemote(SyncedVehicle vehicle, PendingRepair pending)
    {
        int local = (int)_localPlayer!.GetValue(null)!;
        return local != 0 && vehicle.OwnerID != local && vehicle.ID == pending.ID && vehicle.OwnerID == pending.OwnerID &&
            ReferenceEquals(vehicle.MainVehicle, pending.Vehicle) && ReferenceEquals(_readOwner!(vehicle), pending.Owner) &&
            _vehicles!.TryGetValue(pending.ID, out SyncedVehicle current) && ReferenceEquals(current, vehicle) &&
            _players!.TryGetValue(pending.OwnerID, out Player owner) && ReferenceEquals(owner, pending.Owner);
    }

    private static void CompletePendingRepair(SyncedVehicle __instance)
    {
        if (!_pendingRepairs.TryGetValue(__instance, out _)) { return; }
        lock (_vehiclesLock!)
        {
            if (!_pendingRepairs.TryGetValue(__instance, out PendingRepair? pending)) { return; }
            if (!MatchesCurrentRemote(__instance, pending) || _readDead!(__instance) || !pending.Vehicle.Exists() || !pending.Vehicle.IsDead)
            {
                _pendingRepairs.Remove(__instance);
                return;
            }
            if (Stopwatch.GetTimestamp() - pending.Started < Stopwatch.Frequency) { return; }
            _pendingRepairs.Remove(__instance);
            pending.Vehicle.Repair();
        }
    }

    private static void CancelOwnerRepair(SyncedEntity __instance, int __0)
    {
        if (__instance is SyncedVehicle vehicle && __0 != vehicle.OwnerID) { _pendingRepairs.Remove(vehicle); }
    }

    private sealed class PendingRepair
    {
        internal readonly int ID;
        internal readonly int OwnerID;
        internal readonly Player Owner;
        internal readonly Vehicle Vehicle;
        internal readonly long Started = Stopwatch.GetTimestamp();

        internal PendingRepair(SyncedVehicle vehicle, Player owner)
        {
            ID = vehicle.ID;
            OwnerID = vehicle.OwnerID;
            Owner = owner;
            Vehicle = vehicle.MainVehicle;
        }
    }

    internal static byte RestoredBits(byte previous, byte current) => (byte)(previous & ~current);

    private static void BeforeDamage(Vehicle __0, object __1, out DamageApplication __state)
    {
        AppliedDamage applied = _applied.GetValue(__0, _ => new AppliedDamage());
        byte doors = (byte)BrokenDoors!.GetValue(__1)!, windows = (byte)BrokenWindows!.GetValue(__1)!;
        __state = new DamageApplication { Applied = applied, BrokenDoors = doors, BrokenWindows = windows };
        if (__0.IsDead) { return; }
        byte previousDoors = applied.HasSnapshot ? applied.BrokenDoors : ReadBrokenDoors(__0);
        byte previousWindows = applied.HasSnapshot ? applied.BrokenWindows : ReadBrokenWindows(__0);
        byte restoredDoors = RestoredBits(previousDoors, doors);
        byte restoredWindows = RestoredBits(previousWindows, windows);
        if (restoredDoors != 0 && (ReadBrokenDoors(__0) & restoredDoors) != 0)
        {
            // GTA cannot replace an individual detached door. Rebuild the parts
            // once, preserve running condition, then let the original method
            // reapply every part that remains broken/open in this snapshot.
            float engine = __0.EngineHealth, body = __0.BodyHealth, tank = __0.PetrolTankHealth, dirt = __0.DirtLevel;
            bool running = __0.IsEngineRunning;
            try { __0.Repair(); }
            finally
            {
                __0.EngineHealth = engine;
                __0.BodyHealth = body;
                __0.PetrolTankHealth = tank;
                __0.DirtLevel = dirt;
                __0.IsEngineRunning = running;
            }
            return;
        }
        for (int index = 0; index < 8; index++)
        {
            int bit = 1 << index;
            // Only windows 0–3 belong to the same numbered doors. Door bits
            // 4/5 are bonnet/boot and cannot prevent middle-window repair.
            if ((restoredWindows & bit) != 0 && (index >= 4 || (doors & bit) == 0))
            {
                VehicleWindow window = __0.Windows[(VehicleWindowIndex)index];
                if (!window.IsIntact) { window.Repair(); }
            }
        }
    }

    private static void AfterDamage(Vehicle __0, DamageApplication __state)
    {
        // The original loop also skips smashing a window when its numbered
        // body panel is detached. Restore the independent non-door windows.
        int skippedWindows = __state.BrokenWindows & __state.BrokenDoors & 0xf0;
        for (int index = 4; index < 8; index++)
        {
            if ((skippedWindows & (1 << index)) == 0) { continue; }
            VehicleWindow window = __0.Windows[(VehicleWindowIndex)index];
            if (window.IsIntact) { window.Smash(); }
        }
        __state.Applied.BrokenDoors = __state.BrokenDoors;
        __state.Applied.BrokenWindows = __state.BrokenWindows;
        __state.Applied.HasSnapshot = true;
    }

    private static byte ReadBrokenDoors(Vehicle vehicle)
    {
        byte bits = 0;
        for (int index = 0; index < 8; index++)
        {
            if (vehicle.Doors[(VehicleDoorIndex)index].IsBroken) { bits |= (byte)(1 << index); }
        }
        return bits;
    }

    private static byte ReadBrokenWindows(Vehicle vehicle)
    {
        byte bits = 0;
        for (int index = 0; index < 8; index++)
        {
            if (!vehicle.Windows[(VehicleWindowIndex)index].IsIntact) { bits |= (byte)(1 << index); }
        }
        return bits;
    }

    private sealed class AppliedDamage
    {
        internal bool HasSnapshot;
        internal byte BrokenDoors;
        internal byte BrokenWindows;
    }

    private struct DamageApplication
    {
        internal AppliedDamage Applied;
        internal byte BrokenDoors;
        internal byte BrokenWindows;
    }
}
