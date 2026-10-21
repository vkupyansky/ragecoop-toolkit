using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using GTA;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Client;
using RageCoop.Core;
using RageCoopAdmin.Client;

internal static partial class Program
{
    private const BindingFlags IngressStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags IngressInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _ingressAccepted, _ingressRecycled, _ingressDisconnected;

    private static void CheckIngress()
    {
        var boundary = new Harmony("friendslegacy.check.ingressboundary");
        string directory = Path.Combine(Path.GetTempPath(), "CoopIngress-" + Guid.NewGuid().ToString("N"));
        Assembly engine = typeof(SyncedEntity).Assembly;
        Type network = engine.GetType("RageCoop.Client.Networking", true)!, downloads = engine.GetType("RageCoop.Client.DownloadManager", true)!;
        Type host = engine.GetType("RageCoop.Client.Main", true)!, settings = engine.GetType("RageCoop.Client.Settings", true)!;
        Type roster = engine.GetType("RageCoop.Client.PlayerList", true)!;
        object? oldSettings = host.GetField("Settings", IngressStatic)!.GetValue(null), oldLogger = host.GetField("Logger", IngressStatic)!.GetValue(null);
        object? oldServer = network.GetField("ServerConnection", IngressStatic)!.GetValue(null), oldPeer = network.GetField("Peer", IngressStatic)!.GetValue(null);
        IDictionary? players = null;
        void Patch(MethodBase method, string prefix) => boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), prefix));
        Directory.CreateDirectory(directory);
        try
        {
            Patch(typeof(Scaleform).GetMethod("RequestMovie", IngressStatic, null, new[] { typeof(string) }, null)!, nameof(IngressSkip));
            foreach (MethodInfo logger in typeof(Logger).GetMethods(IngressInstance).Where(method => method.Name is "Debug" or "Trace" or "Error" or "Info" or "Warning")) Patch(logger, nameof(IngressSkip));
            Patch(typeof(NetPeer).GetMethod("CreateMessage", IngressInstance, null, Type.EmptyTypes, null)!, nameof(IngressCreate));
            Patch(typeof(NetPeer).GetMethod("SendMessage", IngressInstance, null, new[] { typeof(NetOutgoingMessage), typeof(NetConnection), typeof(NetDeliveryMethod), typeof(int) }, null)!, nameof(IngressSkip));
            Patch(typeof(NetPeer).GetMethod("FlushSendQueue", IngressInstance)!, nameof(IngressSkip));
            Patch(typeof(NetPeer).GetMethod("Recycle", IngressInstance, null, new[] { typeof(NetIncomingMessage) }, null)!, nameof(IngressRecycle));
            Patch(typeof(NetConnection).GetMethod("Disconnect", IngressInstance, null, new[] { typeof(string) }, null)!, nameof(IngressDisconnect));
            Patch(network.GetMethod("HandlePacket", IngressStatic)!, nameof(IngressHandle));
            object config = Activator.CreateInstance(settings)!;
            settings.GetProperty("DataDirectory")!.SetValue(config, directory);
            settings.GetProperty("LastServerAddress")!.SetValue(config, "127.0.0.1:4499");
            host.GetField("Settings", IngressStatic)!.SetValue(null, config);
            host.GetField("Logger", IngressStatic)!.SetValue(null, FormatterServices.GetUninitializedObject(typeof(Logger)));
            Type peerType = network.GetField("Peer", IngressStatic)!.FieldType;
            var nativePeer = (NetPeer)FormatterServices.GetUninitializedObject(peerType);
            network.GetField("Peer", IngressStatic)!.SetValue(null, nativePeer);
            NetConnection Connection(int port)
            {
                var result = (NetConnection)FormatterServices.GetUninitializedObject(typeof(NetConnection));
                typeof(NetConnection).GetField("m_peer", IngressInstance)!.SetValue(result, nativePeer);
                typeof(NetConnection).GetField("m_remoteEndPoint", IngressInstance)!.SetValue(result, new IPEndPoint(IPAddress.Loopback, port));
                return result;
            }
            NetConnection server = Connection(4499), peer = Connection(4500), stranger = Connection(4501);
            network.GetField("ServerConnection", IngressStatic)!.SetValue(null, server);
            players = (IDictionary)roster.GetField("Players", IngressStatic)!.GetValue(null)!;
            var player = new RageCoop.Client.Player();
            typeof(RageCoop.Client.Player).GetProperty("ID")!.SetValue(player, 1234);
            typeof(RageCoop.Client.Player).GetProperty("Connection")!.SetValue(player, peer);
            typeof(RageCoop.Client.Player).GetProperty("ExternalEndPoint")!.SetValue(player, peer.RemoteEndPoint);
            players.Add(1234, player);
            RuntimeHelpers.RunClassConstructor(downloads.TypeHandle);
            string root = (string)downloads.GetProperty("ResourceFolder", IngressStatic)!.GetValue(null)!;
            Directory.CreateDirectory(root);
            string victim = Path.Combine(Path.GetDirectoryName(root)!, "victim.txt");
            File.WriteAllText(victim, "preserve this file");
            void Dispatch(NetConnection source, NetOutgoingMessage outgoing)
            {
                var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
                incoming.Data = outgoing.Data; incoming.LengthBits = outgoing.LengthBits;
                typeof(NetIncomingMessage).GetField("m_incomingMessageType", IngressInstance)!.SetValue(incoming, NetIncomingMessageType.Data);
                typeof(NetIncomingMessage).GetField("m_senderConnection", IngressInstance)!.SetValue(incoming, source);
                EngineIngressGuard.FindMessageTarget().Invoke(null, new object[] { incoming });
            }
            NetOutgoingMessage Message(string type, params (string Name, object Value)[] values)
            {
                Type packetType = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+" + type, true)!;
                object packet = Activator.CreateInstance(packetType)!;
                foreach (var value in values) packetType.GetProperty(value.Name)!.SetValue(packet, value.Value);
                var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
                if (type is "FileTransferRequest" or "FileTransferComplete") { outgoing.Write((byte)IngressPacketType.Request); outgoing.Write(41); }
                packetType.GetMethod("Pack")!.Invoke(packet, new object[] { outgoing });
                return outgoing;
            }
            void Request(NetConnection source, int id, string name, long length) => Dispatch(source,
                Message("FileTransferRequest", ("ID", id), ("Name", name), ("FileLength", length)));
            // Both the original envelope and original file handler run here.
            Request(stranger, 8101, "..\\victim.txt", 3L);
            Assert(new FileInfo(victim).Length == 0,
                "The real unguarded client lets an unknown peer traverse the download root and replace an existing file.");
            downloads.GetMethod("Cleanup", IngressStatic)!.Invoke(null, null);
            File.WriteAllText(victim, "preserve this file");
            Assert(EngineIngressGuard.HasSupportedContract(), "The ingress guard accepts the installed message/file contracts.");
            EngineIngressGuard.Install(); EngineIngressGuard.Install();
            Request(stranger, 8102, "..\\victim.txt", 3L);
            Request(peer, 8103, "..\\victim.txt", 3L);
            Assert(File.ReadAllText(victim) == "preserve this file", "Unknown and recognized direct peers cannot invoke server download handlers.");
            Request(server, 8104, "..\\victim.txt", 3L);
            Assert(File.ReadAllText(victim) == "preserve this file", "Even the server cannot delete a destination outside the download root.");
            foreach (string name in new[] { "C:\\outside.bin", "\\\\server\\share\\file", "file:stream", "..\\..\\outside.bin" })
            {
                bool rejected = false;
                try { EngineIngressGuard.FindDownloadTarget().Invoke(null, new object[] { 8105, name, 3L }); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
                Assert(rejected, "Rooted, alternate-stream and escaping download paths are rejected before filesystem mutation.");
            }
            _ingressAccepted = 0;
            foreach (IngressPacketType type in new[] { IngressPacketType.CustomEvent, IngressPacketType.CustomEventQueued, IngressPacketType.PlayerConnect, IngressPacketType.PlayerDisconnect, IngressPacketType.PlayerInfoUpdate, IngressPacketType.FileTransferChunk })
            {
                var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!; outgoing.Write((byte)type);
                Dispatch(peer, outgoing); Dispatch(stranger, outgoing);
            }
            Assert(_ingressAccepted == 0, "Peers cannot impersonate server custom events, roster changes or file chunks.");
            foreach (IngressPacketType type in new[] { IngressPacketType.PedSync, IngressPacketType.VehicleSync, IngressPacketType.ProjectileSync, IngressPacketType.BulletShot, IngressPacketType.OwnerChanged })
            {
                var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!; outgoing.Write((byte)type);
                Dispatch(peer, outgoing); Dispatch(stranger, outgoing);
            }
            Assert(_ingressAccepted == 5, "Only the introduced peer's actual connection retains direct movement, shots and ownership events.");
            Request(server, 8106, "nested\\allowed.bin", 3L);
            Dispatch(server, Message("FileTransferChunk", ("ID", 8106), ("FileChunk", new byte[] { 4, 5, 6 })));
            Dispatch(server, Message("FileTransferComplete", ("ID", 8106)));
            Assert(File.ReadAllBytes(Path.Combine(root, "nested", "allowed.bin")).SequenceEqual(new byte[] { 4, 5, 6 }),
                "A server request, real chunk handler and completion still download a nested file normally.");
            int recycled = _ingressRecycled;
            network.GetField("ServerConnection", IngressStatic)!.SetValue(null, Connection(4502));
            Request(server, 8107, "old-server.bin", 1L);
            Assert(!File.Exists(Path.Combine(root, "old-server.bin")) && _ingressRecycled == recycled + 1,
                "A late old-server message is refused and recycled once through its source peer.");
            var status = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
            status.Data = new byte[] { (byte)NetConnectionStatus.Connected }; status.LengthBits = 8;
            typeof(NetIncomingMessage).GetField("m_incomingMessageType", IngressInstance)!.SetValue(status, NetIncomingMessageType.StatusChanged);
            typeof(NetIncomingMessage).GetField("m_senderConnection", IngressInstance)!.SetValue(status, stranger);
            EngineIngressGuard.FindMessageTarget().Invoke(null, new object[] { status });
            Assert(_ingressDisconnected == 1, "An incoming connection without a known peer or hail is actively disconnected.");
            Console.WriteLine("Actual client envelope and download-path regression checks passed; socket sends and GTA rendering were isolated.");
        }
        finally
        {
            EngineIngressGuard.Uninstall();
            downloads.GetMethod("Cleanup", IngressStatic)!.Invoke(null, null);
            players?.Remove(1234);
            host.GetField("Settings", IngressStatic)!.SetValue(null, oldSettings); host.GetField("Logger", IngressStatic)!.SetValue(null, oldLogger);
            network.GetField("ServerConnection", IngressStatic)!.SetValue(null, oldServer); network.GetField("Peer", IngressStatic)!.SetValue(null, oldPeer);
            boundary.UnpatchAll(boundary.Id);
            Directory.Delete(directory, true);
        }
    }

    private static bool IngressSkip() => false;
    private static bool IngressCreate(ref NetOutgoingMessage __result) { __result = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!; return false; }
    private static bool IngressRecycle() { _ingressRecycled++; return false; }
    private static bool IngressDisconnect() { _ingressDisconnected++; return false; }
    private static bool IngressHandle(object __0)
    {
        if (Convert.ToByte(__0) == (byte)IngressPacketType.FileTransferChunk) return true;
        _ingressAccepted++;
        return false;
    }
}
