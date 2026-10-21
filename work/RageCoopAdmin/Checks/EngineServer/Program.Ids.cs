using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.IO;
using System.Threading;
using CoopAdmin.Server;
using GTA.Math;
using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server;
using RageCoop.Server.Scripting;

internal static partial class Program
{
    private static byte PacketByte(string name) => Convert.ToByte(Enum.Parse(typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!, name));

    private static void CheckIdentityReservations()
    {
        using var fixture = new Fixture();
        object entities = Field(fixture.Server, "Entities");
        MethodInfo update = entities.GetType().GetMethods(All).Single(method => method.Name == "Update" &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType.Name == "PedSync");
        object packet = Packet("PedSync");
        Set(packet, "ID", fixture.Second.Player.ID);
        Set(packet, "Position", new Vector3(91, 92, 93));
        Set(packet, "Health", 123);
        Vector3 before = fixture.Second.Player.Position;
        Invoke(update, entities, packet, fixture.First);
        Assert(fixture.Second.Player.Position == before && ReferenceEquals(fixture.Second.Player.Owner, fixture.Second),
            "the actual entity Update rejects another player's reserved ID before changing its position or owner");
        var output = fixture.FirstPeer.CreateMessage();
        Call(packet, "Pack", output);
        fixture.FirstPeer.SendMessage(output, NetDeliveryMethod.ReliableOrdered, 10);
        // A later reliable request on the same channel is a processing barrier.
        object legal = Packet("PedSync");
        Set(legal, "ID", fixture.First.Player.ID);
        Set(legal, "Position", new Vector3(5, 6, 7));
        output = fixture.FirstPeer.CreateMessage(); Call(legal, "Pack", output);
        fixture.FirstPeer.SendMessage(output, NetDeliveryMethod.ReliableOrdered, 10);
        WaitUntil(() => { fixture.ThrowPumpFailure(); return fixture.SyncPackets.Any(value => value.Peer == fixture.SecondPeer && value.Id == fixture.First.Player.ID); },
            "the legal player snapshot did not reach its loopback partner");
        Assert(!fixture.SyncPackets.Any(value => value.Id == fixture.Second.Player.ID) && fixture.Second.Player.Position == before,
            "the real ProcessMessage path never forwards or queues a snapshot for another player's reserved ID");
        WaitUntil(() => fixture.First.Player.Position == new Vector3(5, 6, 7), "the legal self snapshot did not run on the worker");
        object vehicle = Packet("VehicleSync");
        Set(vehicle, "ID", fixture.Second.Player.ID);
        Call(fixture.Server, "VehicleSync", vehicle, fixture.First);
        Assert(fixture.Server.API.Entities.GetVehicleByID(fixture.Second.Player.ID) == null,
            "a vehicle snapshot cannot reuse a reserved player ID in another pool");
        Set(packet, "ID", 0); Invoke(update, entities, packet, fixture.First);
        Assert(fixture.Server.API.Entities.GetPedByID(0) == null, "zero does not create a network entity");
        fixture.ThrowPumpFailure();
    }

    private static void CheckHandshakeReservations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ragecoop-handshake-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string previous = Environment.CurrentDirectory;
        var peers = new List<NetClient>();
        Logger logger = Construct<Logger>(true, false);
        logger.UseConsole = true;
        var server = new Server(new Settings { Port = 0, AnnounceSelf = false, AutoUpdate = false, UseP2P = false, LogLevel = 4 }, logger);
        try
        {
            Environment.CurrentDirectory = directory;
            server.Start();
            var transport = (NetServer)Field(server, "MainNetServer");
            NetClient Connect(int id, string name, bool malformedPassword = false)
            {
                var peer = new NetClient(new NetPeerConfiguration("623c92c287cc392406e7aaaac1c0f3b0") { LocalAddress = IPAddress.Loopback });
                peers.Add(peer); peer.Start();
                object handshake = Packet("Handshake");
                Set(handshake, "PedID", id); Set(handshake, "Username", name);
                Set(handshake, "ModVersion", "1.5.12.15");
                Set(handshake, "InternalEndPoint", new IPEndPoint(IPAddress.Loopback, peer.Port));
                using Aes aes = Aes.Create();
                var rsa = (RSA)Field(Field(server, "Security"), "RSA");
                handshake.GetType().GetField("AesKeyCrypted")!.SetValue(handshake, rsa.Encrypt(aes.Key, RSAEncryptionPadding.Pkcs1));
                handshake.GetType().GetField("AesIVCrypted")!.SetValue(handshake, rsa.Encrypt(aes.IV, RSAEncryptionPadding.Pkcs1));
                using ICryptoTransform cipher = aes.CreateEncryptor();
                Set(handshake, "PasswordEncrypted", malformedPassword ? new byte[] { 1 } : cipher.TransformFinalBlock(Array.Empty<byte>(), 0, 0));
                var hail = peer.CreateMessage(); Call(handshake, "Pack", hail);
                peer.Connect(new IPEndPoint(IPAddress.Loopback, transport.Port), hail);
                return peer;
            }
            var receivedStatuses = new Dictionary<NetClient, NetConnectionStatus>();
            bool HasStatus(NetClient peer, NetConnectionStatus status)
            {
                NetIncomingMessage? message;
                while ((message = peer.ReadMessage()) != null)
                {
                    if (message.MessageType == NetIncomingMessageType.StatusChanged)
                        receivedStatuses[peer] = (NetConnectionStatus)message.ReadByte();
                    peer.Recycle(message);
                }
                // NetClient.ConnectionStatus is Disconnected before its outgoing
                // handshake starts; only an observed notification proves denial.
                return receivedStatuses.TryGetValue(peer, out NetConnectionStatus actual) && actual == status;
            }
            NetClient first = Connect(51001, "First");
            WaitUntil(() => HasStatus(first, NetConnectionStatus.Connected) && server.API.GetAllClients().Count == 1, "the genuine initial handshake did not complete");
            Client original = server.API.GetAllClients()[51001];
            ServerPed npc = Construct<ServerPed>(server); Set(npc, "ID", 51002); Set(npc, "Owner", original);
            Call(Field(server, "Entities"), "Add", npc);
            foreach ((int id, string name) in new[] { (51001, "PlayerCollision"), (51002, "NpcCollision"), (0, "ZeroId") })
            {
                NetClient denied = Connect(id, name);
                WaitUntil(() => HasStatus(denied, NetConnectionStatus.Disconnected), "the colliding handshake was not denied");
                Assert(server.API.GetAllClients().Count == 1 && ReferenceEquals(server.API.GetAllClients()[51001], original) &&
                    ReferenceEquals(server.API.Entities.GetPedByID(51001), original.Player) && ReferenceEquals(server.API.Entities.GetPedByID(51002), npc),
                    "a genuine handshake with a reserved or zero ID is denied without replacing an existing player, NPC or client index");
            }
            object security = Field(server, "Security");
            var secured = (IDictionary)Field(security, "SecuredConnections");
            NetClient malformed = Connect(51004, "BadCipher", malformedPassword: true);
            WaitUntil(() => HasStatus(malformed, NetConnectionStatus.Disconnected), "the malformed cipher handshake was not denied");
            WaitUntil(() => !(bool)Call(security, "HasSecuredConnection", new IPEndPoint(IPAddress.Loopback, malformed.Port))!,
                "the rejected cipher retained its crypto session");
            Assert(secured.Count == 1 && server.API.GetAllClients().Count == 1,
                "a genuine handshake with valid RSA keys and an invalid password cipher rolls its AES publication back without changing the accepted player");
            Aes? replacement = null;
            server.API.Events.OnPlayerHandshake += (_, e) =>
            {
                if (e.Username != "Denied" && e.Username != "Replaced") { return; }
                if (e.Username == "Replaced")
                {
                    using Aes next = Aes.Create();
                    var rsa = (RSA)Field(security, "RSA");
                    Call(security, "AddConnection", e.EndPoint, rsa.Encrypt(next.Key, RSAEncryptionPadding.Pkcs1), rsa.Encrypt(next.IV, RSAEncryptionPadding.Pkcs1));
                    replacement = (Aes)secured[e.EndPoint]!;
                }
                e.Deny("isolated refusal");
            };
            NetClient deniedByEvent = Connect(51005, "Denied");
            WaitUntil(() => HasStatus(deniedByEvent, NetConnectionStatus.Disconnected), "the handshake callback refusal was not delivered");
            WaitUntil(() => !(bool)Call(security, "HasSecuredConnection", new IPEndPoint(IPAddress.Loopback, deniedByEvent.Port))!,
                "the callback-refused handshake retained its crypto session");
            Assert(secured.Count == 1, "an explicit handshake callback refusal also rolls its AES publication back");
            NetClient replaced = Connect(51006, "Replaced");
            WaitUntil(() => HasStatus(replaced, NetConnectionStatus.Disconnected), "the replacement refusal was not delivered");
            var replacedEndpoint = new IPEndPoint(IPAddress.Loopback, replaced.Port);
            Assert(replacement != null && ReferenceEquals(secured[replacedEndpoint], replacement),
                "rollback removes only the exact AES installed by that handshake and preserves a later replacement at the same endpoint");
            Call(security, "RemoveConnection", replacedEndpoint);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            Call(server, "QueueJob", new Action(() => { entered.Set(); release.Wait(); }));
            Assert(entered.Wait(2000), "the real worker pauses before an unreserved NPC snapshot");
            try
            {
                object queuedNpc = Packet("PedSync");
                Set(queuedNpc, "ID", 51003); Set(queuedNpc, "OwnerID", 123456);
                Call(server, "PedSync", queuedNpc, original);
                Assert((int)Property(queuedNpc, "OwnerID") == original.Player.ID,
                    "the server canonicalizes a relayed owner ID to the authenticated sender");
                NetClient second = Connect(51003, "Second");
                WaitUntil(() => HasStatus(second, NetConnectionStatus.Connected) && server.API.GetAllClients().Count == 2,
                    "a fresh legal ID did not complete the genuine handshake after denials");
                Client joined = server.API.GetAllClients()[51003];
                Call(server, "QueueJob", new Action(finished.Set));
                release.Set();
                Assert(finished.Wait(2000) && ReferenceEquals(server.API.Entities.GetPedByID(51003), joined.Player) &&
                    ReferenceEquals(joined.Player.Owner, joined) && ReferenceEquals(original.Player.Owner, original),
                    "a queued NPC snapshot cannot overwrite the player who reserves that ID before its actual Update runs");
            }
            finally { release.Set(); }
        }
        finally
        {
            server.Stop();
            foreach (NetClient peer in peers) { peer.Shutdown("check finished"); }
            WaitUntil(() => peers.All(peer => peer.Status == NetPeerStatus.NotRunning), "handshake client sockets did not close");
            Environment.CurrentDirectory = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckStaleDisconnect()
    {
        using var fixture = new Fixture();
        Client previous = fixture.First;
        Call(fixture.Server, "PlayerDisconnected", previous);
        Client replacement = Construct<Client>(fixture.Server);
        Set(replacement, "Username", previous.Username);
        long handle = (long)Field(previous, "NetHandle");
        NetConnection nextConnection = Construct<NetConnection>(fixture.Transport, new IPEndPoint(IPAddress.Loopback, 42115));
        typeof(NetConnection).GetField("m_remoteUniqueIdentifier", All)!.SetValue(nextConnection, handle);
        Set(replacement, "Connection", nextConnection);
        typeof(Client).GetField("NetHandle", All)!.SetValue(replacement, handle);
        ServerPed ped = Construct<ServerPed>(fixture.Server);
        Set(ped, "ID", previous.Player.ID); Set(ped, "Owner", replacement); Set(replacement, "Player", ped);
        Set(replacement, "IsReady", true);
        ((IDictionary)Field(fixture.Server, "ClientsByNetHandle")).Add(handle, replacement);
        ((IDictionary)Field(fixture.Server, "ClientsByID")).Add(ped.ID, replacement);
        ((IDictionary)Field(fixture.Server, "ClientsByName")).Add(replacement.Username.ToLowerInvariant(), replacement);
        Call(Field(fixture.Server, "Entities"), "Add", ped);
        MethodInfo create = typeof(NetPeer).GetMethod("CreateIncomingMessage", All, null,
            new[] { typeof(NetIncomingMessageType), typeof(int) }, null)!;
        var stale = (NetIncomingMessage)Invoke(create, fixture.Transport, NetIncomingMessageType.StatusChanged, 1)!;
        stale.Write((byte)NetConnectionStatus.Disconnected);
        typeof(NetIncomingMessage).GetField("m_senderConnection", All)!.SetValue(stale, Property(previous, "Connection"));
        Call(fixture.Server, "ProcessMessage", stale);
        Assert(stale.Data == null && replacement.IsReady && ReferenceEquals(fixture.Server.API.GetAllClients()[ped.ID], replacement),
            "the real message path drops and recycles a stale connection notification instead of disconnecting its replacement");
        Call(fixture.Server, "PlayerDisconnected", previous);
        Assert(ReferenceEquals(fixture.Server.API.GetAllClients()[ped.ID], replacement) && replacement.IsReady &&
            ReferenceEquals(fixture.Server.API.Entities.GetPedByID(ped.ID), ped),
            "a delayed duplicate disconnect for an old Client cannot remove the replacement session with the same identifiers");
    }
}
