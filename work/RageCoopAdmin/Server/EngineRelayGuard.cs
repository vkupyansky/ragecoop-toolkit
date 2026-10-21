using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RageCoop.Core;

namespace CoopAdmin.Server;

// The base relay changes even ReliableOrdered OwnerChanged messages into
// UnreliableSequenced. Change only that event's outgoing delivery policy.
internal static class EngineRelayGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginerelay";
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    internal static readonly byte OwnerChangedPacketType = ReadPacketType("OwnerChanged");

    internal static void Install()
    {
        if (_harmony != null) { return; }
        MethodInfo target = FindTarget() ?? throw Unsupported("ProcessMessage signature");
        if (FindRelayDelivery(PatchProcessor.GetOriginalInstructions(target)) < 0)
        {
            throw Unsupported("sync relay delivery IL");
        }
        var harmony = new Harmony(PatchId);
        try
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(EngineRelayGuard), nameof(PreserveOwnerDelivery)));
        }
        catch (Exception installFailure)
        {
            try { harmony.Unpatch(target, HarmonyPatchType.Transpiler, PatchId); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("RageCoop relay patch installation and cleanup both failed.", installFailure, cleanupFailure);
            }
            throw;
        }
        _harmony = harmony;
        _target = target;
    }

    internal static void Uninstall()
    {
        if (_harmony == null || _target == null) { return; }
        _harmony.Unpatch(_target, HarmonyPatchType.Transpiler, PatchId);
        _harmony = null;
        _target = null;
    }

    internal static MethodInfo? FindTarget()
    {
        MethodInfo? method = typeof(RageCoop.Server.Server).GetMethod("ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        ParameterInfo[]? parameters = method?.GetParameters();
        return method?.ReturnType == typeof(void) && parameters?.Length == 1 &&
            parameters[0].ParameterType.FullName == "Lidgren.Network.NetIncomingMessage" ? method : null;
    }

    internal static int FindRelayDelivery(IList<CodeInstruction> instructions)
    {
        int match = -1;
        for (int index = 1; index + 2 < instructions.Count; index++)
        {
            if (instructions[index].opcode != OpCodes.Ldc_I4_2 || !instructions[index + 1].LoadsConstant(12) ||
                instructions[index + 2].operand is not MethodInfo send || send.Name != "SendMessage" ||
                send.DeclaringType?.FullName != "Lidgren.Network.NetPeer") { continue; }
            Type[] arguments = send.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            if (match >= 0 || instructions[index + 2].opcode != OpCodes.Callvirt || arguments.Length != 4 ||
                arguments[0].FullName != "Lidgren.Network.NetOutgoingMessage" || !arguments[1].IsGenericType ||
                arguments[1].GetGenericTypeDefinition() != typeof(IList<>) ||
                arguments[1].GetGenericArguments()[0].FullName != "Lidgren.Network.NetConnection" ||
                arguments[2].FullName != "Lidgren.Network.NetDeliveryMethod" || arguments[3] != typeof(int) ||
                Enum.GetName(arguments[2], Enum.ToObject(arguments[2], 2)) != "UnreliableSequenced" ||
                Enum.GetName(arguments[2], Enum.ToObject(arguments[2], 67)) != "ReliableOrdered") { return -1; }
            match = index;
        }
        return match;
    }

    internal static IEnumerable<CodeInstruction> PreserveOwnerDelivery(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> instructions = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        int delivery = FindRelayDelivery(instructions);
        if (delivery < 0) { throw Unsupported("sync relay delivery IL"); }
        // At this point the packet has been copied and its read position is at
        // the end. Read the immutable first byte without moving that position.
        Type incoming = FindTarget()!.GetParameters()[0].ParameterType;
        MethodInfo data = incoming.GetProperty("Data")?.GetGetMethod() ?? throw Unsupported("incoming packet data");
        if (data.ReturnType != typeof(byte[])) { throw Unsupported("incoming packet bytes"); }
        var load = new CodeInstruction(OpCodes.Ldarg_1);
        load.labels.AddRange(instructions[delivery].labels);
        load.blocks.AddRange(instructions[delivery].blocks);
        instructions[delivery] = load;
        instructions.InsertRange(delivery + 1, new[]
        {
            new CodeInstruction(OpCodes.Callvirt, data),
            new CodeInstruction(OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Ldelem_U1),
            CodeInstruction.Call(typeof(EngineRelayGuard), nameof(GetDelivery))
        });
        return instructions;
    }

    internal static int GetDelivery(byte packetType) => packetType == OwnerChangedPacketType ? 67 : 2;

    internal static byte ReadPacketType(string name)
    {
        Type type = typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!;
        if (!type.IsEnum || !Enum.IsDefined(type, name)) { throw Unsupported("packet type " + name); }
        return Convert.ToByte(Enum.Parse(type, name));
    }

    private static InvalidOperationException Unsupported(string contract) =>
        new InvalidOperationException("Unsupported RageCoop engine relay contract: " + contract + ".");
}
