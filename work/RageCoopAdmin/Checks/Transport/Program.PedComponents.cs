using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopControl.Shared;
using Lidgren.Network;

internal static partial class Program
{
    private static void CheckPedWeaponComponents(bool guarded)
    {
        MethodInfo reader = EngineCodecGuard.FindPacketReaders().Single(method => method.DeclaringType!.Name == "PedSync");
        Type type = reader.DeclaringType!;
        PropertyInfo components = type.GetProperty("WeaponComponents")!;
        PropertyInfo flags = type.GetProperty("Flags")!;
        object Packet(int id, Dictionary<uint, bool>? values, bool full = true)
        {
            object packet = CodecSample(reader);
            type.GetProperty("ID")!.SetValue(packet, id);
            type.GetProperty("CurrentWeaponHash")!.SetValue(packet, (uint)(1000 + id));
            components.SetValue(packet, values);
            if (!full)
                flags.SetValue(packet, Enum.ToObject(flags.PropertyType,
                    Convert.ToInt32(flags.GetValue(packet)) & ~Convert.ToInt32(Enum.Parse(flags.PropertyType, "IsFullSync"))));
            return packet;
        }

        object first = Packet(71, new Dictionary<uint, bool> { [123] = true, [234] = false });
        object absent = Packet(72, null);
        object received = Activator.CreateInstance(type)!;
        CodecRoundtrip(first, received);
        object previous = components.GetValue(received)!;
        CodecRoundtrip(absent, received);
        if (!guarded)
        {
            Assert(ReferenceEquals(components.GetValue(received), previous) &&
                (int)type.GetProperty("ID")!.GetValue(received)! == 72,
                "The actual original reused PedSync reader carries the prior ped's components across a full packet with an absent component set.");
            return;
        }
        Assert(components.GetValue(received) == null && (int)type.GetProperty("ID")!.GetValue(received)! == 72 &&
            (uint)type.GetProperty("CurrentWeaponHash")!.GetValue(received)! == 1072,
            "A full packet with no components clears the previous ped's dictionary and retains its own ID and weapon.");

        object shortPacket = Packet(73, null, full: false);
        CodecRoundtrip(first, received);
        previous = components.GetValue(received)!;
        CodecRoundtrip(shortPacket, received);
        Assert(ReferenceEquals(components.GetValue(received), previous),
            "A short packet carries no component presence field and preserves the reusable full-state cache.");

        object empty = Packet(74, new Dictionary<uint, bool>());
        CodecRoundtrip(first, received);
        CodecRoundtrip(empty, received);
        Assert(components.GetValue(received) is Dictionary<uint, bool> { Count: 0 } &&
            CodecPack(empty).LengthBits == CodecPack(absent).LengthBits + 16,
            "An explicitly present empty dictionary remains distinct from absent and consumes its ushort count.");

        object replacement = Packet(75, new Dictionary<uint, bool> { [345] = true });
        CodecRoundtrip(first, received);
        CodecRoundtrip(replacement, received);
        Assert(components.GetValue(received) is Dictionary<uint, bool> actual && actual.Count == 1 && actual[345],
            "A later full nonempty dictionary replaces all components of the prior entity.");
        CodecRoundtrip(absent, received);
        Assert(components.GetValue(received) == null, "Another absent full set clears the replacement dictionary too.");

        foreach (object packet in new[] { absent, empty, replacement, shortPacket })
        {
            NetOutgoingMessage wire = CodecPack(packet);
            for (int length = 8; length < wire.LengthBits; length++)
            {
                CodecRoundtrip(first, received);
                NetIncomingMessage input = CodecIncoming(wire);
                input.LengthBits = length; input.ReadByte();
                CodecReject(() => CodecDeserialize(received, input), "A truncated optional PedSync shape consumed backing-buffer data.");
                Assert(input.Position <= input.LengthBits, "Optional component handling retains the packet reader's bit bounds.");
            }
            CodecRoundtrip(packet, received);
        }
        Console.WriteLine("PASS reused actual PedSync codec: absent/nonempty/empty full sets, different entity IDs, short-state preservation and every-bit truncation.");
    }
}
