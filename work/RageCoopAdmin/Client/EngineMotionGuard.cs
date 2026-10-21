using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// Retain RageCoop's motion model and packet format. A positional deadband must
// not discard a changed speed, and airborne positions use the same packet age
// prediction as ordinary movement instead of an unconditional second of travel.
// Bound only the positional velocity correction to the remaining frame error.
internal static class EngineMotionGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginemotion";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo? VelocityGetter = typeof(SyncedEntity).GetProperty("Velocity", Instance)?.GetGetMethod(true);
    private static readonly MethodInfo? PositionGetter = typeof(SyncedEntity).GetProperty("Position", Instance)?.GetGetMethod(true);
    private static readonly MethodInfo? Predict = typeof(SyncedEntity).GetMethod("Predict", Instance, null, new[] { typeof(Vector3) }, null);
    private static readonly Func<SyncedEntity, Vector3>? ReadVelocity = VelocityGetter == null ? null :
        (Func<SyncedEntity, Vector3>)Delegate.CreateDelegate(typeof(Func<SyncedEntity, Vector3>), VelocityGetter);
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static Harmony? _harmony;

    internal static MethodInfo? FindVehicleTarget() => typeof(SyncedVehicle).GetMethod("DisplayVehicle", Instance, null, Type.EmptyTypes, null);
    internal static MethodInfo? FindPedTarget() => typeof(SyncedPed).GetMethod("DisplayOnFoot", Instance, null, Type.EmptyTypes, null);
    internal static MethodInfo? FindSmoothTarget() => typeof(SyncedPed).GetMethod("SmoothTransition", Instance, null, Type.EmptyTypes, null);

    internal static bool Install()
    {
        if (_harmony != null) { return true; }
        MethodInfo? vehicle = FindVehicleTarget(), ped = FindPedTarget(), smooth = FindSmoothTarget();
        if (vehicle?.ReturnType != typeof(void) || ped?.ReturnType != typeof(void) || smooth?.ReturnType != typeof(void) || ReadVelocity == null ||
            PositionGetter?.ReturnType != typeof(Vector3) || Predict?.ReturnType != typeof(Vector3) ||
            !TryFindVelocityGate(PatchProcessor.GetOriginalInstructions(vehicle), vehicle, out _, out _, out _, out _) ||
            FindAirborneTargets(PatchProcessor.GetOriginalInstructions(ped)).Length != 2 ||
            !TryFindPedCorrection(PatchProcessor.GetOriginalInstructions(smooth), smooth, out _, out _))
        {
            return false;
        }
        var harmony = new Harmony(PatchId);
        try
        {
            Targets.Add(vehicle);
            harmony.Patch(vehicle, transpiler: new HarmonyMethod(typeof(EngineMotionGuard), nameof(ApplyVehicleVelocity)));
            Targets.Add(ped);
            harmony.Patch(ped, transpiler: new HarmonyMethod(typeof(EngineMotionGuard), nameof(PredictAirbornePosition)));
            Targets.Add(smooth);
            harmony.Patch(smooth, transpiler: new HarmonyMethod(typeof(EngineMotionGuard), nameof(LimitPedCorrection)));
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
    }

    internal static bool TryFindVelocityGate(IList<CodeInstruction> codes, MethodBase target,
        out int start, out int end, out int distance, out int correction)
    {
        start = end = distance = correction = -1;
        int[] constants = Enumerable.Range(0, codes.Count).Where(index => codes[index].opcode == OpCodes.Ldc_R8 &&
            codes[index].operand is double value && value == 0.03d).ToArray();
        if (constants.Length != 1) { return false; }
        int at = constants[0] - 2;
        if (at < 0 || at + 18 >= codes.Count || !codes[at].IsLdloc() || codes[at + 1].opcode != OpCodes.Conv_R8 ||
            codes[at + 3].opcode != OpCodes.Cgt || !codes[at + 4].IsStloc() || !codes[at + 5].IsLdloc() ||
            codes[at + 4].LocalIndex() != codes[at + 5].LocalIndex() ||
            (codes[at + 6].opcode != OpCodes.Brfalse && codes[at + 6].opcode != OpCodes.Brfalse_S) ||
            codes[at + 6].operand is not Label destination || !codes[at + 17].labels.Contains(destination) ||
            codes[at + 7].opcode != OpCodes.Nop || codes[at + 8].opcode != OpCodes.Ldarg_0 ||
            !Calls(codes[at + 9], typeof(SyncedVehicle), "get_MainVehicle") || codes[at + 10].opcode != OpCodes.Ldarg_0 ||
            !Calls(codes[at + 11], typeof(SyncedEntity), "get_Velocity") || !codes[at + 12].IsLdloc() ||
            !Calls(codes[at + 13], typeof(Vector3), "op_Addition") || !Calls(codes[at + 14], typeof(Entity), "set_Velocity") ||
            codes[at + 15].opcode != OpCodes.Nop || codes[at + 16].opcode != OpCodes.Nop ||
            codes[at + 17].opcode != OpCodes.Ldarg_0 || !Calls(codes[at + 18], typeof(SyncedVehicle), "get_IsFlipped") ||
            Enumerable.Range(at + 1, 16).Any(index => codes[index].labels.Count != 0 || codes[index].blocks.Count != 0))
        {
            return false;
        }
        IList<LocalVariableInfo>? locals = target.GetMethodBody()?.LocalVariables;
        distance = codes[at].LocalIndex();
        correction = codes[at + 12].LocalIndex();
        int condition = codes[at + 4].LocalIndex();
        if (locals == null || distance >= locals.Count || correction >= locals.Count || condition >= locals.Count ||
            locals[distance].LocalType != typeof(float) || locals[correction].LocalType != typeof(Vector3) || locals[condition].LocalType != typeof(bool))
        {
            return false;
        }
        start = at;
        end = at + 17;
        return true;
    }

    internal static IEnumerable<CodeInstruction> ApplyVehicleVelocity(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        if (!TryFindVelocityGate(codes, __originalMethod, out int start, out int end, out int distance, out int correction))
        {
            throw new InvalidOperationException("Unsupported RageCoop vehicle velocity gate.");
        }
        var entry = new CodeInstruction(OpCodes.Ldarg_0);
        entry.labels.AddRange(codes[start].labels);
        entry.blocks.AddRange(codes[start].blocks);
        codes.RemoveRange(start, end - start);
        codes.InsertRange(start, new[]
        {
            entry, new CodeInstruction(OpCodes.Ldloc, distance), new CodeInstruction(OpCodes.Ldloc, correction),
            new CodeInstruction(OpCodes.Call, typeof(EngineMotionGuard).GetMethod(nameof(WriteVehicleVelocity), BindingFlags.Static | BindingFlags.NonPublic)!)
        });
        return codes;
    }

    private static void WriteVehicleVelocity(SyncedVehicle vehicle, float distance, Vector3 correction)
    {
        Vector3 velocity = ReadVelocity!(vehicle);
        vehicle.MainVehicle.Velocity = (double)distance > 0.03d ?
            velocity + MotionCorrectionPolicy.Limit(correction, distance, Game.LastFrameTime) : velocity;
    }

    internal static bool TryFindPedCorrection(IList<CodeInstruction> codes, MethodBase target, out int addition, out int distance)
    {
        addition = distance = -1;
        int[] setters = Enumerable.Range(0, codes.Count).Where(index => Calls(codes[index], typeof(Entity), "set_Velocity")).ToArray();
        if (setters.Length != 1) { return false; }
        int at = setters[0] - 14;
        if (at < 0 || codes[at].opcode != OpCodes.Ldarg_0 || !Calls(codes[at + 1], typeof(SyncedPed), "get_MainPed") ||
            codes[at + 2].opcode != OpCodes.Ldarg_0 || codes[at + 3].operand as MethodInfo != VelocityGetter ||
            codes[at + 4].opcode != OpCodes.Ldc_R4 || codes[at + 4].operand is not float gain || gain != 5f ||
            !codes[at + 5].IsLdloc() || codes[at + 6].opcode != OpCodes.Mul || !codes[at + 7].IsLdloc() ||
            codes[at + 8].opcode != OpCodes.Ldarg_0 || !Calls(codes[at + 9], typeof(SyncedPed), "get_MainPed") ||
            codes[at + 10].operand is not MethodInfo read || read.DeclaringType?.FullName != "RageCoop.Client.Memory" || read.Name != "ReadPosition" ||
            !Calls(codes[at + 11], typeof(Vector3), "op_Subtraction") || !Calls(codes[at + 12], typeof(Vector3), "op_Multiply") ||
            !Calls(codes[at + 13], typeof(Vector3), "op_Addition") ||
            Enumerable.Range(at + 1, 14).Any(index => codes[index].labels.Count != 0 || codes[index].blocks.Count != 0))
        {
            return false;
        }
        IList<LocalVariableInfo>? locals = target.GetMethodBody()?.LocalVariables;
        distance = codes[at + 5].LocalIndex();
        int predicted = codes[at + 7].LocalIndex();
        if (locals == null || distance >= locals.Count || predicted >= locals.Count ||
            locals[distance].LocalType != typeof(float) || locals[predicted].LocalType != typeof(Vector3)) { return false; }
        addition = at + 13;
        return true;
    }

    internal static IEnumerable<CodeInstruction> LimitPedCorrection(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        if (!TryFindPedCorrection(codes, __originalMethod, out int addition, out int distance))
        {
            throw new InvalidOperationException("Unsupported RageCoop pedestrian velocity correction.");
        }
        codes.InsertRange(addition, new[]
        {
            new CodeInstruction(OpCodes.Ldloc, distance),
            new CodeInstruction(OpCodes.Call, typeof(Game).GetProperty(nameof(Game.LastFrameTime))!.GetGetMethod()!),
            new CodeInstruction(OpCodes.Call, typeof(MotionCorrectionPolicy).GetMethod(nameof(MotionCorrectionPolicy.Limit), BindingFlags.Static | BindingFlags.NonPublic)!)
        });
        return codes;
    }

    internal static int[] FindAirborneTargets(IList<CodeInstruction> codes) => Enumerable.Range(0, Math.Max(0, codes.Count - 7)).Where(index =>
        codes[index].opcode == OpCodes.Ldarg_0 && codes[index + 1].operand as MethodInfo == PositionGetter &&
        codes[index + 2].opcode == OpCodes.Ldarg_0 && codes[index + 3].operand as MethodInfo == VelocityGetter &&
        Calls(codes[index + 4], typeof(Vector3), "op_Addition") && codes[index + 5].opcode == OpCodes.Ldc_R4 &&
        codes[index + 5].operand is float amount && amount == 0.5f && Calls(codes[index + 6], typeof(Vector3), "Lerp") &&
        Calls(codes[index + 7], typeof(Entity), "set_PositionNoOffset") &&
        Enumerable.Range(index + 1, 4).All(at => codes[at].labels.Count == 0 && codes[at].blocks.Count == 0)).ToArray();

    internal static IEnumerable<CodeInstruction> PredictAirbornePosition(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        int[] targets = FindAirborneTargets(codes);
        if (targets.Length != 2 || Predict == null) { throw new InvalidOperationException("Unsupported RageCoop airborne position targets."); }
        foreach (int at in targets)
        {
            codes[at + 1].opcode = OpCodes.Ldarg_0;
            codes[at + 1].operand = null;
            codes[at + 2].opcode = OpCodes.Call;
            codes[at + 2].operand = PositionGetter;
            codes[at + 3].opcode = OpCodes.Call;
            codes[at + 3].operand = Predict;
            codes[at + 4].opcode = OpCodes.Nop;
            codes[at + 4].operand = null;
        }
        return codes;
    }

    private static bool Calls(CodeInstruction code, Type type, string name) =>
        (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo method &&
        method.DeclaringType == type && method.Name == name;
}
