using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GTA.Math;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static partial class Program
{
    private const string PatchId = "friendslegacy.ragecoop.enginepacket";
    private static readonly MethodInfo Deserialize = EnginePacketGuard.FindTarget() ?? throw new InvalidOperationException("Installed vehicle deserializer missing.");
    private static readonly Type Packet = Deserialize.DeclaringType!;
    private static readonly Type Damage = Packet.GetProperty("DamageModel")!.PropertyType;

    private static int Main()
    {
        try
        {
            CheckIngress();
            object reused = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Networking+ReceivedPackets", true)!
                .GetField("VehicelPacket", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            Assert(reused.GetType() == Packet, "The check uses the exact singleton packet reused by the installed client.");
            Read(reused, Snapshot(71, full: true, damaged: true));
            Read(reused, Snapshot(71, full: true, damaged: false));
            Assert(BrokenDoors(reused) == 5, "The unpatched deserializer reproduces stale damage after an intact full packet.");
            CheckContract();
            CheckEnvelopeReads(patched: false);
            EnginePacketGuard.Install();
            EnginePacketGuard.Install();
            Assert(Harmony.GetPatchInfo(Deserialize)?.Transpilers.Count(patch => patch.owner == PatchId) == 1,
                "Repeated installation adds exactly one guarded packet patch.");
            CheckActualReceive(reused);
            CheckEnvelopeReads(patched: true);
            EnginePacketGuard.Uninstall();
            EnginePacketGuard.Uninstall();
            Assert(Harmony.GetPatchInfo(Deserialize)?.Owners.Contains(PatchId) != true,
                "Resource unload removes the packet patch.");
            Read(reused, Snapshot(71, full: true, damaged: true));
            Read(reused, Snapshot(72, full: true, damaged: false));
            Assert(BrokenDoors(reused) == 5, "Uninstall restores the installed reader, proving the patch lifecycle affects the actual method.");
            Console.WriteLine("Actual reused vehicle packet and bounded network envelope checks passed; native game and socket operations were not exercised.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { EnginePacketGuard.Uninstall(); }
    }

    private static void CheckContract()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(Deserialize);
        int marker = EnginePacketGuard.FindDamageMarker(Deserialize, original);
        Assert(marker >= 0, "The exact installed reader contains the supported full-only damage branch.");
        var method = new DynamicMethod("PacketRewriteCheck", typeof(void), Type.EmptyTypes);
        List<CodeInstruction> rewritten = EnginePacketGuard.ResetFullDamage(original, method.GetILGenerator()).ToList();
        Assert(rewritten.Count == original.Count + 5, "The patch inserts only one default damage assignment before the existing presence bit.");
        for (int index = 0; index < original.Count; index++)
        {
            CodeInstruction actual = rewritten[index < marker ? index : index + 5];
            Assert(actual.opcode == original[index].opcode && Equals(actual.operand, original[index].operand),
                "All existing deserialization instructions remain unchanged.");
        }
        bool rejected = false;
        try { EnginePacketGuard.ResetFullDamage(new[] { new CodeInstruction(OpCodes.Ret) }, method.GetILGenerator()).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "An unsupported packet body cannot receive a guessed patch.");
        List<CodeInstruction> changed = original.Select(code => new CodeInstruction(code)).ToList();
        changed[marker + 2].opcode = OpCodes.Brtrue_S;
        Assert(EnginePacketGuard.FindDamageMarker(Deserialize, changed) < 0,
            "A changed presence-branch meaning is rejected before patching.");
        changed = original.Select(code => new CodeInstruction(code)).ToList();
        int fullFlag = changed.FindIndex(code => code.LoadsConstant(8192));
        changed[fullFlag].operand = 4096;
        Assert(EnginePacketGuard.FindDamageMarker(Deserialize, changed) < 0,
            "A changed full-state gate is rejected before patching.");
        Assert(EnginePacketGuard.FindDamageMarker(typeof(Program).GetMethod(nameof(Main), BindingFlags.Static | BindingFlags.NonPublic)!, original) < 0,
            "A different method cannot receive the packet patch even with copied instructions.");
    }

    private static void CheckActualReceive(object reused)
    {
        Read(reused, Snapshot(71, full: true, damaged: true));
        object damaged = Packet.GetProperty("DamageModel")!.GetValue(reused)!;
        Assert(BrokenDoors(reused) == 5 && (byte)Damage.GetProperty("OpenedDoors")!.GetValue(damaged)! == 2 &&
            (byte)Damage.GetProperty("BrokenWindows")!.GetValue(damaged)! == 9 &&
            (short)Damage.GetProperty("BurstedTires")!.GetValue(damaged)! == 513 &&
            (byte)Damage.GetProperty("LeftHeadLightBroken")!.GetValue(damaged)! == 1 &&
            (byte)Damage.GetProperty("RightHeadLightBroken")!.GetValue(damaged)! == 1,
            "A real damaged packet retains every transported damage field.");
        Read(reused, Snapshot(71, full: false, damaged: false));
        Assert(Equals(Packet.GetProperty("DamageModel")!.GetValue(reused), damaged) &&
            (GTA.Math.Vector3)Packet.GetProperty("Position")!.GetValue(reused)! == new Vector3(11f, 12f, 13f),
            "A partial packet updates movement while preserving its unused previous full damage state.");
        Read(reused, Snapshot(71, full: true, damaged: false));
        Assert(IsClean(reused), "A clean full snapshot clears old damage for the same vehicle.");
        Read(reused, Snapshot(71, full: true, damaged: true));
        Read(reused, Snapshot(72, full: true, damaged: false));
        Assert(IsClean(reused) && (int)Packet.GetProperty("ID")!.GetValue(reused)! == 72,
            "A clean second vehicle cannot inherit damage from the singleton's previous vehicle.");
        Assert((byte)Packet.GetProperty("RadioStation")!.GetValue(reused)! == 7 &&
            (string)Packet.GetProperty("LicensePlate")!.GetValue(reused)! == "CHECK72" &&
            (int)Packet.GetProperty("Livery")!.GetValue(reused)! == 3,
            "Fields after the one-bit damage marker remain correctly aligned.");
        Read(reused, Snapshot(72, full: true, damaged: false));
        Assert(IsClean(reused), "Repeating a clean full snapshot remains clean.");
    }

    private static object Snapshot(int id, bool full, bool damaged)
    {
        object packet = Activator.CreateInstance(Packet)!;
        void Set(string name, object value) => Packet.GetProperty(name)!.SetValue(packet, value);
        Set("ID", id);
        Set("OwnerID", 11);
        Set("Flags", Enum.ToObject(Packet.GetProperty("Flags")!.PropertyType, full ? 8192 : 0));
        Set("Position", new Vector3(11f, 12f, 13f));
        Set("Colors", new byte[] { 12, 34 });
        Set("Mods", new Dictionary<int, int> { { 0, 2 } });
        Set("RadioStation", (byte)7);
        Set("LicensePlate", "CHECK" + id);
        Set("Livery", 3);
        object damage = Activator.CreateInstance(Damage)!;
        if (damaged)
        {
            Damage.GetProperty("BrokenDoors")!.SetValue(damage, (byte)5);
            Damage.GetProperty("OpenedDoors")!.SetValue(damage, (byte)2);
            Damage.GetProperty("BrokenWindows")!.SetValue(damage, (byte)9);
            Damage.GetProperty("BurstedTires")!.SetValue(damage, (short)513);
            Damage.GetProperty("LeftHeadLightBroken")!.SetValue(damage, (byte)1);
            Damage.GetProperty("RightHeadLightBroken")!.SetValue(damage, (byte)1);
        }
        Set("DamageModel", damage);
        return packet;
    }

    private static void Read(object reused, object outgoingPacket)
    {
        var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
        Packet.GetMethod("Pack")!.Invoke(outgoingPacket, new object[] { outgoing });
        var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
        incoming.Data = outgoing.Data;
        incoming.LengthBits = outgoing.LengthBits;
        incoming.ReadByte();
        Deserialize.Invoke(reused, new object[] { incoming });
        Assert(incoming.Position == incoming.LengthBits, "The real serializer and deserializer consume exactly the same payload bits.");
    }

    private static byte BrokenDoors(object packet) => (byte)Damage.GetProperty("BrokenDoors")!.GetValue(Packet.GetProperty("DamageModel")!.GetValue(packet))!;

    private static int _envelopeCallbacks;
    private static void CheckEnvelopeReads(bool patched)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo process = EnginePacketGuard.FindEnvelopeTarget()!;
        Assert(EnginePacketGuard.HasEnvelopeContract(process), "The exact installed network envelope contract matches.");
        IDictionary pending = (IDictionary)process.DeclaringType!.GetField("PendingResponses", flags)!.GetValue(null)!;
        Type callbackType = pending.GetType().GetGenericArguments()[1];
        Type[] parameters = callbackType.GetMethod("Invoke")!.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        var callback = new DynamicMethod("EnvelopeCallback", typeof(void), parameters, typeof(Program), true);
        ILGenerator il = callback.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(EnvelopeCallback), flags)!);
        il.Emit(OpCodes.Ret);
        const int requestId = 123456;
        pending[requestId] = callback.CreateDelegate(callbackType);
        NetIncomingMessage Incoming(NetIncomingMessageType type, byte[] data, int bits)
        {
            var message = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
            typeof(NetIncomingMessage).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(NetIncomingMessageType)).SetValue(message, type);
            message.Data = data;
            message.LengthBits = bits;
            return message;
        }
        byte[] storage = new byte[32];
        storage[0] = 7; // Response, followed by a readable stale backing-buffer tail.
        Array.Copy(BitConverter.GetBytes(requestId), 0, storage, 1, 4);
        storage[5] = 5;
        _envelopeCallbacks = 0;
        try
        {
            NetIncomingMessage truncated = Incoming(NetIncomingMessageType.Data, storage, 8);
            process.Invoke(null, new object[] { truncated });
            Assert(_envelopeCallbacks == (patched ? 0 : 1) && (!patched || truncated.Position <= truncated.LengthBits),
                patched ? "A truncated response cannot resolve an ID from stale backing-buffer bytes." :
                "The unpatched real reader reproduces a callback from bytes outside the declared response length.");
            if (!patched) { return; }
            for (int bits = 1; bits < 8; bits++)
            {
                NetIncomingMessage subByte = Incoming(NetIncomingMessageType.Data, storage, bits);
                process.Invoke(null, new object[] { subByte });
                Assert(subByte.Position == 0 && _envelopeCallbacks == 0, "A sub-byte Data envelope cannot consume its packet type.");
            }
            for (int bits = 9; bits < 48; bits++)
            {
                NetIncomingMessage partial = Incoming(NetIncomingMessageType.Data, storage, bits);
                process.Invoke(null, new object[] { partial });
                Assert(partial.Position <= partial.LengthBits && _envelopeCallbacks == 0,
                    "A partial response ID or subtype cannot dispatch a registered callback.");
            }
            byte[] requestStorage = (byte[])storage.Clone();
            requestStorage[0] = 6;
            NetIncomingMessage request = Incoming(NetIncomingMessageType.Data, requestStorage, 32);
            process.Invoke(null, new object[] { request });
            Assert(request.Position == 8, "An incomplete request ID is rejected before its integer read.");
            NetIncomingMessage complete = Incoming(NetIncomingMessageType.Data, storage, 48);
            process.Invoke(null, new object[] { complete });
            Assert(_envelopeCallbacks == 1 && complete.Position == 48, "A complete response envelope still reaches its actual registered callback.");
            NetIncomingMessage status = Incoming(NetIncomingMessageType.StatusChanged, new byte[32], 8);
            bool rejected = false;
            try { process.Invoke(null, new object[] { status }); }
            catch (TargetInvocationException error) when (error.InnerException is System.IO.InvalidDataException) { rejected = true; }
            Assert(rejected && status.Position == 8, "A missing status reason is rejected before its string length reads backing-buffer bytes.");
        }
        finally { pending.Remove(requestId); }
    }

    private static void EnvelopeCallback()
    {
        _envelopeCallbacks++;
        // The actual ProcessMessage catch retains ownership of this message;
        // no live peer or socket is needed to observe callback dispatch.
        throw new InvalidOperationException("Envelope callback observed.");
    }

    private static bool IsClean(object packet) => Equals(Packet.GetProperty("DamageModel")!.GetValue(packet), Activator.CreateInstance(Damage));
    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
