using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using CoopAdmin.Server;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server;

internal static class Program
{
    private static int Main()
    {
        try
        {
            MethodInfo target = EngineRelayGuard.FindTarget() ?? throw new InvalidOperationException("Actual server relay missing.");
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            int gate = EngineRelayGuard.FindRelayDelivery(original);
            Assert(gate >= 0, "the exact installed server has the verified unreliable sync-event relay");
            List<CodeInstruction> rewritten = EngineRelayGuard.PreserveOwnerDelivery(original).ToList();
            Assert(rewritten.Count == original.Count + 4, "the relay patch changes one delivery argument");
            for (int index = 0; index < original.Count; index++)
            {
                if (index == gate) { continue; }
                CodeInstruction actual = rewritten[index < gate ? index : index + 4];
                if (actual.opcode != original[index].opcode || !Equals(actual.operand, original[index].operand))
                {
                    throw new InvalidOperationException("An unrelated server instruction changed.");
                }
            }
            Assert(EngineRelayGuard.GetDelivery(EngineRelayGuard.OwnerChangedPacketType) == (int)NetDeliveryMethod.ReliableOrdered &&
                EngineRelayGuard.GetDelivery(EngineRelayGuard.ReadPacketType("BulletShot")) == (int)NetDeliveryMethod.UnreliableSequenced &&
                EngineRelayGuard.GetDelivery(EngineRelayGuard.ReadPacketType("PedKilled")) == (int)NetDeliveryMethod.UnreliableSequenced,
                "ownership alone gains reliability; ordinary sync-event policy remains unchanged");
            bool rejected = false;
            try { EngineRelayGuard.PreserveOwnerDelivery(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "an unsupported relay cannot receive a guessed IL change");
            EngineRelayGuard.Install();
            EngineRelayGuard.Install();
            CheckRealRelay(target);
            EngineRelayGuard.Uninstall();
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.enginerelay") != true,
                "resource unload removes its server relay patch");
            Console.WriteLine("Engine relay checks passed with isolated loopback peers; no game or external account was used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { EngineRelayGuard.Uninstall(); }
    }

    private static void CheckRealRelay(MethodInfo process)
    {
        string application = "friendslegacy.enginecheck." + Guid.NewGuid().ToString("N");
        var transport = new NetServer(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback, Port = 0, MaximumConnections = 3 });
        var sender = new NetClient(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback });
        var receiver = new NetClient(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback });
        try
        {
            transport.Start();
            sender.Start();
            receiver.Start();
            var endpoint = new IPEndPoint(IPAddress.Loopback, transport.Port);
            sender.Connect(endpoint);
            receiver.Connect(endpoint);
            WaitUntil(() =>
            {
                Drain(transport);
                Drain(sender);
                Drain(receiver);
                return transport.ConnectionsCount == 2 && sender.ConnectionStatus == NetConnectionStatus.Connected &&
                    receiver.ConnectionStatus == NetConnectionStatus.Connected;
            }, "isolated peers did not connect");

            var server = (Server)RuntimeHelpers.GetUninitializedObject(typeof(Server));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Server).GetField("MainNetServer", fields)!.SetValue(server, transport);
            typeof(Server).GetField("Settings", fields)!.SetValue(server, new Settings { UseP2P = false });
            var clients = new Dictionary<long, Client>();
            foreach (NetConnection connection in transport.Connections)
            {
                var client = (Client)RuntimeHelpers.GetUninitializedObject(typeof(Client));
                typeof(Client).GetProperty("Connection", fields)!.SetValue(client, connection);
                clients.Add(connection.RemoteUniqueIdentifier, client);
            }
            typeof(Server).GetField("ClientsByNetHandle", fields)!.SetValue(server, clients);
            Drain(transport);
            Drain(sender);
            Drain(receiver);

            NetOutgoingMessage outgoing = sender.CreateMessage();
            outgoing.Write(EngineRelayGuard.OwnerChangedPacketType);
            outgoing.Write(715);
            outgoing.Write(22);
            sender.SendMessage(outgoing, NetDeliveryMethod.ReliableOrdered, 12);
            NetIncomingMessage incoming = WaitData(transport);
            int originalLength = incoming.LengthBits;
            process.Invoke(server, new object[] { incoming });
            NetIncomingMessage delivered = WaitData(receiver);
            Assert(delivered.DeliveryMethod == NetDeliveryMethod.ReliableOrdered && delivered.SequenceChannel == 12 &&
                delivered.ReadByte() == EngineRelayGuard.OwnerChangedPacketType && delivered.ReadInt32() == 715 && delivered.ReadInt32() == 22 &&
                delivered.LengthBits == originalLength,
                "the actual patched Server.ProcessMessage relays ownership reliably with unchanged channel and payload");
            receiver.Recycle(delivered);

            NetOutgoingMessage ordinary = sender.CreateMessage();
            ordinary.Write(EngineRelayGuard.ReadPacketType("PedKilled"));
            ordinary.Write(715);
            sender.SendMessage(ordinary, NetDeliveryMethod.UnreliableSequenced, 12);
            process.Invoke(server, new object[] { WaitData(transport) });
            delivered = WaitData(receiver);
            Assert(delivered.DeliveryMethod == NetDeliveryMethod.UnreliableSequenced && delivered.SequenceChannel == 12 &&
                delivered.ReadByte() == EngineRelayGuard.ReadPacketType("PedKilled") && delivered.ReadInt32() == 715,
                "the same real relay preserves ordinary event delivery and bytes");
            receiver.Recycle(delivered);
        }
        finally
        {
            transport.Shutdown("isolated check finished");
            sender.Shutdown("isolated check finished");
            receiver.Shutdown("isolated check finished");
        }
    }

    private static NetIncomingMessage WaitData(NetPeer peer)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            NetIncomingMessage? message = peer.ReadMessage();
            if (message?.MessageType == NetIncomingMessageType.Data) { return message; }
            if (message != null) { peer.Recycle(message); }
            Thread.Sleep(5);
        }
        throw new TimeoutException("No isolated relay data arrived.");
    }

    private static void WaitUntil(Func<bool> predicate, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds >= 5000) { throw new TimeoutException(failure); }
            Thread.Sleep(5);
        }
    }

    private static void Drain(NetPeer peer)
    {
        NetIncomingMessage? message;
        while ((message = peer.ReadMessage()) != null) { peer.Recycle(message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
        Console.WriteLine("PASS " + message);
    }
}
