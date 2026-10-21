using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using CoopControl.Shared;
using GTA.Math;
using Lidgren.Network;

internal static partial class Program
{
    private static void CheckPacketTruncation()
    {
        foreach (MethodInfo reader in EngineCodecGuard.FindPacketReaders())
        {
            object sample = CodecSample(reader);
            NetOutgoingMessage wire = CodecPack(sample);
            object received = CodecSample(reader);
            CodecRoundtrip(sample, received);
            for (int length = 8; length < wire.LengthBits; length++)
            {
                NetIncomingMessage input = CodecIncoming(wire);
                input.LengthBits = length;
                input.ReadByte();
                CodecReject(() => CodecDeserialize(received, input), "A truncated " + reader.DeclaringType!.Name + " payload consumed backing-buffer data.");
                Assert(input.Position <= input.LengthBits, "A rejected packet cannot advance past its declared bits.");
            }
        }
        foreach (bool prefix in new[] { false, true })
        {
            object[] values = { (byte)7, (short)-2, (ushort)3, 4, 5u, 6L, 7UL, 8f, true, "Проверка", new Vector3(1, 2, 3),
                new Quaternion(1, 2, 3, 4), new GTA.Model(123), new Vector2(1, 2), new byte[] { 1, 2 } };
            foreach (object value in values)
            {
                object packet = CodecEvent(new[] { value });
                NetOutgoingMessage wire = CodecOutgoing();
                if (prefix) { wire.Write(true); }
                packet.GetType().GetMethod("Serialize", CodecMethods)!.Invoke(packet, new object[] { wire });
                int start = prefix ? 1 : 0;
                for (int length = start; length < wire.LengthBits; length++)
                {
                    NetIncomingMessage input = CodecIncoming(wire); input.LengthBits = length; input.Position = start;
                    CodecReject(() => CodecDeserialize(CodecEvent(Array.Empty<object>()), input), "Truncated typed custom-event argument was accepted.");
                    Assert(input.Position <= input.LengthBits, "Custom-event bounds apply at aligned and unaligned starting positions.");
                }
            }
        }
        foreach (byte[] invalid in new[] { new byte[] { 0x80 }, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff },
            new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 }, new byte[] { 3, 65 } })
        {
            NetOutgoingMessage wire = CodecOutgoing(); wire.Write(715); wire.Write(1); wire.Write((byte)16); wire.Write(invalid);
            CodecReject(() => CodecDeserialize(CodecEvent(Array.Empty<object>()), CodecIncoming(wire)), "Malformed string length was accepted.");
        }
        MethodInfo players = EngineCodecGuard.FindPacketReaders().Single(method => method.DeclaringType!.Name == "HandshakeSuccess");
        foreach (int count in new[] { -1, int.MaxValue, 1 })
        {
            NetOutgoingMessage wire = CodecOutgoing(); wire.Write(count);
            CodecReject(() => CodecDeserialize(Activator.CreateInstance(players.DeclaringType!)!, CodecIncoming(wire)),
                "An impossible player count allocated its array before payload validation.");
        }
        Console.WriteLine("PASS all 24 actual Core packet readers reject every bit-truncated payload; all custom-event scalar/vector/string tags reject aligned and unaligned truncation.");
        CheckNonFiniteSnapshots();
    }

    private static void CheckNonFiniteSnapshots()
    {
        foreach (MethodInfo reader in EngineCodecGuard.FindPacketReaders().Where(reader => new[]
            { "PedSync", "VehicleSync", "ProjectileSync", "PlayerInfoUpdate", "BulletShot", "VehicleBulletShot" }.Contains(reader.DeclaringType!.Name)))
        {
            object sample = CodecSample(reader);
            foreach (PropertyInfo property in sample.GetType().GetProperties().Where(property =>
                property.PropertyType == typeof(float) || property.PropertyType == typeof(Vector3) || property.PropertyType == typeof(Quaternion)))
            {
                object original = property.GetValue(sample)!;
                PropertyInfo? flags = sample.GetType().Name == "PedSync" && property.Name == "Position" ? sample.GetType().GetProperty("Flags") : null;
                object? priorFlags = flags?.GetValue(sample);
                if (flags != null)
                    flags.SetValue(sample, Enum.ToObject(flags.PropertyType, Convert.ToInt32(priorFlags) & ~Convert.ToInt32(Enum.Parse(flags.PropertyType, "IsRagdoll"))));
                foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    object value = property.PropertyType == typeof(float) ? invalid : property.PropertyType == typeof(Vector3) ?
                        new Vector3(invalid, 0, 0) : new Quaternion(0, 0, 0, invalid);
                    property.SetValue(sample, value);
                    NetIncomingMessage input = CodecIncoming(CodecPack(sample)); input.ReadByte();
                    CodecReject(() => CodecDeserialize(CodecSample(reader), input), "A non-finite snapshot value was accepted.");
                }
                property.SetValue(sample, original);
                if (flags != null) { flags.SetValue(sample, priorFlags); }
            }
        }
        // Arbitrary custom events retain their existing scalar contract.
        CodecRoundtrip(CodecEvent(new object[] { float.NaN, float.PositiveInfinity }), CodecEvent(Array.Empty<object>()));
        foreach (int invalid in new[] { -1, 4 })
        {
            NetIncomingMessage input = CodecIncoming(CodecPack(CodecVoice(new byte[3], invalid))); input.ReadByte();
            CodecReject(() => CodecDeserialize(CodecVoice(Array.Empty<byte>(), 0), input), "Voice accepted Recorded outside its actual buffer.");
        }
        Console.WriteLine("PASS real snapshot readers reject NaN/Infinity before queueing or game calls; Voice.Recorded cannot exceed its buffer.");
    }

    private static object CodecSample(MethodInfo reader)
    {
        Type type = reader.DeclaringType!;
        object packet = type.Name == "ChatMessage" ? Activator.CreateInstance(type, new object[] { new Func<string, byte[]>(Encoding.UTF8.GetBytes) })! : Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in type.GetProperties().Where(property => property.CanWrite))
        {
            if (property.PropertyType == typeof(string)) { property.SetValue(packet, "text"); }
            if (property.PropertyType == typeof(byte[])) { property.SetValue(packet, new byte[type.Name == "PedSync" ? 36 : type.Name == "VehicleSync" ? 2 : 3]); }
        }
        foreach (FieldInfo field in type.GetFields().Where(field => field.FieldType == typeof(byte[]))) { field.SetValue(packet, new byte[3]); }
        if (type.Name == "ChatMessage") { return CodecSampleChat(packet); }
        if (type.Name == "Handshake") { type.GetProperty("InternalEndPoint")!.SetValue(packet, new IPEndPoint(IPAddress.Loopback, 4499)); }
        if (type.Name == "HandshakeSuccess")
        {
            PropertyInfo property = type.GetProperty("Players")!;
            Array array = Array.CreateInstance(property.PropertyType.GetElementType()!, 2);
            for (int index = 0; index < array.Length; index++)
            {
                object player = Activator.CreateInstance(property.PropertyType.GetElementType()!)!;
                player.GetType().GetField("ID")!.SetValue(player, 71 + index);
                player.GetType().GetField("Username")!.SetValue(player, "Игрок" + index);
                array.SetValue(player, index);
            }
            property.SetValue(packet, array);
        }
        if (type.Name is "PedSync" or "VehicleSync")
        {
            PropertyInfo flags = type.GetProperty("Flags")!;
            string[] names = type.Name == "PedSync" ? new[] { "IsFullSync", "IsAiming", "IsRagdoll" } : new[] { "IsFullSync", "IsAircraft", "HasRoof", "IsDeluxoHovering" };
            int value = names.Select(name => Convert.ToInt32(Enum.Parse(flags.PropertyType, name))).Aggregate(0, (left, right) => left | right);
            flags.SetValue(packet, Enum.ToObject(flags.PropertyType, value));
        }
        if (type.Name == "PedSync")
        {
            type.GetProperty("WeaponComponents")!.SetValue(packet, new Dictionary<uint, bool> { [123] = true, [234] = false });
            PropertyInfo color = type.GetProperty("BlipColor")!; color.SetValue(packet, Enum.ToObject(color.PropertyType, 1));
        }
        if (type.Name == "VehicleSync")
        {
            type.GetProperty("Mods")!.SetValue(packet, new Dictionary<int, int> { [1] = 2 });
            PropertyInfo damage = type.GetProperty("DamageModel")!;
            object value = Activator.CreateInstance(damage.PropertyType)!;
            value.GetType().GetProperty("BrokenDoors")!.SetValue(value, (byte)1);
            damage.SetValue(packet, value);
        }
        return packet;
    }

    private static object CodecSampleChat(object packet)
    {
        // Both delegates are part of the installed type's constructor contract.
        packet.GetType().GetField("decrypt", CodecMethods)!.SetValue(packet, new Func<byte[], byte[]>(bytes => bytes));
        return packet;
    }
}
