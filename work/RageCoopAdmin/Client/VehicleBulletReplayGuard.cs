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

// Keep GTA's original shot and damage calculation. Only its 50 m replay filter
// needs an exception when the hit belongs to a loaded vehicle we own farther away.
internal static class VehicleBulletReplayGuard
{
    private const string PatchId = "friendslegacy.ragecoop.vehiclebulletrange";
    private static Harmony? _harmony;
    private static MethodInfo? _target;

    internal static bool Install()
    {
        if (_harmony != null) { return true; }
        MethodInfo? target = FindTarget();
        if (target == null || target.ReturnType != typeof(void) || !RuntimeEntityLookup.CanListVehicles ||
            FindRangeGate(PatchProcessor.GetOriginalInstructions(target)) < 0)
        {
            return false;
        }

        var harmony = new Harmony(PatchId);
        try
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(VehicleBulletReplayGuard), nameof(ExtendVehicleRange)));
        }
        catch
        {
            harmony.Unpatch(target, HarmonyPatchType.Transpiler, PatchId);
            throw;
        }
        _target = target;
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony == null || _target == null) { return; }
        _harmony.Unpatch(_target, HarmonyPatchType.Transpiler, PatchId);
        _harmony = null;
        _target = null;
    }

    internal static MethodInfo? FindTarget() =>
        typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.SyncEvents", throwOnError: false)?
            .GetMethod("HandleBulletShot", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(Vector3), typeof(Vector3), typeof(uint), typeof(int) }, null);

    internal static int FindRangeGate(IList<CodeInstruction> instructions)
    {
        int match = -1;
        for (int index = 3; index + 1 < instructions.Count; index++)
        {
            if (instructions[index].opcode != OpCodes.Ldc_R4 ||
                instructions[index].operand is not float value || value != VehicleBulletRange.StandardRangeSquared)
            {
                continue;
            }

            if (match >= 0 || instructions[index + 1].opcode != OpCodes.Cgt ||
                instructions[index - 3].operand is not MethodInfo distance ||
                distance.DeclaringType != typeof(Vector3) || distance.Name != nameof(Vector3.DistanceToSquared))
            {
                return -1;
            }

            match = index;
        }

        return match;
    }

    internal static IEnumerable<CodeInstruction> ExtendVehicleRange(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> instructions = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        int index = FindRangeGate(instructions);
        if (index < 0) { throw new InvalidOperationException("Unsupported RageCoop bullet range filter."); }

        var duplicateDistance = new CodeInstruction(OpCodes.Dup);
        duplicateDistance.labels.AddRange(instructions[index].labels);
        duplicateDistance.blocks.AddRange(instructions[index].blocks);
        instructions[index] = duplicateDistance;
        instructions.Insert(index + 1, new CodeInstruction(OpCodes.Ldarg_1));
        instructions.Insert(index + 2, CodeInstruction.Call(typeof(VehicleBulletReplayGuard), nameof(GetRangeLimit)));
        return instructions;
    }

    internal static float GetRangeLimit(float distanceSquared, Vector3 impact)
    {
        if (!VehicleBulletRange.NeedsVehicleCheck(distanceSquared)) { return VehicleBulletRange.StandardRangeSquared; }
        foreach (SyncedVehicle synced in RuntimeEntityLookup.GetLocalVehicles())
        {
            Vehicle? vehicle = synced.MainVehicle;
            if (vehicle == null || !vehicle.Exists()) { continue; }
            vehicle.Model.GetDimensions(out Vector3 minimum, out Vector3 maximum);
            if (VehicleBulletRange.IsVehicleHit(vehicle.GetPositionOffset(impact), minimum, maximum))
            {
                return float.MaxValue;
            }
        }

        return VehicleBulletRange.StandardRangeSquared;
    }
}
