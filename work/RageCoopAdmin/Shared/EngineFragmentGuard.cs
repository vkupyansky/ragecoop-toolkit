using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Lidgren.Network;

namespace CoopControl.Shared;

// Bound the installed reassembler before it allocates or copies. Its existing
// fragment storage, completion and application delivery remain authoritative.
internal static class EngineFragmentGuard
{
    private const string PatchId = "friendslegacy.ragecoop.transportfragments";
    internal const int MaximumMessageBytes = 1024 * 1024;
    internal const int MaximumConnectionBytes = 4 * 1024 * 1024;
    internal const int MaximumPeerBytes = 16 * 1024 * 1024;
    internal const int MaximumConnectionGroups = 32;
    internal const int MaximumPeerGroups = 256;
    internal const int LifetimeSeconds = 30;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo NativeGroups = typeof(NetPeer).GetField("m_receivedFragmentGroups", Instance)!;
    private static readonly Type NativeGroupType = typeof(NetPeer).Assembly.GetType("Lidgren.Network.ReceivedFragmentGroup", true)!;
    private static readonly FieldInfo GroupData = NativeGroupType.GetField("Data", Instance)!;
    private static readonly FieldInfo GroupChunks = NativeGroupType.GetField("ReceivedChunks", Instance)!;
    private static readonly FieldInfo ConnectionPeer = typeof(NetConnection).GetField("m_peer", Instance)!;
    private static readonly FieldInfo ConnectionStatus = typeof(NetConnection).GetField("m_status", Instance)!;
    private static readonly FieldInfo StoragePool = typeof(NetPeer).GetField("m_storagePool", Instance)!;
    private static readonly FieldInfo StorageBytes = typeof(NetPeer).GetField("m_storagePoolBytes", Instance)!;
    private static readonly FieldInfo StorageSlots = typeof(NetPeer).GetField("m_storageSlotsUsedCount", Instance)!;
    private static readonly Type ReliableSender = typeof(NetPeer).Assembly.GetType("Lidgren.Network.NetReliableSenderChannel", true)!;
    private static readonly FieldInfo ReliableWindow = ReliableSender.GetField("m_windowStart", Instance)!;
    private static readonly FieldInfo ReliableSend = ReliableSender.GetField("m_sendStart", Instance)!;
    private static ConditionalWeakTable<NetPeer, PeerState> _states = new ConditionalWeakTable<NetPeer, PeerState>();
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static Harmony? _harmony;

    internal static MethodInfo FindFragmentTarget() => typeof(NetPeer).GetMethod("HandleReleasedFragment", Instance, null, new[] { typeof(NetIncomingMessage) }, null)!;
    internal static MethodInfo FindHeartbeatTarget() => typeof(NetPeer).GetMethod("Heartbeat", Instance, null, Type.EmptyTypes, null)!;
    internal static MethodInfo FindDisconnectTarget() => typeof(NetConnection).GetMethod("SetStatus", Instance, null, new[] { typeof(NetConnectionStatus), typeof(string) }, null)!;
    internal static MethodInfo FindShutdownTarget() => typeof(NetPeer).GetMethod("ExecutePeerShutdown", Instance, null, Type.EmptyTypes, null)!;
    internal static MethodInfo FindStorageTarget() => typeof(NetPeer).GetMethod("Recycle", Instance, null, new[] { typeof(byte[]) }, null)!;
    internal static MethodInfo FindAcknowledgeTarget() => ReliableSender.GetMethod("ReceiveAcknowledge", Instance, null, new[] { typeof(double), typeof(int) }, null)!;

    internal static void Install()
    {
        if (_harmony != null) { return; }
        if (!HasSupportedContract()) { throw new InvalidOperationException("Unsupported Lidgren fragment contract."); }
        var harmony = new Harmony(PatchId);
        try
        {
            Patch(harmony, FindFragmentTarget(), nameof(BeforeFragment), finalizer: nameof(AfterFragment));
            Patch(harmony, FindHeartbeatTarget(), nameof(BeforeHeartbeat));
            Patch(harmony, FindDisconnectTarget(), postfix: nameof(AfterConnectionStatus));
            Patch(harmony, FindShutdownTarget(), nameof(BeforeShutdown));
            Patch(harmony, FindStorageTarget(), nameof(BeforeStorage), finalizer: nameof(AfterStorage));
            Patch(harmony, FindAcknowledgeTarget(), nameof(BeforeAcknowledge));
        }
        catch
        {
            foreach (MethodInfo target in Targets) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
            Targets.Clear();
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
        {
            foreach (MethodInfo target in Targets) { _harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        }
        Targets.Clear(); _harmony = null;
        // In-flight finalizers retain their monitor state. Native incomplete
        // groups belong to NetPeer and remain valid after removing this guard.
        _states = new ConditionalWeakTable<NetPeer, PeerState>();
    }

    private static void Patch(Harmony harmony, MethodInfo target, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        Targets.Add(target);
        HarmonyMethod? Method(string? name) => name == null ? null : new HarmonyMethod(typeof(EngineFragmentGuard), name);
        harmony.Patch(target, prefix: Method(prefix), postfix: Method(postfix), finalizer: Method(finalizer));
    }

    internal static bool HasSupportedContract() => NativeGroups?.FieldType.IsGenericType == true && GroupData?.FieldType == typeof(byte[]) &&
        GroupChunks?.FieldType == typeof(NetBitVector) && ConnectionPeer?.FieldType == typeof(NetPeer) &&
        ConnectionStatus?.FieldType == typeof(NetConnectionStatus) && StorageBytes?.FieldType == typeof(int) && StorageSlots?.FieldType == typeof(int) && StoragePool != null &&
        Body(FindFragmentTarget(), "1fa30afb8697db28604c980dc9ca4ee5e6d82a43b6e20eed19f276095731dc12") &&
        Body(FindHeartbeatTarget(), "74424738d782e75d0173c83cb01257064e2582fd10fa6c40c07bc5c16dcef025") &&
        Body(FindDisconnectTarget(), "5716fccd643d85a6f1ad665b1e9fc73ee08375a1a36bafc8647d44fce07dc941") &&
        Body(FindShutdownTarget(), "d43790284ae31b92cde4dcfc495e6f7a05ee2330f902285e852a075cd9d1d391") &&
        Body(FindStorageTarget(), "8113dbb1e9c1cc4291ad1d58b2e47edfd0183a9edc01082f9259d5a2958dac58") &&
        ReliableWindow?.FieldType == typeof(int) && ReliableSend?.FieldType == typeof(int) &&
        Body(FindAcknowledgeTarget(), "d5c680099b8c44642d2d79872668130bda25b36eb842f1de18a225f1e37e14f0");

    private static bool BeforeAcknowledge(object __instance, int seqNr)
    {
        // The original accepts sendStart itself, including an empty window.
        // That sequence has no stored message and DestoreMessage dereferences it
        // before its null check, killing the peer's network thread.
        if (seqNr < 0 || seqNr >= 1024) { return false; }
        int start = (int)ReliableWindow.GetValue(__instance)!;
        int sent = ((int)ReliableSend.GetValue(__instance)! - start + 1024) % 1024;
        return (seqNr - start + 1024) % 1024 < sent;
    }

    private static bool BeforeFragment(NetPeer __instance, NetIncomingMessage __0, out FragmentLease __state)
    {
        PeerState state = State(__instance);
        Monitor.Enter(state);
        __state = new FragmentLease(state);
        long now = Timestamp();
        Sweep(state, now);
        NetConnection? sender = __0.SenderConnection;
        if (sender == null || (NetConnectionStatus)ConnectionStatus.GetValue(sender)! == NetConnectionStatus.Disconnected ||
            !TryHeader(__0, out FragmentHeader header)) { __instance.Recycle(__0); return false; }
        IDictionary? native = ConnectionGroups(__instance, sender);
        object? nativeGroup = native?[header.Group];
        ConnectionState connection = ConnectionStateFor(state, sender);
        if (connection.Groups.TryGetValue(header.Group, out GroupState? existing))
        {
            if (nativeGroup == null) { Forget(state, sender, header.Group, removeNative: false); }
            else
            {
                if (existing.DataBytes != header.TotalBytes || existing.Chunks != header.Chunks ||
                    !ReferenceEquals(existing.Native, nativeGroup)) { __instance.Recycle(__0); return false; }
                if (existing.TotalBits == 0)
                {
                    // Safely adopt a bounded in-flight group when the resource
                    // installs during a transfer; acknowledged chunks survive.
                    existing.TotalBits = header.TotalBits; existing.ChunkBytes = header.ChunkBytes;
                }
                if (existing.TotalBits != header.TotalBits || existing.ChunkBytes != header.ChunkBytes) { __instance.Recycle(__0); return false; }
                var bits = (NetBitVector)GroupChunks.GetValue(nativeGroup)!;
                if (bits[header.Number]) { __instance.Recycle(__0); return false; }
            }
        }
        if (!connection.Groups.ContainsKey(header.Group))
        {
            int charge = Charge(header.TotalBytes, header.Chunks);
            if (nativeGroup != null || connection.Groups.Count >= MaximumConnectionGroups || state.GroupCount >= MaximumPeerGroups ||
                connection.Bytes + charge > MaximumConnectionBytes || state.Bytes + charge > MaximumPeerBytes)
            { __instance.Recycle(__0); return false; }
            var group = new GroupState(header.TotalBytes, header.Chunks, now, null)
            { TotalBits = header.TotalBits, ChunkBytes = header.ChunkBytes };
            connection.Groups.Add(header.Group, group); connection.Bytes += charge; state.Bytes += charge; state.GroupCount++;
        }
        __state.Sender = sender; __state.Group = header.Group; __state.Accepted = true;
        return true;
    }

    private static Exception? AfterFragment(Exception? __exception, FragmentLease? __state)
    {
        if (__state == null) { return __exception; }
        try
        {
            if (__state.Accepted && __state.Sender != null)
            {
                PeerState state = __state.State;
                object? native = ConnectionGroups(state.Peer, __state.Sender)?[__state.Group];
                if (native == null) { Forget(state, __state.Sender, __state.Group, removeNative: false); }
                else { state.Connections[__state.Sender].Groups[__state.Group].Native = native; }
            }
        }
        finally { Monitor.Exit(__state.State); }
        return __exception;
    }

    private static void BeforeHeartbeat(NetPeer __instance)
    {
        PeerState state = State(__instance);
        lock (state) { Sweep(state, Timestamp()); }
    }

    private static void AfterConnectionStatus(NetConnection __instance, NetConnectionStatus __0)
    {
        if (__0 != NetConnectionStatus.Disconnected || ConnectionPeer.GetValue(__instance) is not NetPeer peer || !_states.TryGetValue(peer, out PeerState? state)) { return; }
        lock (state)
        {
            if (state.Connections.TryGetValue(__instance, out ConnectionState? connection))
            {
                foreach (int group in connection.Groups.Keys.ToArray()) { Forget(state, __instance, group, removeNative: true); }
            }
            ((IDictionary)NativeGroups.GetValue(peer)!).Remove(__instance);
        }
    }

    private static void BeforeShutdown(NetPeer __instance)
    {
        if (_states.TryGetValue(__instance, out PeerState? state)) { lock (state) { Clear(state); } }
        else { ((IDictionary)NativeGroups.GetValue(__instance)!).Clear(); }
    }

    private static bool BeforeStorage(NetPeer __instance, byte[]? __0, out object? __state)
    {
        __state = null;
        if (__0 == null || StoragePool.GetValue(__instance) is not List<byte[]?> pool) { return true; }
        Monitor.Enter(pool); __state = pool;
        int bytes = (int)StorageBytes.GetValue(__instance)!, slots = (int)StorageSlots.GetValue(__instance)!;
        // The client installs after downloading its resource. Trim only unused
        // cached buffers if the old engine already exceeded this budget.
        for (int index = pool.Count - 1; bytes > MaximumPeerBytes && index >= 0; index--)
        {
            byte[]? cached = pool[index];
            if (cached == null) { continue; }
            bytes -= cached.Length; slots--; pool[index] = null;
        }
        StorageBytes.SetValue(__instance, bytes); StorageSlots.SetValue(__instance, slots);
        return __0.Length <= MaximumMessageBytes && bytes <= MaximumPeerBytes - __0.Length;
    }
    private static void AfterStorage(object? __state) { if (__state != null) { Monitor.Exit(__state); } }

    private static PeerState State(NetPeer peer) => _states.GetValue(peer, key => new PeerState(key));

    private static ConnectionState ConnectionStateFor(PeerState state, NetConnection sender)
    {
        if (!state.Connections.TryGetValue(sender, out ConnectionState? connection))
        { state.Connections.Add(sender, connection = new ConnectionState()); }
        return connection;
    }

    private static void Sweep(PeerState state, long now)
    {
        if (!state.Initialized)
        {
            IDictionary all = (IDictionary)NativeGroups.GetValue(state.Peer)!;
            foreach (NetConnection sender in all.Keys.Cast<NetConnection>().ToArray())
            {
                IDictionary native = (IDictionary)all[sender]!;
                ConnectionState connection = ConnectionStateFor(state, sender);
                foreach (int id in native.Keys.Cast<int>().ToArray())
                {
                    object group = native[id]!;
                    byte[] data = (byte[])GroupData.GetValue(group)!;
                    var chunks = (NetBitVector)GroupChunks.GetValue(group)!;
                    int charge = data.Length <= MaximumMessageBytes && chunks.Capacity > 0 && chunks.Capacity <= MaximumMessageBytes ? Charge(data.Length, chunks.Capacity) : int.MaxValue;
                    if (data.Length == 0 || connection.Groups.Count >= MaximumConnectionGroups || state.GroupCount >= MaximumPeerGroups ||
                        charge > MaximumConnectionBytes - connection.Bytes || charge > MaximumPeerBytes - state.Bytes)
                    { native.Remove(id); continue; }
                    connection.Groups.Add(id, new GroupState(data.Length, chunks.Capacity, now, group));
                    connection.Bytes += charge; state.Bytes += charge; state.GroupCount++;
                }
            }
            state.Initialized = true;
        }
        if (now < state.NextSweep) { return; }
        state.NextSweep = now + Stopwatch.Frequency;
        foreach (NetConnection sender in state.Connections.Keys.ToArray())
        {
            ConnectionState connection = state.Connections[sender];
            bool disconnected = (NetConnectionStatus)ConnectionStatus.GetValue(sender)! == NetConnectionStatus.Disconnected;
            foreach (int group in connection.Groups.Keys.ToArray())
            {
                GroupState entry = connection.Groups[group];
                if (disconnected || now - entry.Created >= Stopwatch.Frequency * LifetimeSeconds)
                { Forget(state, sender, group, removeNative: true); }
            }
            if (connection.Groups.Count == 0)
            {
                state.Connections.Remove(sender);
                if (ConnectionGroups(state.Peer, sender)?.Count == 0) { ((IDictionary)NativeGroups.GetValue(state.Peer)!).Remove(sender); }
            }
        }
    }

    private static void Forget(PeerState state, NetConnection sender, int id, bool removeNative)
    {
        if (state.Connections.TryGetValue(sender, out ConnectionState? connection) && connection.Groups.TryGetValue(id, out GroupState? group))
        {
            int charge = Charge(group.DataBytes, group.Chunks);
            state.Bytes -= charge; state.GroupCount--; connection.Bytes -= charge; connection.Groups.Remove(id);
        }
        IDictionary? native = ConnectionGroups(state.Peer, sender);
        if (removeNative) { native?.Remove(id); }
        if (native?.Count == 0) { ((IDictionary)NativeGroups.GetValue(state.Peer)!).Remove(sender); }
    }

    private static void Clear(PeerState state)
    {
        ((IDictionary)NativeGroups.GetValue(state.Peer)!).Clear();
        state.Connections.Clear(); state.Bytes = 0; state.GroupCount = 0;
    }

    private static IDictionary? ConnectionGroups(NetPeer peer, NetConnection sender) => ((IDictionary)NativeGroups.GetValue(peer)!)[sender] as IDictionary;
    private static int Charge(int bytes, int chunks) => bytes + ((chunks + 31) / 32) * sizeof(int);
    private static long Timestamp() => Stopwatch.GetTimestamp();

    internal static bool TryHeader(NetIncomingMessage message, out FragmentHeader header)
    {
        header = default;
        byte[]? data = message.Data;
        int length = message.LengthBytes, offset = 0;
        if (data == null || length > data.Length || message.LengthBits <= 0 ||
            !Varint(data, length, ref offset, out int group) || !Varint(data, length, ref offset, out int bits) ||
            !Varint(data, length, ref offset, out int chunk) || !Varint(data, length, ref offset, out int number) ||
            group < 1 || group >= 65534 || bits < 1 || bits > MaximumMessageBytes * 8 || chunk < 1 || chunk > MaximumMessageBytes) { return false; }
        int bytes = (bits + 7) / 8, chunks = (bytes + chunk - 1) / chunk;
        if (number >= chunks) { return false; }
        int payload = Math.Min(chunk, bytes - number * chunk);
        if (length - offset != payload || message.LengthBits - offset * 8 <= (payload - 1) * 8) { return false; }
        header = new FragmentHeader { Group = group, TotalBits = bits, TotalBytes = bytes, ChunkBytes = chunk, Number = number, Chunks = chunks };
        return true;
    }

    private static bool Varint(byte[] data, int length, ref int offset, out int value)
    {
        value = 0;
        for (int shift = 0; shift <= 28 && offset < length; shift += 7)
        {
            byte current = data[offset++];
            if (shift == 28 && current > 7) { return false; }
            value |= (current & 127) << shift;
            if ((current & 128) == 0) { return shift == 0 || current != 0; }
        }
        return false;
    }

    private static bool Body(MethodInfo method, string expected)
    {
        using (SHA256 hash = SHA256.Create())
        { return BitConverter.ToString(hash.ComputeHash(method.GetMethodBody()!.GetILAsByteArray()!)).Replace("-", string.Empty).ToLowerInvariant() == expected; }
    }
    internal struct FragmentHeader { internal int Group, TotalBits, TotalBytes, ChunkBytes, Number, Chunks; }
    private sealed class FragmentLease
    {
        internal readonly PeerState State;
        internal NetConnection? Sender;
        internal int Group;
        internal bool Accepted;
        internal FragmentLease(PeerState state) { State = state; }
    }
    private sealed class PeerState
    {
        internal readonly NetPeer Peer;
        internal readonly Dictionary<NetConnection, ConnectionState> Connections = new Dictionary<NetConnection, ConnectionState>();
        internal int Bytes, GroupCount;
        internal long NextSweep;
        internal bool Initialized;
        internal PeerState(NetPeer peer) { Peer = peer; }
    }
    private sealed class ConnectionState
    {
        internal readonly Dictionary<int, GroupState> Groups = new Dictionary<int, GroupState>();
        internal int Bytes;
    }
    private sealed class GroupState
    {
        internal readonly int DataBytes, Chunks;
        internal readonly long Created;
        internal int TotalBits, ChunkBytes;
        internal object? Native;
        internal GroupState(int bytes, int chunks, long created, object? native)
        { DataBytes = bytes; Chunks = chunks; Created = created; Native = native; }
    }
}
