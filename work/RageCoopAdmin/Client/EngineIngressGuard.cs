using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using RageCoop.Core;

namespace RageCoopAdmin.Client;

// Direct peers exchange game snapshots. Resource/control messages belong only
// to the current server; a download name never authorizes leaving its root.
internal static class EngineIngressGuard
{
    private const string PatchId = "friendslegacy.ragecoop.engineingress";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly Engine = typeof(SyncedEntity).Assembly;
    private static readonly Type Network = Engine.GetType("RageCoop.Client.Networking", true)!;
    private static readonly Type Downloads = Engine.GetType("RageCoop.Client.DownloadManager", true)!;
    private static readonly FieldInfo Server = Network.GetField("ServerConnection", Static)!;
    private static readonly FieldInfo ConnectionPeer = typeof(NetConnection).GetField("m_peer", Instance)!;
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static Harmony? _harmony;
    private static string _ownerId = PatchId;

    internal static MethodInfo FindMessageTarget() => Network.GetMethod("ProcessMessage", Static, null, new[] { typeof(NetIncomingMessage) }, null)!;
    internal static MethodInfo FindDownloadTarget() => Downloads.GetMethod("AddFile", Static, null, new[] { typeof(int), typeof(string), typeof(long) }, null)!;

    internal static void Install(string ownerId = PatchId)
    {
        if (_harmony != null) return;
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("A patch owner is required.", nameof(ownerId));
        MethodInfo message = FindMessageTarget(), download = FindDownloadTarget();
        if (!HasSupportedContract()) throw new InvalidOperationException("Unsupported RageCoop incoming-message contract.");
        var harmony = new Harmony(ownerId);
        try
        {
            Targets.Add(message);
            harmony.Patch(message, prefix: new HarmonyMethod(typeof(EngineIngressGuard), nameof(BeforeMessage)) { priority = Priority.First });
            Targets.Add(download);
            harmony.Patch(download, prefix: new HarmonyMethod(typeof(EngineIngressGuard), nameof(BeforeDownload)));
        }
        catch
        {
            foreach (MethodInfo target in Targets) harmony.Unpatch(target, HarmonyPatchType.All, ownerId);
            Targets.Clear();
            throw;
        }
        _ownerId = ownerId;
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
            foreach (MethodInfo target in Targets) _harmony.Unpatch(target, HarmonyPatchType.All, _ownerId);
        Targets.Clear();
        _harmony = null;
    }

    internal static bool HasSupportedContract()
    {
        MethodInfo message = FindMessageTarget(), download = FindDownloadTarget();
        using SHA256 hash = SHA256.Create();
        byte[]? body = message.GetMethodBody()?.GetILAsByteArray();
        return message.ReturnType == typeof(void) && body != null &&
            BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() ==
                "2eb20f51ef3a68412335373ce6772092f3f84d3344188fed8aca4c555b74abea" &&
            Enum.GetValues(typeof(IngressPacketType)).Cast<IngressPacketType>().All(value =>
                Enum.GetName(typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!, (byte)value) == value.ToString()) &&
            download.ReturnType == typeof(bool) && Server.FieldType == typeof(NetConnection) &&
            ConnectionPeer.FieldType == typeof(NetPeer) && Downloads.GetProperty("ResourceFolder", Static)?.PropertyType == typeof(string) &&
            PatchProcessor.GetOriginalInstructions(download).Count(code => code.operand is MethodInfo method &&
                method.DeclaringType == Downloads && method.Name == "FileAlreadyExists") == 1;
    }

    private static bool BeforeMessage(NetIncomingMessage? __0)
    {
        if (__0 == null) return false;
        if (__0.MessageType != NetIncomingMessageType.Data && __0.MessageType != NetIncomingMessageType.StatusChanged) return true;
        NetConnection? sender = __0.SenderConnection;
        if (sender != null && ReferenceEquals(sender, Server.GetValue(null))) return true;
        if (__0.MessageType == NetIncomingMessageType.StatusChanged)
        {
            if (__0.LengthBits - __0.Position >= 8 && (NetConnectionStatus)__0.PeekByte() == NetConnectionStatus.Connected &&
                !IsRecognizedConnection(sender, incomingHandshake: true))
            {
                sender?.Disconnect("Unrecognized direct peer.");
                RecycleRejected(__0);
                return false;
            }
            return true;
        }
        if (__0.LengthBits - __0.Position >= 8 && IsPeerPacket((IngressPacketType)__0.PeekByte()) &&
            IsRecognizedConnection(sender, incomingHandshake: false)) return true;
        RecycleRejected(__0);
        return false;
    }

    private static bool IsPeerPacket(IngressPacketType type) => type is IngressPacketType.PedSync or IngressPacketType.VehicleSync or
        IngressPacketType.ProjectileSync or IngressPacketType.BulletShot or IngressPacketType.VehicleBulletShot or IngressPacketType.OwnerChanged or
        IngressPacketType.PedKilled or IngressPacketType.NozzleTransform or IngressPacketType.Voice;

    private static bool IsRecognizedConnection(NetConnection? connection, bool incomingHandshake)
    {
        if (connection == null) return false;
        Dictionary<int, RageCoop.Client.Player> players;
        try { players = API.Players; }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException) { return false; }
        if (players.Values.Any(player => ReferenceEquals(player.Connection, connection) && IsExpectedEndpoint(player, connection.RemoteEndPoint))) return true;
        if (!incomingHandshake || connection.RemoteHailMessage is not NetIncomingMessage hail || hail.LengthBits != 40) return false;
        long position = hail.Position;
        try
        {
            hail.Position = 0;
            if ((IngressPacketType)hail.ReadByte() != IngressPacketType.P2PConnect || !players.TryGetValue(hail.ReadInt32(), out RageCoop.Client.Player? player)) return false;
            return IsExpectedEndpoint(player, connection.RemoteEndPoint);
        }
        finally { hail.Position = position; }
    }

    private static bool IsExpectedEndpoint(RageCoop.Client.Player player, IPEndPoint? actual) => actual != null &&
        (actual.Equals(player.InternalEndPoint) || actual.Equals(player.ExternalEndPoint));

    private static void RecycleRejected(NetIncomingMessage message)
    {
        // A late message can belong to a previous peer. Return it to the source
        // peer's pool, never to the replacement Networking.Peer instance.
        if (message.SenderConnection is NetConnection sender && ConnectionPeer.GetValue(sender) is NetPeer peer)
            peer.Recycle(message);
    }

    private static void BeforeDownload(string __1, long __2)
    {
        if (__2 < 0) throw new InvalidDataException("A download length cannot be negative.");
        string root = (string)Downloads.GetProperty("ResourceFolder", Static)!.GetValue(null)!;
        ValidateDownloadPath(root, __1);
    }

    internal static void ValidateDownloadPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.IndexOf(':') >= 0 ||
            name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidDataException("The download name is invalid.");
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string destination = Path.GetFullPath(Path.Combine(canonicalRoot, name));
        if (!destination.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A download cannot leave the resource directory.");
        for (string? path = destination; path != null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A download cannot use a linked file or directory.");
            if (string.Equals(path, canonicalRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}

// Values are checked against the installed internal enum before patching.
internal enum IngressPacketType : byte
{
    PlayerConnect = 1, PlayerDisconnect = 2, PlayerInfoUpdate = 3, Request = 6, Response = 7,
    FileTransferChunk = 11, CustomEvent = 16, CustomEventQueued = 17, P2PConnect = 19,
    HolePunch = 21, Voice = 22, PedSync = 23, VehicleSync = 24, ProjectileSync = 25,
    PedKilled = 30, BulletShot = 31, VehicleBulletShot = 32, OwnerChanged = 35, NozzleTransform = 37
}
