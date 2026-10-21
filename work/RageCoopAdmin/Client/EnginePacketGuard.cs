using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using CoopControl.Shared;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Client;
using RageCoop.Core;

namespace RageCoopAdmin.Client;

// A missing damage payload in a full snapshot means an intact vehicle. The
// engine reuses its receive packet, so its previous model must be cleared here.
internal static class EnginePacketGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginepacket";
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    private static MethodInfo? _envelopeTarget;

    internal static MethodInfo? FindEnvelopeTarget() => typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Networking", false)?
        .GetMethod("ProcessMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(NetIncomingMessage) }, null);

    internal static bool HasEnvelopeContract(MethodInfo target)
    {
        if (target != FindEnvelopeTarget() || target.ReturnType != typeof(void)) { return false; }
        byte[]? body = target.GetMethodBody()?.GetILAsByteArray();
        if (body == null) { return false; }
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() ==
            "2eb20f51ef3a68412335373ce6772092f3f84d3344188fed8aca4c555b74abea";
    }

    internal static MethodInfo? FindTarget()
    {
        Type? packet = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", false);
        MethodInfo? target = packet?.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        ParameterInfo[]? parameters = target?.GetParameters();
        return target?.ReturnType == typeof(void) && parameters?.Length == 1 &&
            parameters[0].ParameterType.FullName == "Lidgren.Network.NetIncomingMessage" ? target : null;
    }

    internal static void Install()
    {
        if (_harmony != null) { return; }
        MethodInfo target = FindTarget() ?? throw Unsupported("VehicleSync.Deserialize signature");
        MethodInfo envelope = FindEnvelopeTarget() ?? throw Unsupported("Networking.ProcessMessage signature");
        if (!HasEnvelopeContract(envelope)) { throw Unsupported("Networking.ProcessMessage IL"); }
        if (FindDamageMarker(target, PatchProcessor.GetOriginalInstructions(target)) < 0)
        {
            throw Unsupported("full vehicle damage reader IL");
        }
        var harmony = new Harmony(PatchId);
        try
        {
            // Locate the original damage marker before the shared codec guard
            // replaces Lidgren scalar readers with their bounded equivalents.
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(EnginePacketGuard), nameof(ResetFullDamage)) { priority = Priority.First });
            harmony.Patch(envelope, transpiler: new HarmonyMethod(typeof(EnginePacketGuard), nameof(BoundEnvelopeReads)));
        }
        catch (Exception installFailure)
        {
            try
            {
                try { harmony.Unpatch(envelope, HarmonyPatchType.Transpiler, PatchId); }
                finally { harmony.Unpatch(target, HarmonyPatchType.Transpiler, PatchId); }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("RageCoop packet patch installation and cleanup both failed.", installFailure, cleanupFailure);
            }
            throw;
        }
        _harmony = harmony;
        _target = target;
        _envelopeTarget = envelope;
    }

    internal static void Uninstall()
    {
        if (_harmony == null || _target == null) { return; }
        try
        {
            if (_envelopeTarget != null) { _harmony.Unpatch(_envelopeTarget, HarmonyPatchType.Transpiler, PatchId); }
        }
        finally { _harmony.Unpatch(_target, HarmonyPatchType.Transpiler, PatchId); }
        _harmony = null;
        _target = null;
        _envelopeTarget = null;
    }

    internal static IEnumerable<CodeInstruction> BoundEnvelopeReads(IEnumerable<CodeInstruction> original)
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>();
        foreach (string name in new[] { "ReadByte", "ReadInt32", "ReadString" })
        {
            replacements.Add(typeof(NetBuffer).GetMethod(name, Type.EmptyTypes)!,
                typeof(EngineCodecGuard).GetMethod("Bounded" + name, BindingFlags.Static | BindingFlags.NonPublic)!);
        }
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo reader && replacements.TryGetValue(reader, out MethodInfo bounded))
            {
                code.opcode = OpCodes.Call;
                code.operand = bounded;
            }
            yield return code;
        }
    }

    internal static int FindDamageMarker(MethodInfo target, IList<CodeInstruction> instructions)
    {
        if (target != FindTarget() || target.DeclaringType == null) { return -1; }
        Type packet = target.DeclaringType;
        PropertyInfo? damage = packet.GetProperty("DamageModel");
        MethodInfo? setter = damage?.GetSetMethod();
        Type? damageType = damage?.PropertyType;
        Type? flags = packet.GetProperty("Flags")?.PropertyType;
        if (setter == null || damageType?.FullName != "RageCoop.Core.VehicleDamageModel" || !damageType.IsValueType ||
            flags?.IsEnum != true || !Enum.IsDefined(flags, "IsFullSync") || Convert.ToUInt64(Enum.Parse(flags, "IsFullSync")) != 8192)
        {
            return -1;
        }
        foreach (string name in new[] { "BrokenDoors", "OpenedDoors", "BrokenWindows", "LeftHeadLightBroken", "RightHeadLightBroken" })
        {
            if (damageType.GetProperty(name)?.PropertyType != typeof(byte)) { return -1; }
        }
        if (damageType.GetProperty("BurstedTires")?.PropertyType != typeof(short)) { return -1; }

        int marker = -1, damageWrite = -1, fullGate = -1;
        for (int index = 0; index < instructions.Count; index++)
        {
            CodeInstruction code = instructions[index];
            if (Equals(code.operand, setter))
            {
                if (damageWrite >= 0 || code.opcode != OpCodes.Call) { return -1; }
                damageWrite = index;
            }
            if (code.operand is MethodInfo method && method.Name == "ReadBoolean" && method.DeclaringType?.FullName == "Lidgren.Network.NetBuffer")
            {
                if (marker >= 0 || index < 1 || index + 1 >= instructions.Count || code.opcode != OpCodes.Callvirt ||
                    method.ReturnType != typeof(bool) || method.GetParameters().Length != 0 || instructions[index - 1].opcode != OpCodes.Ldarg_1 ||
                    !IsFalseBranch(instructions[index + 1])) { return -1; }
                marker = index - 1;
            }
            if (index >= 2 && index + 2 < instructions.Count && code.LoadsConstant(8192) &&
                instructions[index - 1].Calls(packet.GetProperty("Flags")!.GetGetMethod()!) &&
                instructions[index - 2].opcode == OpCodes.Ldarg_0 &&
                instructions[index + 1].operand is MethodInfo hasFlag && hasFlag.Name == "HasVehFlag" &&
                hasFlag.DeclaringType?.FullName == "RageCoop.Core.Extensions" && hasFlag.ReturnType == typeof(bool) &&
                hasFlag.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { flags, flags }) &&
                IsFalseBranch(instructions[index + 2]))
            {
                if (fullGate >= 0) { return -1; }
                fullGate = index + 2;
            }
        }
        if (fullGate < 0 || marker <= fullGate || damageWrite <= marker + 2 ||
            instructions[marker + 2].operand is not Label skip || instructions[fullGate].operand is not Label partialReturn)
        {
            return -1;
        }
        int skipIndex = instructions.ToList().FindIndex(code => code.labels.Contains(skip));
        int returnIndex = instructions.ToList().FindIndex(code => code.labels.Contains(partialReturn));
        if (skipIndex != damageWrite + 1 || skipIndex + 3 >= instructions.Count || returnIndex <= skipIndex ||
            instructions[returnIndex].opcode != OpCodes.Ret || instructions[skipIndex].opcode != OpCodes.Ldarg_0 ||
            instructions[skipIndex + 1].opcode != OpCodes.Ldarg_1 ||
            instructions[skipIndex + 2].operand is not MethodInfo readByte || readByte.Name != "ReadByte" ||
            readByte.DeclaringType?.FullName != "Lidgren.Network.NetBuffer" || readByte.ReturnType != typeof(byte) ||
            !instructions[skipIndex + 3].Calls(packet.GetProperty("LockStatus")?.GetSetMethod()) ||
            instructions.Skip(marker + 3).Take(damageWrite - marker - 3).Count(code => code.opcode == OpCodes.Initobj && Equals(code.operand, damageType)) != 1)
        {
            return -1;
        }
        return marker;
    }

    internal static IEnumerable<CodeInstruction> ResetFullDamage(IEnumerable<CodeInstruction> original, ILGenerator generator)
    {
        MethodInfo target = FindTarget() ?? throw Unsupported("VehicleSync.Deserialize signature");
        List<CodeInstruction> instructions = original.Select(code => new CodeInstruction(code)).ToList();
        int marker = FindDamageMarker(target, instructions);
        if (marker < 0) { throw Unsupported("full vehicle damage reader IL"); }
        PropertyInfo damage = target.DeclaringType!.GetProperty("DamageModel")!;
        LocalBuilder clean = generator.DeclareLocal(damage.PropertyType);
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(instructions[marker].labels);
        first.blocks.AddRange(instructions[marker].blocks);
        instructions[marker].labels.Clear();
        instructions[marker].blocks.Clear();
        instructions.InsertRange(marker, new[]
        {
            first,
            new CodeInstruction(OpCodes.Ldloca, clean),
            new CodeInstruction(OpCodes.Initobj, damage.PropertyType),
            new CodeInstruction(OpCodes.Ldloc, clean),
            new CodeInstruction(OpCodes.Call, damage.GetSetMethod()!)
        });
        return instructions;
    }

    private static bool IsFalseBranch(CodeInstruction code) => code.opcode == OpCodes.Brfalse || code.opcode == OpCodes.Brfalse_S;

    private static InvalidOperationException Unsupported(string contract) =>
        new InvalidOperationException("Unsupported RageCoop engine packet contract: " + contract + ".");
}
