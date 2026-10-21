using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// Correct the installed engine's destructive traffic filter and lifecycle gates.
// Keep discovery, interpolation, population rules and packet formats in RageCoop.
internal static class EngineTrafficGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginetraffic";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type? Pool = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", false);
    private static readonly Type? Network = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Networking", false);
    private static readonly Dictionary<int, SyncedVehicle>? VehicleHandles = PoolField<Dictionary<int, SyncedVehicle>>("VehiclesByHandle");
    private static readonly Dictionary<int, SyncedPed>? PedHandles = PoolField<Dictionary<int, SyncedPed>>("PedsByHandle");
    private static readonly Dictionary<int, SyncedPed>? Peds = PoolField<Dictionary<int, SyncedPed>>("PedsByID");
    private static readonly object? VehicleLock = PoolField<object>("VehiclesLock");
    private static readonly object? PedLock = PoolField<object>("PedsLock");
    private static readonly Func<SyncedEntity, Model>? ReadModel = BindEntityGetter<Model>("Model");
    private static readonly Func<SyncedEntity, Stopwatch>? ReadLastSent = BindEntityGetter<Stopwatch>("LastSentStopWatch");
    private static readonly FieldInfo? SyncInterval = Network?.GetField("SyncInterval", Static);
    private static ConditionalWeakTable<SyncedEntity, FullSnapshotSchedule> _fullSchedules = new ConditionalWeakTable<SyncedEntity, FullSnapshotSchedule>();
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static Harmony? _harmony;

    internal static MethodInfo? FindDoSync() => Pool?.GetMethod("DoSync", Static, null, Type.EmptyTypes, null);
    internal static MethodInfo? FindCreateVehicle() => typeof(SyncedVehicle).GetMethod("CreateVehicle", Instance, null, Type.EmptyTypes, null);
    internal static MethodInfo? FindAdd(Type entityType) => Pool?.GetMethod("Add", Static, null, new[] { entityType }, null);
    internal static MethodInfo? FindSend(Type entityType) => Network?.GetMethod(entityType == typeof(SyncedPed) ? "SendPed" : "SendVehicle",
        Static, null, new[] { entityType, typeof(bool) }, null);

    internal static bool Install()
    {
        if (_harmony != null) { return true; }
        MethodInfo? sync = FindDoSync(), create = FindCreateVehicle();
        MethodInfo? addPed = FindAdd(typeof(SyncedPed)), addVehicle = FindAdd(typeof(SyncedVehicle));
        MethodInfo? sendPed = FindSend(typeof(SyncedPed)), sendVehicle = FindSend(typeof(SyncedVehicle));
        if (sync == null || create?.ReturnType != typeof(bool) || addPed == null || addVehicle == null ||
            sendPed == null || sendVehicle == null || VehicleHandles == null || PedHandles == null || Peds == null ||
            VehicleLock == null || PedLock == null || ReadModel == null || ReadLastSent == null || SyncInterval?.FieldType != typeof(int) ||
            !HasCullGates(PatchProcessor.GetOriginalInstructions(sync), sync) ||
            !TryFindNearbyBlock(PatchProcessor.GetOriginalInstructions(create), out _, out _) ||
            !HasSendGate(sendPed) || !HasSendGate(sendVehicle))
        {
            return false;
        }

        var harmony = new Harmony(PatchId);
        try
        {
            Patch(harmony, sync, transpiler: nameof(ProtectTraffic));
            Patch(harmony, create, prefix: nameof(BeforeCreateVehicle), transpiler: nameof(RemoveNearbyReplacement));
            Patch(harmony, addPed, prefix: nameof(BeforeAddPed));
            Patch(harmony, addVehicle, prefix: nameof(BeforeAddVehicle));
            Patch(harmony, sendPed, prefix: nameof(BeforeSendPed), postfix: nameof(AfterSendPed));
            Patch(harmony, sendVehicle, prefix: nameof(BeforeSendVehicle), postfix: nameof(AfterSendVehicle));
        }
        catch
        {
            foreach (MethodInfo target in Targets) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
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
            foreach (MethodInfo target in Targets) { _harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        }
        Targets.Clear();
        _harmony = null;
        _fullSchedules = new ConditionalWeakTable<SyncedEntity, FullSnapshotSchedule>();
    }

    private static void Patch(Harmony harmony, MethodInfo target, string? prefix = null, string? postfix = null, string? transpiler = null)
    {
        Targets.Add(target);
        HarmonyMethod? Method(string? name) => name == null ? null : new HarmonyMethod(typeof(EngineTrafficGuard), name);
        harmony.Patch(target, prefix: Method(prefix), postfix: Method(postfix), transpiler: Method(transpiler));
    }

    internal static bool HasCullGates(IList<CodeInstruction> instructions, MethodBase target) =>
        FindCullGate(instructions, target, "WorldPedSoftLimit", typeof(Ped), out _) >= 0 &&
        FindCullGate(instructions, target, "WorldVehicleSoftLimit", typeof(Vehicle), out _) >= 0;

    private static int FindCullGate(IList<CodeInstruction> instructions, MethodBase target, string setting, Type candidateType, out int candidate)
    {
        candidate = -1;
        if (instructions.Count < 2) { return -1; }
        int[] matches = Enumerable.Range(0, instructions.Count - 1).Where(index =>
            instructions[index].operand is MethodInfo getter && getter.DeclaringType?.FullName == "RageCoop.Client.Settings" &&
            getter.Name == "get_" + setting && instructions[index + 1].opcode == OpCodes.Cgt).ToArray();
        if (matches.Length != 1) { return -1; }
        int comparison = matches[0] + 1;
        for (int index = comparison + 1; index < Math.Min(comparison + 12, instructions.Count); index++)
        {
            if (instructions[index].operand is not MethodInfo getter || getter.DeclaringType != typeof(Entity) || getter.Name != "get_PopulationType") { continue; }
            if (!instructions[index - 1].IsLdloc()) { return -1; }
            candidate = instructions[index - 1].LocalIndex();
            IList<LocalVariableInfo>? locals = target.GetMethodBody()?.LocalVariables;
            return locals != null && candidate >= 0 && candidate < locals.Count && locals[candidate].LocalType == candidateType ? comparison : -1;
        }
        return -1;
    }

    internal static IEnumerable<CodeInstruction> ProtectTraffic(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        List<CodeInstruction> instructions = original.Select(code => new CodeInstruction(code)).ToList();
        int ped = FindCullGate(instructions, __originalMethod, "WorldPedSoftLimit", typeof(Ped), out int pedLocal);
        int vehicle = FindCullGate(instructions, __originalMethod, "WorldVehicleSoftLimit", typeof(Vehicle), out int vehicleLocal);
        if (ped < 0 || vehicle < 0) { throw new InvalidOperationException("Unsupported RageCoop traffic culling gates."); }
        foreach (var gate in new[] { (Index: ped, Local: pedLocal, Method: nameof(CanCullPed)), (Index: vehicle, Local: vehicleLocal, Method: nameof(CanCullVehicle)) }
            .OrderByDescending(gate => gate.Index))
        {
            instructions.InsertRange(gate.Index + 1, new[] { CodeInstruction.LoadLocal(gate.Local), CodeInstruction.Call(typeof(EngineTrafficGuard), gate.Method) });
        }
        return instructions;
    }

    private static bool CanCullPed(bool overLimit, Ped ped) => overLimit && ped.Exists() &&
        EngineTrafficPolicy.CanCull(true, ped.IsOnScreen, ped.IsPersistent, IsPlayer(ped), NearestPlayerDistanceSquared(ped.Position));

    private static bool CanCullVehicle(bool overLimit, Vehicle vehicle)
    {
        if (!overLimit || !vehicle.Exists()) { return false; }
        bool playerOccupant = vehicle.Occupants.Any(IsPlayer);
        return EngineTrafficPolicy.CanCull(true, vehicle.IsOnScreen, vehicle.IsPersistent, playerOccupant, NearestPlayerDistanceSquared(vehicle.Position));
    }

    private static bool IsPlayer(Ped ped)
    {
        if (ped.IsPlayer || ped.Handle == Game.Player.Character.Handle) { return true; }
        lock (PedLock!) { return PedHandles!.TryGetValue(ped.Handle, out SyncedPed synced) && synced.IsPlayer; }
    }

    private static float NearestPlayerDistanceSquared(Vector3 position)
    {
        float distance = position.DistanceToSquared(Game.Player.Character.Position);
        lock (PedLock!)
        {
            foreach (SyncedPed synced in Peds!.Values)
            {
                if (synced.IsPlayer && synced.MainPed != null && synced.MainPed.Exists())
                {
                    distance = Math.Min(distance, position.DistanceToSquared(synced.MainPed.Position));
                }
            }
        }
        return distance;
    }

    private static bool BeforeCreateVehicle(SyncedVehicle __instance, ref bool __result)
    {
        __result = false;
        if (__instance.LastFullSynced == 0) { return false; }
        Model model = ReadModel!(__instance);
        if (!model.IsInCdImage || !model.IsVehicle) { return false; }
        if (model.IsLoaded) { return true; }
        model.Request();
        return false;
    }

    internal static bool TryFindNearbyBlock(IList<CodeInstruction> instructions, out int start, out int end)
    {
        start = end = -1;
        if (instructions.Count < 50 || instructions[0].opcode != OpCodes.Nop || instructions[1].opcode != OpCodes.Ldarg_0 ||
            instructions[2].operand is not MethodInfo position || position.DeclaringType != typeof(SyncedEntity) || position.Name != "get_Position" ||
            instructions[3].opcode != OpCodes.Ldc_R4 || !Equals(instructions[3].operand, 2f) ||
            instructions[5].operand is not MethodInfo nearby || nearby.DeclaringType != typeof(World) || nearby.Name != "GetNearbyVehicles")
        {
            return false;
        }
        for (int index = 6; index + 1 < instructions.Count; index++)
        {
            if (instructions[index].operand is not MethodInfo getter || getter.DeclaringType != typeof(SyncedVehicle) ||
                getter.Name != "get_MainVehicle" || instructions[index + 1].opcode != OpCodes.Dup) { continue; }
            start = 1;
            end = index - 1;
            CodeInstruction[] block = instructions.Skip(start).Take(end - start).ToArray();
            return end - start == 42 && instructions[end].opcode == OpCodes.Ldarg_0 && block.All(code => code.blocks.Count == 0) &&
                block.Count(code => code.operand is MethodInfo method && method.DeclaringType == Pool && method.Name == "RemoveVehicle") == 1 &&
                block.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(PoolObject) && method.Name == "Delete") == 1;
        }
        return false;
    }

    internal static IEnumerable<CodeInstruction> RemoveNearbyReplacement(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> instructions = original.Select(code => new CodeInstruction(code)).ToList();
        if (!TryFindNearbyBlock(instructions, out int start, out int end))
        {
            throw new InvalidOperationException("Unsupported RageCoop nearby vehicle replacement block.");
        }
        instructions.RemoveRange(start, end - start);
        return instructions;
    }

    private static void BeforeAddPed(SyncedPed __0)
    {
        lock (PedLock!) { RemoveStaleHandles(PedHandles!, __0, __0.MainPed?.Handle); }
    }

    private static void BeforeAddVehicle(SyncedVehicle __0)
    {
        lock (VehicleLock!) { RemoveStaleHandles(VehicleHandles!, __0, __0.MainVehicle?.Handle); }
    }

    internal static void RemoveStaleHandles<T>(Dictionary<int, T> handles, T entity, int? currentHandle) where T : class
    {
        int[] stale = handles.Where(pair => ReferenceEquals(pair.Value, entity) && pair.Key != currentHandle).Select(pair => pair.Key).ToArray();
        foreach (int handle in stale) { handles.Remove(handle); }
    }

    private static bool HasSendGate(MethodInfo target)
    {
        List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(target);
        int gate = instructions.FindIndex(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Stopwatch) && method.Name == "get_ElapsedMilliseconds");
        return gate >= 0 && gate + 1 < instructions.Count && instructions[gate + 1].LoadsField(SyncInterval) &&
            instructions.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Stopwatch) && method.Name == "Restart") == 1 &&
            instructions.Count(code => code.operand is MethodInfo method && method.DeclaringType == Network && method.Name == "SendSync") == 1;
    }

    private static void BeforeSendPed(SyncedPed __0, ref bool __1, out SendAttempt __state) => PrepareSend(__0, ref __1, out __state);
    private static void BeforeSendVehicle(SyncedVehicle __0, ref bool __1, out SendAttempt __state) => PrepareSend(__0, ref __1, out __state);
    private static void AfterSendPed(SyncedPed __0, SendAttempt __state) => FinishSend(__0, __state);
    private static void AfterSendVehicle(SyncedVehicle __0, SendAttempt __state) => FinishSend(__0, __state);

    private static void PrepareSend(SyncedEntity entity, ref bool full, out SendAttempt attempt)
    {
        attempt = default;
        full = false;
        if (ReadLastSent!(entity).ElapsedMilliseconds < (int)SyncInterval!.GetValue(null)!) { return; }
        FullSnapshotSchedule schedule = _fullSchedules.GetValue(entity, _ => new FullSnapshotSchedule());
        bool requested = entity.SendFullNextFrame;
        full = schedule.IsDue(Stopwatch.GetTimestamp(), Stopwatch.Frequency / 2, requested);
        attempt = new SendAttempt { Schedule = schedule, Full = full, Requested = requested };
    }

    private static void FinishSend(SyncedEntity entity, SendAttempt attempt)
    {
        if (!attempt.Full) { return; }
        attempt.Schedule!.Sent(Stopwatch.GetTimestamp());
        if (attempt.Requested) { entity.SendFullNextFrame = false; }
    }

    private struct SendAttempt
    {
        internal FullSnapshotSchedule? Schedule;
        internal bool Full;
        internal bool Requested;
    }

    private static T? PoolField<T>(string name) where T : class => Pool?.GetField(name, Static)?.GetValue(null) as T;

    private static Func<SyncedEntity, T>? BindEntityGetter<T>(string name)
    {
        MethodInfo? getter = typeof(SyncedEntity).GetProperty(name, Instance)?.GetGetMethod(true);
        return getter?.ReturnType == typeof(T) ? (Func<SyncedEntity, T>)getter.CreateDelegate(typeof(Func<SyncedEntity, T>)) : null;
    }
}
