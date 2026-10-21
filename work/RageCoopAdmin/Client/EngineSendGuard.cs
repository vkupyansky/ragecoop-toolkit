using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Core;

namespace RageCoopAdmin.Client;

// Keep independent send-packet construction failures out of the frame's world
// update, without changing snapshot cadence, delivery policy or wire layout.
internal static class EngineSendGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginesend";
    private static readonly List<MethodInfo> Patched = new List<MethodInfo>();
    private static Harmony? _harmony;

    internal static MethodInfo? FindSend(Type entity)
    {
        if (entity != typeof(SyncedPed) && entity != typeof(SyncedVehicle)) { return null; }
        Type? network = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Networking", false);
        MethodInfo? method = network?.GetMethod(entity == typeof(SyncedPed) ? "SendPed" : "SendVehicle",
            BindingFlags.Public | BindingFlags.Static, null, new[] { entity, typeof(bool) }, null);
        return method?.ReturnType == typeof(void) ? method : null;
    }

    internal static void Install()
    {
        if (_harmony != null) { return; }
        MethodInfo ped = FindSend(typeof(SyncedPed)) ?? throw Unsupported("SendPed signature");
        MethodInfo vehicle = FindSend(typeof(SyncedVehicle)) ?? throw Unsupported("SendVehicle signature");
        if (FindOwnerCheck(ped, PatchProcessor.GetOriginalInstructions(ped)) < 0 ||
            FindRadioReset(vehicle, PatchProcessor.GetOriginalInstructions(vehicle)) < 0)
        {
            throw Unsupported("send construction IL");
        }
        var harmony = new Harmony(PatchId);
        try
        {
            Patched.Add(ped);
            harmony.Patch(ped, transpiler: new HarmonyMethod(typeof(EngineSendGuard), nameof(ProtectMissingVehicle)) { priority = Priority.First });
            Patched.Add(vehicle);
            harmony.Patch(vehicle, transpiler: new HarmonyMethod(typeof(EngineSendGuard), nameof(ResetRadioStation)) { priority = Priority.First });
        }
        catch (Exception installFailure)
        {
            try { RemovePatches(harmony); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("RageCoop send patch installation and cleanup both failed.", installFailure, cleanupFailure);
            }
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony == null) { return; }
        RemovePatches(_harmony);
        _harmony = null;
    }

    private static void RemovePatches(Harmony harmony)
    {
        foreach (MethodInfo method in Patched) { harmony.Unpatch(method, HarmonyPatchType.Transpiler, PatchId); }
        Patched.Clear();
    }

    internal static int FindOwnerCheck(MethodInfo target, IList<CodeInstruction> instructions)
    {
        if (target != FindSend(typeof(SyncedPed))) { return -1; }
        MethodInfo local = typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.IsLocal))!.GetGetMethod()!;
        int check = -1;
        for (int index = 1; index + 1 < instructions.Count; index++)
        {
            if (!instructions[index].Calls(local)) { continue; }
            int slot = instructions[index - 1].IsLdloc() ? instructions[index - 1].LocalIndex() : -1;
            IList<LocalVariableInfo>? locals = target.GetMethodBody()?.LocalVariables;
            if (check >= 0 || instructions[index].opcode != OpCodes.Callvirt || slot < 0 || locals == null ||
                slot >= locals.Count || locals[slot].LocalType != typeof(SyncedVehicle) ||
                (instructions[index + 1].opcode != OpCodes.Brtrue && instructions[index + 1].opcode != OpCodes.Brtrue_S) ||
                instructions[index + 1].operand is not Label skip ||
                !instructions.Any(code => code.labels.Contains(skip) && code.LoadsConstant(0))) { return -1; }
            check = index;
        }
        return check >= 0 && instructions.Count(code => Equals(code.operand, "Invalid vehicle")) == 1 &&
            instructions.Count(code => code.operand is MethodInfo method && method.DeclaringType?.FullName == "RageCoop.Client.SyncEvents" &&
                method.Name == "TriggerChangeOwner" && method.ReturnType == typeof(void) &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(int), typeof(int) })) == 1 ? check : -1;
    }

    internal static int FindRadioReset(MethodInfo target, IList<CodeInstruction> instructions)
    {
        if (target != FindSend(typeof(SyncedVehicle))) { return -1; }
        Type? packet = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", false);
        MethodInfo? station = packet?.GetProperty("RadioStation")?.GetSetMethod();
        MethodInfo? livery = packet?.GetProperty("Livery")?.GetSetMethod();
        if (station == null || station.GetParameters().Length != 1 || station.GetParameters()[0].ParameterType != typeof(byte) || livery == null) { return -1; }
        int radio = -1, last = -1;
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Calls(station))
            {
                int slot = index >= 2 && instructions[index - 2].IsLdloc() ? instructions[index - 2].LocalIndex() : -1;
                if (radio >= 0 || index < 2 || instructions[index].opcode != OpCodes.Callvirt ||
                    instructions[index - 1].operand is not MethodInfo read || read.Name != "GetPlayerRadioIndex" ||
                    read.DeclaringType?.FullName != "RageCoop.Client.Util" || read.ReturnType != typeof(byte) || read.GetParameters().Length != 0 ||
                    slot < 0 ||
                    target.GetMethodBody()!.LocalVariables[slot].LocalType != packet) { return -1; }
                radio = index;
            }
            if (instructions[index].operand is MethodInfo getter && getter.DeclaringType?.FullName == "GTA.Player" && getter.Name == "get_LastVehicle")
            {
                if (last >= 0 || index < 5 || index + 4 >= instructions.Count ||
                    !instructions[index - 5].Calls(livery) || instructions[index - 4].opcode != OpCodes.Nop ||
                    instructions[index - 3].opcode != OpCodes.Ldarg_0 ||
                    !instructions[index - 2].Calls(typeof(SyncedVehicle).GetProperty(nameof(SyncedVehicle.MainVehicle))!.GetGetMethod()!) ||
                    instructions[index - 1].operand is not MethodInfo player || player.DeclaringType?.FullName != "GTA.Game" || player.Name != "get_Player" ||
                    instructions[index + 1].operand is not MethodInfo equal || equal.DeclaringType?.FullName != "GTA.Entity" || equal.Name != "op_Equality" ||
                    (instructions[index + 4].opcode != OpCodes.Brfalse && instructions[index + 4].opcode != OpCodes.Brfalse_S)) { return -1; }
                last = index;
            }
        }
        if (last < 0 || radio <= last || instructions[last + 4].operand is not Label skip) { return -1; }
        int skipIndex = instructions.ToList().FindIndex(code => code.labels.Contains(skip));
        int full = instructions.ToList().FindIndex(code => code.opcode == OpCodes.Ldarg_1);
        if (skipIndex <= radio || full < 0 || full + 3 >= last - 3 ||
            instructions.Count(code => code.opcode == OpCodes.Ldarg_1) != 1 ||
            !instructions[full + 1].IsStloc() || !instructions[full + 2].IsLdloc() ||
            instructions[full + 1].LocalIndex() != instructions[full + 2].LocalIndex() ||
            target.GetMethodBody()!.LocalVariables[instructions[full + 1].LocalIndex()].LocalType != typeof(bool) ||
            (instructions[full + 3].opcode != OpCodes.Brfalse && instructions[full + 3].opcode != OpCodes.Brfalse_S) ||
            instructions[full + 3].operand is not Label compactSkip) { return -1; }
        int compactIndex = instructions.ToList().FindIndex(code => code.labels.Contains(compactSkip));
        return compactIndex > skipIndex && instructions.Skip(full + 4).Take(last - full - 7).Count(code => code.LoadsConstant(8192)) == 1 ? last - 3 : -1;
    }

    internal static IEnumerable<CodeInstruction> ProtectMissingVehicle(IEnumerable<CodeInstruction> original)
    {
        MethodInfo target = FindSend(typeof(SyncedPed)) ?? throw Unsupported("SendPed signature");
        List<CodeInstruction> instructions = original.Select(code => new CodeInstruction(code)).ToList();
        int check = FindOwnerCheck(target, instructions);
        if (check < 0) { throw Unsupported("ped ownership check IL"); }
        instructions[check].opcode = OpCodes.Call;
        instructions[check].operand = typeof(EngineSendGuard).GetMethod(nameof(IsLocalOrMissing), BindingFlags.Static | BindingFlags.NonPublic)!;
        return instructions;
    }

    internal static IEnumerable<CodeInstruction> ResetRadioStation(IEnumerable<CodeInstruction> original)
    {
        MethodInfo target = FindSend(typeof(SyncedVehicle)) ?? throw Unsupported("SendVehicle signature");
        List<CodeInstruction> instructions = original.Select(code => new CodeInstruction(code)).ToList();
        int marker = FindRadioReset(target, instructions);
        if (marker < 0) { throw Unsupported("vehicle radio construction IL"); }
        MethodInfo station = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", true)!.GetProperty("RadioStation")!.GetSetMethod()!;
        int setter = instructions.FindIndex(code => code.Calls(station));
        var first = new CodeInstruction(instructions[setter - 2]);
        first.labels.Clear();
        first.blocks.Clear();
        first.labels.AddRange(instructions[marker].labels);
        first.blocks.AddRange(instructions[marker].blocks);
        instructions[marker].labels.Clear();
        instructions[marker].blocks.Clear();
        instructions.InsertRange(marker, new[] { first, new CodeInstruction(OpCodes.Ldc_I4, 255), new CodeInstruction(OpCodes.Callvirt, station) });
        return instructions;
    }

    private static bool IsLocalOrMissing(SyncedEntity? vehicle) => vehicle == null || vehicle.IsLocal;

    private static InvalidOperationException Unsupported(string contract) =>
        new InvalidOperationException("Unsupported RageCoop engine send contract: " + contract + ".");
}
