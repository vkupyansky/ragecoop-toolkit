using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Reflection;
using CoopControl.Shared;
using HarmonyLib;
using Lidgren.Network;

internal static partial class Program
{
    private const BindingFlags FragmentInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags FragmentStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static long _fragmentNow;

    private static void CheckFragments()
    {
        var clock = new Harmony("friendslegacy.check.fragmentclock");
        var peer = FragmentPeer();
        NetConnection sender = FragmentConnection(peer, 42001);
        MethodInfo release = EngineFragmentGuard.FindFragmentTarget();
        IDictionary groups = FragmentGroups(peer);
        void Receive(NetIncomingMessage fragment) => release.Invoke(peer, new object[] { fragment });
        NetIncomingMessage Fragment(int group, int bits, int chunk, int number, byte[] payload) => FragmentMessage(peer, sender, group, bits, chunk, number, payload);
        try
        {
            Assert(EngineFragmentGuard.HasSupportedContract(), "Fragment guard matches the exact installed Lidgren IL and fields.");
            clock.Patch(typeof(EngineFragmentGuard).GetMethod("Timestamp", FragmentStatic)!, prefix: new HarmonyMethod(typeof(Program), nameof(FragmentClock)));
            _fragmentNow = 100;

            // Start with the real unpatched assembler, then install during an
            // incomplete reliable transfer. Previously acknowledged data remains.
            byte[] prior = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
            Receive(Fragment(1, prior.Length * 8, 1024, 0, prior.Take(1024).ToArray()));
            Assert(((IDictionary)groups[sender]!).Count == 1, "The actual original assembler contains an in-flight group before guard installation.");
            for (int repeat = 0; repeat < 18; repeat++) EngineFragmentGuard.FindStorageTarget().Invoke(peer, new object[] { new byte[EngineFragmentGuard.MaximumMessageBytes] });
            EngineFragmentGuard.Install(); EngineFragmentGuard.Install();
            EngineFragmentGuard.FindStorageTarget().Invoke(peer, new object[] { new byte[128] });
            Assert((int)typeof(NetPeer).GetField("m_storagePoolBytes", FragmentInstance)!.GetValue(peer)! <= EngineFragmentGuard.MaximumPeerBytes,
                "Installing after resource downloads also bounds pre-existing unused cached storage.");
            for (int number = 1; number < 4; number++) Receive(Fragment(1, prior.Length * 8, 1024, number, prior.Skip(number * 1024).Take(1024).ToArray()));
            NetIncomingMessage completed = peer.ReadMessage();
            Assert(completed != null && completed.LengthBytes == prior.Length && completed.Data.Take(prior.Length).SequenceEqual(prior) && groups.Count == 0,
                "Installing the guard preserves pre-existing acknowledged fragments and the native assembler releases the correct complete bytes once.");
            peer.Recycle(completed);

            byte[] fileChunk = Enumerable.Range(0, 50321).Select(value => (byte)(value * 17)).ToArray();
            int chunks = (fileChunk.Length + 1023) / 1024;
            for (int number = chunks - 1; number >= 0; number--)
            {
                byte[] payload = fileChunk.Skip(number * 1024).Take(1024).ToArray();
                Receive(Fragment(2, fileChunk.Length * 8, 1024, number, payload));
                if (number > 0) Receive(Fragment(2, fileChunk.Length * 8, 1024, number, new byte[payload.Length]));
            }
            completed = peer.ReadMessage();
            Assert(completed != null && completed.Data.Take(fileChunk.Length).SequenceEqual(fileChunk) && peer.ReadMessage() == null && groups.Count == 0,
                "The native assembler accepts an ordinary 50k resource chunk out of order, ignores conflicting duplicates and releases exactly one intact result.");
            peer.Recycle(completed);

            byte[] first = Enumerable.Repeat((byte)11, 8).ToArray(), last = Enumerable.Repeat((byte)22, 8).ToArray();
            Receive(Fragment(3, 128, 8, 0, first));
            Receive(Fragment(3, 128, 4, 1, new byte[4]));
            Receive(Fragment(3, 128, 8, 1, last));
            completed = peer.ReadMessage();
            Assert(completed != null && completed.Data.Take(16).SequenceEqual(first.Concat(last)),
                "A changed chunk-size header cannot corrupt an existing native group or prevent its legitimate completion.");
            peer.Recycle(completed);

            foreach (NetIncomingMessage invalid in new[]
            {
                Fragment(4, int.MaxValue, 1, 0, new byte[1]),
                Fragment(4, -1, 1, 0, new byte[1]),
                Fragment(4, 16, 0, 0, new byte[1]),
                Fragment(4, 16, 1, 2, new byte[1]),
                Fragment(4, 16, 1, -1, new byte[1]),
                Fragment(4, (EngineFragmentGuard.MaximumMessageBytes + 1) * 8, 1, 0, new byte[1]),
                Fragment(4, 128, 8, 0, new byte[9]),
                Fragment(4, 128, 8, 0, new byte[7]),
                FragmentRaw(peer, sender, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0 }),
                FragmentRaw(peer, sender, new byte[] { 1, 0x80 }),
            })
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                Receive(invalid);
                Assert(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536 && groups.Count == 0 && peer.ReadMessage() == null,
                    "Malformed fragment length, varint, offset or payload is rejected before allocation or native copy.");
            }

            Receive(Fragment(5, 8000, 500, 0, new byte[500]));
            _fragmentNow += System.Diagnostics.Stopwatch.Frequency * (EngineFragmentGuard.LifetimeSeconds - 1);
            Receive(Fragment(5, 8000, 500, 0, new byte[500]));
            _fragmentNow += System.Diagnostics.Stopwatch.Frequency * 2;
            EngineFragmentGuard.FindHeartbeatTarget().Invoke(peer, Array.Empty<object>());
            Assert(groups.Count == 0, "An incomplete group expires by monotonic absolute age; duplicate traffic cannot extend its lifetime.");

            for (int id = 100; id <= 100 + EngineFragmentGuard.MaximumConnectionGroups; id++) Receive(Fragment(id, 16, 1, 0, new byte[1]));
            Assert(((IDictionary)groups[sender]!).Count == EngineFragmentGuard.MaximumConnectionGroups,
                "One connected sender cannot retain more than the configured number of native incomplete groups.");
            EngineFragmentGuard.FindDisconnectTarget().Invoke(sender, new object[] { NetConnectionStatus.Disconnected, "test" });
            Assert(groups.Count == 0, "The actual connection status transition immediately frees all of that sender's incomplete groups.");
            EngineFragmentGuard.FindDisconnectTarget().Invoke(sender, new object[] { NetConnectionStatus.Connected, "test" });

            for (int id = 200; id < 208; id++) Receive(Fragment(id, EngineFragmentGuard.MaximumMessageBytes * 8, 1024, 0, new byte[1024]));
            Assert(FragmentBytes(peer, sender) <= EngineFragmentGuard.MaximumConnectionBytes && ((IDictionary)groups[sender]!).Count == 3,
                "Native payload and bitmap allocations are both charged against the per-connection byte budget.");
            EngineFragmentGuard.FindDisconnectTarget().Invoke(sender, new object[] { NetConnectionStatus.Disconnected, "test" });
            for (int index = 0; index < 8; index++)
            {
                NetConnection other = FragmentConnection(peer, 42010 + index);
                for (int id = 300; id < 303; id++)
                    Receive(FragmentMessage(peer, other, id, EngineFragmentGuard.MaximumMessageBytes * 8, 1024, 0, new byte[1024]));
            }
            Assert(FragmentBytes(peer) <= EngineFragmentGuard.MaximumPeerBytes && groups.Count < 8,
                "Multiple connections share one bounded peer budget instead of multiplying the allowed allocation.");
            EngineFragmentGuard.FindShutdownTarget().Invoke(peer, Array.Empty<object>());
            Assert(groups.Count == 0, "Actual peer shutdown releases every incomplete fragment group.");

            for (int repeat = 0; repeat < 40; repeat++) EngineFragmentGuard.FindStorageTarget().Invoke(peer, new object[] { new byte[EngineFragmentGuard.MaximumMessageBytes] });
            int cached = (int)typeof(NetPeer).GetField("m_storagePoolBytes", FragmentInstance)!.GetValue(peer)!;
            Assert(cached <= EngineFragmentGuard.MaximumPeerBytes,
                "Completed large payloads cannot grow the native recycled byte-array cache beyond its byte budget.");

            EngineFragmentGuard.Uninstall(); EngineFragmentGuard.Uninstall();
            Assert(new[] { release, EngineFragmentGuard.FindHeartbeatTarget(), EngineFragmentGuard.FindDisconnectTarget(), EngineFragmentGuard.FindShutdownTarget(), EngineFragmentGuard.FindStorageTarget(), EngineFragmentGuard.FindAcknowledgeTarget() }
                .All(method => Harmony.GetPatchInfo(method)?.Owners.Contains("friendslegacy.ragecoop.transportfragments") != true),
                "Transport lifecycle removes all six owned patches and repeated uninstall is harmless.");
        }
        finally { EngineFragmentGuard.Uninstall(); clock.UnpatchAll(clock.Id); }
    }

    private static bool FragmentClock(ref long __result) { __result = _fragmentNow; return false; }
    private static NetPeer FragmentPeer()
    {
        var config = new NetPeerConfiguration("isolated-fragment-check");
        config.DisableMessageType(NetIncomingMessageType.StatusChanged);
        var peer = new NetPeer(config);
        typeof(NetPeer).GetMethod("InitializePools", FragmentInstance)!.Invoke(peer, Array.Empty<object>());
        return peer;
    }
    private static NetConnection FragmentConnection(NetPeer peer, int port)
    {
        var connection = (NetConnection)Activator.CreateInstance(typeof(NetConnection), FragmentInstance, null, new object[] { peer, new IPEndPoint(IPAddress.Loopback, port) }, null)!;
        typeof(NetConnection).GetField("m_status", FragmentInstance)!.SetValue(connection, NetConnectionStatus.Connected);
        return connection;
    }
    private static IDictionary FragmentGroups(NetPeer peer) => (IDictionary)typeof(NetPeer).GetField("m_receivedFragmentGroups", FragmentInstance)!.GetValue(peer)!;
    private static int FragmentBytes(NetPeer peer, NetConnection? only = null)
    {
        int bytes = 0;
        foreach (DictionaryEntry pair in FragmentGroups(peer))
        {
            if (only != null && !ReferenceEquals(pair.Key, only)) { continue; }
            foreach (object group in ((IDictionary)pair.Value!).Values)
            {
                byte[] data = (byte[])group.GetType().GetField("Data")!.GetValue(group)!;
                var chunks = (NetBitVector)group.GetType().GetField("ReceivedChunks")!.GetValue(group)!;
                bytes += data.Length + ((chunks.Capacity + 31) / 32) * sizeof(int);
            }
        }
        return bytes;
    }
    private static NetIncomingMessage FragmentMessage(NetPeer peer, NetConnection sender, int group, int bits, int chunk, int number, byte[] payload)
    {
        byte[] storage = new byte[20 + payload.Length];
        Type helper = typeof(NetPeer).Assembly.GetType("Lidgren.Network.NetFragmentationHelper", true)!;
        int header = (int)helper.GetMethod("WriteHeader", FragmentStatic)!.Invoke(null, new object[] { storage, 0, group, bits, chunk, number })!;
        Array.Copy(payload, 0, storage, header, payload.Length);
        Array.Resize(ref storage, header + payload.Length);
        return FragmentRaw(peer, sender, storage);
    }
    private static NetIncomingMessage FragmentRaw(NetPeer peer, NetConnection sender, byte[] storage)
    {
        var message = (NetIncomingMessage)typeof(NetPeer).GetMethod("CreateIncomingMessage", FragmentInstance, null, new[] { typeof(NetIncomingMessageType), typeof(byte[]) }, null)!
            .Invoke(peer, new object[] { NetIncomingMessageType.Data, storage })!;
        message.LengthBits = storage.Length * 8;
        typeof(NetIncomingMessage).GetField("m_senderConnection", FragmentInstance)!.SetValue(message, sender);
        typeof(NetIncomingMessage).GetField("m_isFragment", FragmentInstance)!.SetValue(message, true);
        return message;
    }
}
