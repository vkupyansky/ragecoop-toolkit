using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using CoopControl.Shared;
using GTA;
using GTA.Math;
using HarmonyLib;
using Lidgren.Network;

internal static partial class Program
{
    private const BindingFlags CodecMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string CodecPatchId = "friendslegacy.ragecoop.enginecodec";

    private static void CheckCodecs()
    {
        Assert(EngineCodecGuard.HasSupportedContract(), "The codec guard recognizes the exact installed Core readers and writers.");
        CheckCodecRewrites();
        CheckHandshakeEndpoints(guarded: false);
        CheckPedWeaponComponents(guarded: false);
        object unchangedEvent = CodecEvent(new object[] { 71, "unchanged", new byte[] { 2, 4, 6 }, true, 93 });
        byte[] unchangedWire = CodecPack(unchangedEvent).Data.ToArray();
        object originalVoice = CodecVoice(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 }, 8);
        int originalVoiceBits = CodecPack(originalVoice).LengthBits;
        object originalVector2 = CodecEvent(new object[] { new Vector2(5, 6), 71593 });
        int originalVector2Bits = CodecPack(originalVector2).LengthBits;
        foreach (MethodInfo reader in EngineCodecGuard.FindByteArrayReaders())
        {
            object packet = CodecArrayReader(reader);
            CodecDeserialize(packet, CodecArrayReaderInput(reader, invalid: false));
        }
        EngineCodecGuard.Install();
        EngineCodecGuard.Install();
        try
        {
            Assert(Harmony.GetPatchInfo(EngineCodecGuard.FindByteArrayTarget())?.Prefixes.Count(patch => patch.owner == CodecPatchId) == 1 &&
                Harmony.GetPatchInfo(EngineCodecGuard.FindCustomEventTarget())?.Transpilers.Count(patch => patch.owner == CodecPatchId) == 1 &&
                Harmony.GetPatchInfo(EngineCodecGuard.FindVoiceSerializeTarget())?.Transpilers.Count(patch => patch.owner == CodecPatchId) == 1 &&
                Harmony.GetPatchInfo(EngineCodecGuard.FindObjectWriterTarget())?.Transpilers.Count(patch => patch.owner == CodecPatchId) == 1 &&
                EngineCodecGuard.FindPacketReaders().All(method => Harmony.GetPatchInfo(method)?.Transpilers.Count(patch => patch.owner == CodecPatchId) == 1),
                "Repeated installation adds one codec patch for every supported packet reader, including already-compiled readers.");
            Assert(CodecPack(unchangedEvent).Data.SequenceEqual(unchangedWire), "Ordinary custom-event serialization retains its original bytes.");
            CheckByteArrayCodec();
            CheckCustomEventCodec();
            CheckVoiceCodec();
            CheckPacketTruncation();
            CheckHandshakeEndpoints(guarded: true);
            CheckPedWeaponComponents(guarded: true);
            foreach (MethodInfo reader in EngineCodecGuard.FindByteArrayReaders())
            {
                Assert(!PatchProcessor.GetCurrentInstructions(reader).Any(code => code.Calls(EngineCodecGuard.FindByteArrayTarget())),
                    "Each warmed real packet reader directly calls the bounded helper after rewriting its caller.");
                object packet = CodecArrayReader(reader);
                CodecDeserialize(packet, CodecArrayReaderInput(reader, invalid: false));
                CodecReject(() => CodecDeserialize(packet, CodecArrayReaderInput(reader, invalid: true)),
                    "A packet reader compiled before installation rejects its hostile byte-array length after installation.");
            }
            Assert(CodecPack(originalVoice).LengthBits == originalVoiceBits + 32, "Voice gains exactly the missing four-byte buffer length.");
            Assert(CodecPack(originalVector2).LengthBits == originalVector2Bits - 32, "Vector2 writes exactly its two float components without a third component.");
        }
        finally { EngineCodecGuard.Uninstall(); }
        EngineCodecGuard.Uninstall();
        foreach (MethodInfo target in new[] { EngineCodecGuard.FindByteArrayTarget(), EngineCodecGuard.FindCustomEventTarget(), EngineCodecGuard.FindVoiceSerializeTarget(), EngineCodecGuard.FindObjectWriterTarget() }.Concat(EngineCodecGuard.FindPacketReaders()))
        {
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains(CodecPatchId) != true, "Unload removes each codec patch.");
        }
        Assert(CodecPack(originalVoice).LengthBits == originalVoiceBits, "Uninstall restores the installed Voice writer.");
        Assert(CodecPack(originalVector2).LengthBits == originalVector2Bits, "Uninstall restores the installed Vector2 writer.");
    }

    private static void CheckCodecRewrites()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(EngineCodecGuard.FindCustomEventTarget());
        List<CodeInstruction> changed = EngineCodecGuard.RewriteCustomEvent(original).ToList();
        Assert(changed.Count == original.Count, "Custom-event patch retains every instruction and branch position.");
        int differences = 0;
        for (int index = 0; index < original.Count; index++)
        {
            if (changed[index].opcode != original[index].opcode || !Equals(changed[index].operand, original[index].operand)) { differences++; }
            Assert(changed[index].labels.SequenceEqual(original[index].labels) && changed[index].blocks.SequenceEqual(original[index].blocks),
                "Custom-event patch preserves branch labels and exception boundaries.");
        }
        Assert(differences > 5, "The custom-event patch guards every typed read as well as its header, count and short codec.");
        original = PatchProcessor.GetOriginalInstructions(EngineCodecGuard.FindVoiceSerializeTarget());
        changed = EngineCodecGuard.RewriteVoice(original).ToList();
        Assert(changed.Count == original.Count && changed.Where((code, index) => code.opcode != original[index].opcode || !Equals(code.operand, original[index].operand)).Count() == 1,
            "Voice changes only its one buffer writer.");
        original = PatchProcessor.GetOriginalInstructions(EngineCodecGuard.FindObjectWriterTarget());
        changed = EngineCodecGuard.RewriteObjectWriter(original).ToList();
        Assert(changed.Count == original.Count && changed.Where((code, index) => code.opcode != original[index].opcode || !Equals(code.operand, original[index].operand)).Count() == 2 &&
            changed.Where((code, index) => !code.labels.SequenceEqual(original[index].labels) || !code.blocks.SequenceEqual(original[index].blocks)).Count() == 0,
            "Only the Vector2 conversion and its writer change; branch labels and exception boundaries remain intact.");
        CodecRejectRewrite(() => EngineCodecGuard.RewriteCustomEvent(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray());
        CodecRejectRewrite(() => EngineCodecGuard.RewriteVoice(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray());
        CodecRejectRewrite(() => EngineCodecGuard.RewriteObjectWriter(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray());
        CodecRejectRewrite(() => EngineCodecGuard.ResetPedWeaponComponents(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray());
    }

    private static object CodecArrayReader(MethodInfo reader) => reader.DeclaringType!.Name == "ChatMessage" ?
        Activator.CreateInstance(reader.DeclaringType, new object[] { new Func<byte[], byte[]>(bytes => bytes) })! : Activator.CreateInstance(reader.DeclaringType)!;

    private static NetIncomingMessage CodecArrayReaderInput(MethodInfo reader, bool invalid)
    {
        NetOutgoingMessage output = CodecOutgoing();
        switch (reader.DeclaringType!.Name)
        {
            case "ChatMessage": output.Write("test"); break;
            case "FileTransferChunk": case "Voice": output.Write(71); break;
            case "Handshake": output.Write(71); output.Write("test"); output.Write("1.5.12.15"); output.Write("127.0.0.1:4499"); break;
            case "PublicKeyResponse": break;
            default: throw new InvalidOperationException("Unexpected array reader.");
        }
        output.Write(invalid ? int.MaxValue : 0);
        if (!invalid)
        {
            if (reader.DeclaringType.Name == "Handshake") { output.Write(0); output.Write(0); }
            else if (reader.DeclaringType.Name == "PublicKeyResponse" || reader.DeclaringType.Name == "Voice") { output.Write(0); }
        }
        return CodecIncoming(output);
    }

    private static void CheckByteArrayCodec()
    {
        MethodInfo write = EngineCodecGuard.FindByteArrayTarget().DeclaringType!.GetMethod("WriteByteArray", CodecMethods)!;
        foreach (int size in new[] { 0, 1, 50_000, EngineCodecGuard.MaximumByteArrayLength })
        {
            byte[] data = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
            NetOutgoingMessage outgoing = CodecOutgoing();
            outgoing.Write(true);
            write.Invoke(null, new object[] { outgoing, data });
            outgoing.Write(71593);
            NetIncomingMessage incoming = CodecIncoming(outgoing);
            Assert(incoming.ReadBoolean(), "The array test starts at an unaligned bit position.");
            byte[] actual = (byte[])EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { incoming })!;
            Assert(actual.SequenceEqual(data) && incoming.ReadInt32() == 71593 && incoming.Position == incoming.LengthBits,
                "Actual byte-array reader supports empty, unaligned, file-chunk and maximum-size arrays without consuming the next field.");
        }
        foreach (int length in new[] { -1, int.MinValue, int.MaxValue, EngineCodecGuard.MaximumByteArrayLength + 1 })
        {
            NetOutgoingMessage outgoing = CodecOutgoing();
            outgoing.Write(length);
            CodecReject(() => EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { CodecIncoming(outgoing) }), "Invalid byte-array length is rejected before allocation.");
        }
        NetOutgoingMessage truncated = CodecOutgoing();
        truncated.Write(2);
        truncated.Write((byte)7);
        CodecReject(() => EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { CodecIncoming(truncated) }), "A length cannot consume unused backing-buffer capacity.");
        NetOutgoingMessage partial = CodecOutgoing();
        partial.Write(1);
        partial.Write((byte)7);
        NetIncomingMessage partialInput = CodecIncoming(partial);
        partialInput.LengthBits--;
        CodecReject(() => EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { partialInput }), "Seven remaining bits cannot satisfy a one-byte array.");
        NetIncomingMessage header = CodecIncoming(partial);
        header.LengthBits = 31;
        CodecReject(() => EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { header }), "A truncated byte-array header is rejected before its read.");
        NetIncomingMessage invalidPosition = CodecIncoming(partial);
        invalidPosition.Position = -1;
        CodecReject(() => EngineCodecGuard.FindByteArrayTarget().Invoke(null, new object[] { invalidPosition }), "An invalid message position is rejected.");
    }

    private static void CheckCustomEventCodec()
    {
        object[] expected =
        {
            (byte)254, short.MinValue, short.MaxValue, (ushort)65000, -42, uint.MaxValue, long.MinValue, ulong.MaxValue,
            3.25f, true, "Проверка", new Vector3(1, 2, 3), new Quaternion(1, 2, 3, 4), new Model(123), new Vector2(5, 6),
            new byte[] { 9, 8, 7 }, false, (short)-12, 71593
        };
        foreach (bool queued in new[] { false, true })
        {
            object packet = CodecEvent(expected, queued: queued);
            object received = CodecEvent(Array.Empty<object>(), queued: queued);
            CodecRoundtrip(packet, received);
            object[] actual = (object[])received.GetType().GetProperty("Args")!.GetValue(received)!;
            Assert(actual.Length == expected.Length && (int)received.GetType().GetProperty("Hash")!.GetValue(received)! == 715,
                "Both actual custom-event kinds preserve their hash and argument count.");
            for (int index = 0; index < expected.Length; index++)
            {
                Assert(actual[index].GetType() == expected[index].GetType() &&
                    (expected[index] is byte[] bytes ? ((byte[])actual[index]).SequenceEqual(bytes) : Equals(actual[index], expected[index])),
                    "Every tested argument retains its value, runtime type and following-field alignment.");
            }
        }
        object empty = CodecEvent(Array.Empty<object>());
        CodecRoundtrip(empty, CodecEvent(Array.Empty<object>()));
        object maximum = CodecEvent(Enumerable.Repeat<object>((byte)7, EngineCodecGuard.MaximumCustomArguments).ToArray());
        CodecRoundtrip(maximum, CodecEvent(Array.Empty<object>()));
        int resolved = 0;
        Func<byte, NetIncomingMessage, object> resolver = (tag, input) =>
        {
            Assert(tag == 55, "The original custom resolver receives its unchanged tag.");
            resolved++;
            return input.ReadUInt16();
        };
        object resolverPacket = CodecEvent(new object[] { Tuple.Create((byte)55, new byte[] { 7, 8 }), (short)-12, 42 });
        object resolverReceived = CodecEvent(Array.Empty<object>(), resolver);
        CodecRoundtrip(resolverPacket, resolverReceived);
        Assert(resolved == 1 && (ushort)((object[])resolverReceived.GetType().GetProperty("Args")!.GetValue(resolverReceived)!)[0] == 2055,
            "Existing extension arguments still use the engine's original resolver.");
        foreach (int count in new[] { -1, int.MinValue, int.MaxValue, EngineCodecGuard.MaximumCustomArguments + 1, 1 })
        {
            NetOutgoingMessage outgoing = CodecOutgoing();
            outgoing.Write(715);
            outgoing.Write(count);
            if (count == EngineCodecGuard.MaximumCustomArguments + 1) { outgoing.Write(new byte[count * 2]); }
            object[] previous = { "retained" };
            object packet = CodecEvent(previous);
            CodecReject(() => CodecDeserialize(packet, CodecIncoming(outgoing)), "Invalid custom-event count is rejected before object-array allocation.");
            Assert(ReferenceEquals(packet.GetType().GetProperty("Args")!.GetValue(packet), previous), "A rejected count does not replace the previous argument array.");
        }
        NetOutgoingMessage undersized = CodecOutgoing();
        undersized.Write(715);
        undersized.Write(2);
        undersized.Write((byte)1);
        CodecReject(() => CodecDeserialize(CodecEvent(Array.Empty<object>()), CodecIncoming(undersized)), "Each declared argument requires at least one complete tag byte.");
        NetIncomingMessage truncatedHeader = CodecIncoming(undersized);
        truncatedHeader.LengthBits = 63;
        CodecReject(() => CodecDeserialize(CodecEvent(Array.Empty<object>()), truncatedHeader), "A custom-event header must contain both integers.");
        NetOutgoingMessage nested = CodecOutgoing();
        nested.Write(715);
        nested.Write(1);
        nested.Write((byte)21);
        nested.Write(int.MaxValue);
        CodecReject(() => CodecDeserialize(CodecEvent(Array.Empty<object>()), CodecIncoming(nested)), "Custom-event byte arrays use the guarded actual extension reader.");
    }

    private static void CheckVoiceCodec()
    {
        foreach (byte[] buffer in new[] { Array.Empty<byte>(), new byte[128], new byte[] { 255, 255, 255, 127, 8, 7, 6, 5 } })
        {
            object packet = CodecVoice(buffer, buffer.Length);
            object received = CodecVoice(Array.Empty<byte>(), 0);
            CodecRoundtrip(packet, received);
            Type type = received.GetType();
            Assert((int)type.GetProperty("ID")!.GetValue(received)! == 71 &&
                ((byte[])type.GetProperty("Buffer")!.GetValue(received)!).SequenceEqual(buffer) &&
                (int)type.GetProperty("Recorded")!.GetValue(received)! == buffer.Length,
                "Actual Voice codecs preserve empty, zero PCM and nonzero PCM buffers and Recorded.");
        }
        NetOutgoingMessage malformed = CodecOutgoing();
        malformed.Write(71);
        malformed.Write(int.MaxValue);
        malformed.Write(0);
        CodecReject(() => CodecDeserialize(CodecVoice(Array.Empty<byte>(), 0), CodecIncoming(malformed)), "Voice rejects a hostile length before allocating its audio buffer.");
    }

    private static int endpointDnsCalls;

    private static void CheckHandshakeEndpoints(bool guarded)
    {
        Type utility = typeof(RageCoop.Core.Logger).Assembly.GetType("RageCoop.Core.CoreUtils", true)!;
        MethodInfo resolver = utility.GetMethod("GetIPfromHost", CodecMethods)!;
        var isolation = new Harmony("friendslegacy.check.endpointdns");
        isolation.Patch(resolver, prefix: new HarmonyMethod(typeof(Program), nameof(RefuseEndpointDns)));
        endpointDnsCalls = 0;
        MethodInfo reader = EngineCodecGuard.FindPacketReaders().Single(method => method.DeclaringType!.Name == "Handshake");
        object Receive(string endpoint)
        {
            NetOutgoingMessage wire = CodecOutgoing();
            wire.Write(71); wire.Write("test"); wire.Write("1.5.12.15"); wire.Write(endpoint);
            wire.Write(0); wire.Write(0); wire.Write(0);
            object packet = Activator.CreateInstance(reader.DeclaringType!)!;
            CodecDeserialize(packet, CodecIncoming(wire));
            return packet;
        }
        try
        {
            if (!guarded)
            {
                try { Receive("untrusted.invalid:4499"); throw new InvalidOperationException("The original endpoint DNS boundary was not reached."); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException failure && failure.Message == "isolated endpoint DNS") { }
                Assert(endpointDnsCalls == 1, "the real original handshake reader calls DNS for a supplied hostname; the resolver is isolated from the network");
                return;
            }
            foreach (IPEndPoint expected in new[]
            {
                new IPEndPoint(IPAddress.Loopback, 4499), new IPEndPoint(IPAddress.Any, 0),
                new IPEndPoint(IPAddress.IPv6Loopback, 65535), new IPEndPoint(IPAddress.Parse("fe80::1%7"), 4499)
            })
            {
                object received = Receive(expected.ToString());
                Assert(Equals(received.GetType().GetProperty("InternalEndPoint")!.GetValue(received), expected),
                    "the actual guarded handshake preserves numeric IPv4/IPv6 endpoints, scoped IPv6 and port boundaries");
            }
            foreach (string invalid in new[] { "untrusted.invalid:4499", "localhost:4499", "", ":4499", "127.0.0.1", "127.0.0.1:-1",
                "127.0.0.1:65536", "127.0.0.1:+1", "[::1]", "::1:4499", "[untrusted.invalid]:4499", "127.0.0.1: 1" })
                CodecReject(() => Receive(invalid), "An invalid handshake endpoint was accepted.");
            Assert(endpointDnsCalls == 0, "malformed or hostname handshake endpoints fail before any DNS call in both runtimes");
        }
        finally { isolation.UnpatchAll(isolation.Id); }
    }

    private static void RefuseEndpointDns()
    {
        endpointDnsCalls++;
        throw new InvalidOperationException("isolated endpoint DNS");
    }

    private static object CodecEvent(object[] arguments, Func<byte, NetIncomingMessage, object>? resolver = null, bool queued = false)
    {
        Type type = EngineCodecGuard.FindCustomEventTarget().DeclaringType!;
        object packet = Activator.CreateInstance(type, new object?[] { resolver, queued })!;
        type.GetProperty("Hash")!.SetValue(packet, 715);
        type.GetProperty("Args")!.SetValue(packet, arguments);
        return packet;
    }

    private static object CodecVoice(byte[] buffer, int recorded)
    {
        Type type = EngineCodecGuard.FindVoiceSerializeTarget().DeclaringType!;
        object packet = Activator.CreateInstance(type)!;
        type.GetProperty("ID")!.SetValue(packet, 71);
        type.GetProperty("Buffer")!.SetValue(packet, buffer);
        type.GetProperty("Recorded")!.SetValue(packet, recorded);
        return packet;
    }

    private static NetOutgoingMessage CodecPack(object packet)
    {
        NetOutgoingMessage outgoing = CodecOutgoing();
        packet.GetType().GetMethod("Pack")!.Invoke(packet, new object[] { outgoing });
        return outgoing;
    }

    private static void CodecRoundtrip(object packet, object received)
    {
        NetIncomingMessage incoming = CodecIncoming(CodecPack(packet));
        incoming.ReadByte();
        CodecDeserialize(received, incoming);
        Assert(incoming.Position == incoming.LengthBits, "Actual codecs consume exactly the payload bits written by Pack.");
    }

    private static void CodecDeserialize(object packet, NetIncomingMessage input) => packet.GetType().GetMethod("Deserialize")!.Invoke(packet, new object[] { input });
    private static NetOutgoingMessage CodecOutgoing() => (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
    private static NetIncomingMessage CodecIncoming(NetOutgoingMessage outgoing)
    {
        var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
        incoming.Data = outgoing.Data;
        incoming.LengthBits = outgoing.LengthBits;
        return incoming;
    }

    private static void CodecReject(Action action, string message)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { action(); }
        catch (Exception error) when ((error is TargetInvocationException invocation ? invocation.InnerException : error) is InvalidDataException)
        {
            Assert(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024, "Rejected lengths do not allocate their declared byte/object arrays.");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void CodecRejectRewrite(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("An unsupported codec body received a guessed rewrite.");
    }
}
