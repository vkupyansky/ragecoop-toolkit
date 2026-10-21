using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoopAdmin.Server;
using CoopControl.Shared;
using GTA.Math;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server;
using RageCoop.Server.Scripting;

internal static partial class Program
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type PacketContainer = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets", true)!;
    private static SnapshotBarrier? snapshotBarrier;

    private static int Main()
    {
        try
        {
            IReadOnlyList<MethodBase> targets = EngineServerGuard.FindTargets();
            Assert(targets.Count >= 25, "all affected methods resolve against the installed host before mutation");
            EngineFragmentGuard.Install();
            EngineServerGuard.Install();
            EngineServerGuard.Install();
            using (var fixture = new Fixture())
            {
                CheckResources(fixture);
                CheckResponses(fixture);
                CheckServerEnvelopes(fixture);
                CheckFileReadFailure(fixture);
                CheckClientSnapshots(fixture);
                CheckEntities(fixture);
                CheckFailureAndStop(fixture);
            }
            CheckResourceNames();
            CheckEntityQuota();
            CheckErrorConnectionAfterRecycle();
            CheckStopDuringResources();
            CheckServerStartStop();
            CheckIdentityReservations();
            CheckHandshakeReservations();
            CheckStaleDisconnect();
            CheckSecurityLifecycle();
            EngineServerGuard.Uninstall();
            EngineServerGuard.Uninstall();
            Assert(targets.All(target => Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.engineserver") != true),
                "unload removes every server guard patch");
            Console.WriteLine("Installed server lifecycle, entity and resource transfer checks passed with loopback clients.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { EngineServerGuard.Uninstall(); EngineFragmentGuard.Uninstall(); }
    }

    private static void CheckResources(Fixture fixture)
    {
        byte[] bytes = Enumerable.Range(0, 64 * 1024 + 37).Select(index => (byte)(index * 37)).ToArray();
        using var stream = new SlowStream(bytes);
        object resources = Field(fixture.Server, "Resources");
        ((IDictionary)Field(resources, "ClientResources")).Add("concurrency", stream);
        int ready = 0;
        bool correctThread = true;
        Thread worker = (Thread)Field(Field(fixture.Server, "_worker"), "_workerThread");
        fixture.Server.API.Events.OnPlayerReady += (_, _) =>
        {
            correctThread &= ReferenceEquals(Thread.CurrentThread, worker);
            fixture.Server.API.GetAllClients();
            Interlocked.Increment(ref ready);
        };
        Call(resources, "SendTo", fixture.First);
        Call(resources, "SendTo", fixture.Second);
        WaitUntil(() => { fixture.ThrowPumpFailure(); return Volatile.Read(ref ready) == 2; }, "parallel resource loading did not finish");
        Assert(fixture.Downloads.Count == 2 && fixture.Downloads.Values.All(value => value.SequenceEqual(bytes)),
            "two actual Resources.SendTo tasks receive complete identical bytes from one shared stream");
        Assert(correctThread && fixture.First.IsReady && fixture.Second.IsReady,
            "actual resource completion publishes readiness and invokes callbacks on the server worker");
        Assert(stream.ConcurrentReads == 0, "shared resource seeks and reads are serialized for each complete transfer");
        Assert(fixture.FileOffers.Values.All(offer => offer.Name == "concurrency.res" && offer.Length == bytes.Length),
            "unrelated resources retain their ordinary file names and actual lengths");
        ((IDictionary)Field(resources, "ClientResources")).Clear();
        using var separate = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
        Call(fixture.Server, "SendFile", separate, "CoopControl.res", fixture.First, 0, null);
        Assert(fixture.FileOffers.Values.Count(offer => offer.Name == "CoopControl.res") == 1,
            "an ordinary SendFile with the same name is not treated as the owned client resource");
        fixture.ThrowPumpFailure();
    }

    private static void CheckResourceNames()
    {
        byte[] first = Enumerable.Range(0, 32 * 1024 + 17).Select(index => (byte)(index * 19)).ToArray();
        byte[] second = (byte[])first.Clone();
        second[1234] ^= 0x80;
        string previousName = SendResourceVersion(first);
        string nextName = SendResourceVersion(second);
        Assert(previousName != nextName && first.Length == second.Length,
            "changed CoopControl bytes produce a new cache name even when both resource versions have the same length");
    }

    private static string SendResourceVersion(byte[] bytes)
    {
        using var fixture = new Fixture();
        using var stream = new SlowStream(bytes);
        stream.Position = 11;
        object resources = Field(fixture.Server, "Resources");
        ((IDictionary)Field(resources, "ClientResources")).Add("CoopControl", stream);
        Call(resources, "SendTo", fixture.First);
        Call(resources, "SendTo", fixture.Second);
        WaitUntil(() => { fixture.ThrowPumpFailure(); return fixture.First.IsReady && fixture.Second.IsReady; },
            "content-addressed resource loading did not finish");
        string expected = "CoopControl." + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".res";
        Assert(fixture.FileOffers.Count == 2 && fixture.FileOffers.Values.All(offer => offer.Name == expected && offer.Length == bytes.Length) &&
            fixture.Downloads.Count == 2 && fixture.Downloads.Values.All(value => value.SequenceEqual(bytes)),
            "actual Resources.SendTo offers both clients the SHA256 name and transfers the matching original bytes and length");
        Assert(stream.BytesRead == bytes.Length * 3L && stream.ConcurrentReads == 0,
            "one immutable CoopControl stream is hashed once and both complete transfers share its monitor");
        ((IDictionary)Field(resources, "ClientResources")).Clear();
        return expected;
    }

    private static void CheckResponses(Fixture fixture)
    {
        fixture.AutomaticResponses = false;
        MethodInfo request = RequestMethod();
        object allSent = Packet("AllResourcesSent");
        Task<object?>[] timeouts = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            Invoke(request, null, fixture.Server, index % 2 == 0 ? fixture.First : fixture.Second, allSent, Channel("File"), 70))).ToArray();
        Task.WaitAll(timeouts);
        Assert(timeouts.All(task => task.Result == null) && EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "parallel production response waits time out without retained callbacks or waiters");
        fixture.Requests.Clear();
        Task<object?> waiting = Task.Run(() => Invoke(request, null, fixture.Server, fixture.First, allSent, Channel("File"), 3000));
        WaitUntil(() => fixture.Requests.Count != 0, "request envelope missing");
        RequestEnvelope envelope = fixture.Requests.Last();
        fixture.Respond(fixture.SecondPeer, envelope.Id, 13, 0, "Loaded");
        Thread.Sleep(50);
        Assert(!waiting.IsCompleted, "a response from another connected player cannot complete a pending request");
        fixture.Respond(fixture.FirstPeer, envelope.Id, 14, 0, "Loaded");
        Thread.Sleep(50);
        Assert(!waiting.IsCompleted, "the expected player with the wrong packet type cannot complete a pending request");
        fixture.Respond(fixture.FirstPeer, envelope.Id, 13, 999, "Loaded");
        Thread.Sleep(50);
        Assert(!waiting.IsCompleted, "a file acknowledgement for another file cannot complete a pending request");
        fixture.Respond(fixture.FirstPeer, envelope.Id, 13, 0, "Loaded");
        Assert(waiting.Wait(3000) && waiting.Result != null && EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "the matching real response completes once and releases the request slot");
        fixture.Respond(fixture.FirstPeer, envelope.Id, 13, 0, "Loaded");
        object fileRequest = Packet("FileTransferRequest");
        Set(fileRequest, "ID", 153);
        Set(fileRequest, "Name", "phase.res");
        Set(fileRequest, "FileLength", 1L);
        Task<object?> fileTimeout = Task.Run(() => Invoke(request, null, fixture.Server, fixture.First, fileRequest, Channel("File"), 40));
        try { fileTimeout.GetAwaiter().GetResult(); throw new InvalidOperationException("file timeout was hidden"); }
        catch (TimeoutException) { }
        Assert(EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "a missing file-transfer acknowledgement fails the transfer and releases its request slot");
        fixture.Requests.Clear();
        Task<object?> wrongPhase = Task.Run(() => Invoke(request, null, fixture.Server, fixture.First, fileRequest, Channel("File"), 3000));
        WaitUntil(() => fixture.Requests.Any(value => value.Type == 12), "phase request envelope missing");
        RequestEnvelope phaseEnvelope = fixture.Requests.Last(value => value.Type == 12);
        fixture.Respond(fixture.FirstPeer, phaseEnvelope.Id, 13, 153, "Loaded");
        try { wrongPhase.GetAwaiter().GetResult(); throw new InvalidOperationException("wrong file phase was accepted"); }
        catch (IOException ex) when (ex.InnerException is InvalidDataException) { }
        Assert(EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "an acknowledgement from a different transfer phase cannot turn a failed download into readiness");
        fixture.ThrowPumpFailure();
    }

    private static void CheckFileReadFailure(Fixture fixture)
    {
        fixture.AutomaticResponses = true;
        using var stream = new FailingStream();
        MethodInfo send = typeof(Server).GetMethods(All).Single(method => method.Name == "SendFile" && method.GetParameters()[0].ParameterType == typeof(Stream));
        Task sending = Task.Run(() => Invoke(send, fixture.Server, stream, "read-error.res", fixture.First, 0, null));
        try { sending.GetAwaiter().GetResult(); throw new InvalidOperationException("read failure was hidden"); }
        catch (IOException ex) when (ex.Message == "isolated read failure") { }
        bool unlocked = Monitor.TryEnter(stream);
        if (unlocked) { Monitor.Exit(stream); }
        Assert(unlocked && ((IDictionary)Field(fixture.Server, "InProgressFileTransfers")).Count == 0,
            "an actual SendFile read failure releases its stream and in-progress transfer record");
        fixture.AutomaticResponses = false;
        fixture.ThrowPumpFailure();
    }

    private static void CheckClientSnapshots(Fixture fixture)
    {
        MethodInfo process = typeof(Server).GetMethod("ProcessMessage", All)!;
        var inspection = new Harmony("friendslegacy.servercheck.snapshot");
        using var barrier = new SnapshotBarrier(fixture.First);
        snapshotBarrier = barrier;
        inspection.Patch(process, prefix: new HarmonyMethod(typeof(Program), nameof(PauseClientMutation)) { priority = Priority.Last });
        try
        {
            Task processing = Task.Run(() => Invoke(process, fixture.Server, new object?[] { null }));
            Assert(barrier.Entered.Wait(2000), "actual ProcessMessage holds the client mutation gate");
            using var started = new ManualResetEventSlim();
            Task<Dictionary<int, Client>> snapshot = Task.Run(() => { started.Set(); return fixture.Server.API.GetAllClients(); });
            Assert(started.Wait(2000) && !snapshot.Wait(40), "API.GetAllClients waits while the actual message path changes client indices");
            barrier.Release.Set();
            Task.WaitAll(processing, snapshot);
            Assert(snapshot.Result.TryGetValue(9901, out Client? added) && ReferenceEquals(added, fixture.First),
                "the returned client dictionary is a complete consistent snapshot after the mutation");
            ((IDictionary)Field(fixture.Server, "ClientsByID")).Remove(9901);
        }
        finally
        {
            barrier.Release.Set();
            inspection.Unpatch(process, HarmonyPatchType.All, inspection.Id);
            snapshotBarrier = null;
        }
    }

    private static void PauseClientMutation(Server __instance, object[] __args)
    {
        SnapshotBarrier? barrier = snapshotBarrier;
        if (__args[0] != null || barrier == null) { return; }
        barrier.Entered.Set();
        if (!barrier.Release.Wait(5000)) { throw new TimeoutException("isolated snapshot barrier"); }
        ((IDictionary)Field(__instance, "ClientsByID")).Add(9901, barrier.Client);
    }

    private static void CheckStopDuringResources()
    {
        using var fixture = new Fixture { AutomaticResponses = false };
        int ready = 0;
        fixture.Server.API.Events.OnPlayerReady += (_, _) => Interlocked.Increment(ref ready);
        object resources = Field(fixture.Server, "Resources");
        using var stream = new MemoryStream(new byte[4097], writable: false);
        ((IDictionary)Field(resources, "ClientResources")).Add("shutdown", stream);
        Call(resources, "SendTo", fixture.First);
        WaitUntil(() => EngineServerGuard.PendingResponseCount(fixture.Server) != 0, "resource request did not start before shutdown");
        var watch = Stopwatch.StartNew();
        fixture.Server.Stop();
        Assert(watch.ElapsedMilliseconds < 3000 && ready == 0 && !fixture.First.IsReady && EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "Stop cancels an active real Resources.SendTo task before readiness or resource teardown");
        ((IDictionary)Field(resources, "ClientResources")).Clear();
    }

    private static void CheckServerStartStop()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ragecoop-server-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string previous = Environment.CurrentDirectory;
        Logger logger = Construct<Logger>(true, false);
        logger.UseConsole = true;
        var server = new Server(new Settings { Port = 0, AnnounceSelf = false, AutoUpdate = false, UseP2P = false, LogLevel = 4 }, logger);
        try
        {
            Environment.CurrentDirectory = directory;
            server.Start();
            var listener = (Thread)Field(server, "_listenerThread");
            var peer = (NetServer)Field(server, "MainNetServer");
            Assert(listener.IsAlive && peer.Status == NetPeerStatus.Running && peer.Port != 0,
                "the real installed Server.Start runs its listener and resource loader on an isolated ephemeral port");
            server.Stop();
            WaitUntil(() => peer.Status == NetPeerStatus.NotRunning, "isolated server socket did not close");
            Assert(!listener.IsAlive && !((Thread)Field(Field(server, "_worker"), "_workerThread")).IsAlive,
                "real listener-driven resource unload and Stop join both threads and close the socket");
        }
        finally
        {
            server.Stop();
            Environment.CurrentDirectory = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckErrorConnectionAfterRecycle()
    {
        using var fixture = new Fixture();
        MethodInfo create = typeof(NetPeer).GetMethod("CreateIncomingMessage", All, null,
            new[] { typeof(NetIncomingMessageType), typeof(int) }, null)!;
        var incoming = (NetIncomingMessage)Invoke(create, fixture.Transport, NetIncomingMessageType.Data, 1)!;
        incoming.Write((byte)7); // Response header truncated before the request ID.
        typeof(NetIncomingMessage).GetField("m_senderConnection", All)!.SetValue(incoming, Property(fixture.First, "Connection"));
        InvalidDataException failure;
        try { Call(fixture.Server, "ProcessMessage", incoming); throw new InvalidOperationException("truncated response was accepted"); }
        catch (InvalidDataException ex) { failure = ex; }
        Assert(incoming.Data == null, "exceptional ProcessMessage returns its incoming message to the transport pool");
        // Model the stock Listen catch reading a message already reused for another
        // connection. Its original packet exception must retain the actual sender.
        Call(fixture.Server, "DisconnectAndLog", Property(fixture.Second, "Connection"),
            Enum.ToObject(typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!, 255), failure);
        WaitUntil(() => fixture.FirstPeer.ConnectionStatus == NetConnectionStatus.Disconnected,
            "malformed packet sender was not disconnected");
        Assert(fixture.SecondPeer.ConnectionStatus == NetConnectionStatus.Connected,
            "the listener error path disconnects the captured sender after recycle and preserves another connection");
        WaitUntil(() => fixture.DisconnectReasons.ContainsKey(fixture.FirstPeer), "the malformed-data disconnect reason was not delivered");
        Assert(fixture.DisconnectReasons[fixture.FirstPeer] == "Не удалось прочитать данные игры. Переподключитесь к серверу.",
            "malformed data retains its technical log cause and sends the affected player a specific Russian reconnect instruction");
        fixture.ThrowPumpFailure();
    }

    private static void CheckEntities(Fixture fixture)
    {
        object entities = Field(fixture.Server, "Entities");
        ConcurrentDictionary<int, ServerPed> peds = (ConcurrentDictionary<int, ServerPed>)Property(entities, "Peds");
        ConcurrentDictionary<int, ServerVehicle> vehicles = (ConcurrentDictionary<int, ServerVehicle>)Property(entities, "Vehicles");
        MethodInfo pedUpdate = entities.GetType().GetMethods(All).Single(method => method.Name == "Update" &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType.Name == "PedSync");
        MethodInfo vehicleUpdate = entities.GetType().GetMethods(All).Single(method => method.Name == "Update" &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType.Name == "VehicleSync");
        object npc = Packet("PedSync");
        Set(npc, "ID", 731);
        Set(npc, "Position", new Vector3(1, 2, 3));
        Invoke(pedUpdate, entities, npc, fixture.First);
        Invoke(pedUpdate, entities, npc, fixture.First);
        Assert(fixture.First.EntitiesCount == 2 && fixture.Second.EntitiesCount == 1,
            "repeated real ped updates count a newly registered NPC once alongside each player");
        Invoke(pedUpdate, entities, npc, fixture.Second);
        Assert(fixture.First.EntitiesCount == 1 && fixture.Second.EntitiesCount == 2,
            "legitimate ped ownership takeover transfers the count without blocking the update");
        object baseScript = Field(fixture.Server, "BaseScript");
        ((ServerScript)baseScript).API = fixture.Server.API;
        MethodInfo pedDelete = DeleteHandler("RemovePed");
        Invoke(pedDelete, baseScript, new CustomEventReceivedArgs { Client = fixture.First, Args = new object[] { 731 } });
        Assert(peds.ContainsKey(731), "a former owner cannot delete another player's current NPC");
        Invoke(pedDelete, baseScript, new CustomEventReceivedArgs { Client = fixture.Second, Args = new object[] { 731 } });
        Assert(!peds.ContainsKey(731) && fixture.Second.EntitiesCount == 1,
            "the current owner can delete its NPC and the counter remains exact");
        Invoke(pedDelete, baseScript, new CustomEventReceivedArgs { Client = fixture.Second, Args = new object[] { fixture.First.Player.ID } });
        Assert(peds.ContainsKey(fixture.First.Player.ID), "another player cannot delete the real player character");
        Invoke(pedDelete, baseScript, new CustomEventReceivedArgs { Client = fixture.First, Args = new object[] { fixture.First.Player.ID } });
        Assert(peds.ContainsKey(fixture.First.Player.ID), "even its owner cannot delete a connected player's reserved character through an NPC deletion event");

        ServerVehicle vehicle = Construct<ServerVehicle>(fixture.Server);
        Set(vehicle, "ID", 812);
        Set(vehicle, "Owner", fixture.First);
        vehicles.TryAdd(vehicle.ID, vehicle);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Call(fixture.Server, "QueueJob", new Action(() => { entered.Set(); release.Wait(); }));
        Assert(entered.Wait(2000), "server worker barrier entered");
        object takeover = Packet("VehicleSync");
        Set(takeover, "ID", vehicle.ID);
        Set(takeover, "Position", new Vector3(4, 5, 6));
        object late = Packet("PedSync");
        Set(late, "ID", 999);
        Call(fixture.Server, "QueueJob", new Action(() => Invoke(vehicleUpdate, entities, takeover, fixture.Second)));
        Call(fixture.Server, "QueueJob", new Action(() => Invoke(pedUpdate, entities, late, fixture.First)));
        Call(fixture.Server, "PlayerDisconnected", fixture.First);
        Call(fixture.Server, "QueueJob", new Action(finished.Set));
        release.Set();
        Assert(finished.Wait(3000), "disconnect cleanup completed on the real worker");
        Assert(ReferenceEquals(vehicles.GetValueOrDefault(vehicle.ID), vehicle) && ReferenceEquals(vehicle.Owner, fixture.Second),
            "queued disconnect cleanup preserves the exact vehicle already taken over by a connected player");
        Assert(!peds.ContainsKey(999) && !peds.ContainsKey(fixture.First.Player.ID) && !fixture.First.IsReady && fixture.First.EntitiesCount == 0,
            "queued snapshots from a departed session cannot recreate entities and readiness is cleared");
        Assert(fixture.Second.EntitiesCount == 2, "surviving player and car determine the remaining count");
    }

    private static void CheckEntityQuota()
    {
        using var fixture = new Fixture();
        var settings = (Settings)Field(fixture.Server, "Settings");
        settings.KickSpamming = true;
        settings.SpamLimit = 1024;
        object entities = Field(fixture.Server, "Entities");
        MethodInfo update = entities.GetType().GetMethods(All).Single(method => method.Name == "Update" &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType.Name == "PedSync");
        object packet = Packet("PedSync");
        for (int index = 0; index < 100; index++)
        {
            Set(packet, "ID", 10000 + index);
            Invoke(update, entities, packet, fixture.First);
            Invoke(update, entities, packet, fixture.First);
        }
        Assert(fixture.First.EntitiesCount == 101,
            "100 ambient NPCs plus the player count once despite repeated real snapshots");
        Set(fixture.First, "EntitiesCount", int.MaxValue);
        Call(fixture.Server, "KickAssholes");
        Assert(fixture.First.EntitiesCount == 101 && fixture.FirstPeer.ConnectionStatus == NetConnectionStatus.Connected,
            "actual quota enforcement recounts the live world and preserves a legitimate 101-object session");
        for (int index = 100; index < 1023; index++)
        {
            Set(packet, "ID", 10000 + index);
            Invoke(update, entities, packet, fixture.First);
        }
        Assert(fixture.First.EntitiesCount == 1024, "the retained technical quota includes the player");
        Call(fixture.Server, "KickAssholes");
        Assert(fixture.FirstPeer.ConnectionStatus == NetConnectionStatus.Connected,
            "the configured quota accepts its exact boundary");
        Set(packet, "ID", 11024);
        Invoke(update, entities, packet, fixture.First);
        Assert(fixture.First.EntitiesCount == 1025, "the excessive world reaches the real 1025-object boundary");
        Call(fixture.Server, "KickAssholes");
        WaitUntil(() => fixture.FirstPeer.ConnectionStatus == NetConnectionStatus.Disconnected,
            "the configured anti-spam quota did not disconnect the excessive owner");
        WaitUntil(() => fixture.DisconnectReasons.ContainsKey(fixture.FirstPeer), "the disconnect reason was not received");
        Assert(fixture.DisconnectReasons[fixture.FirstPeer] == "Превышен допустимый объём объектов. Переподключитесь к серверу.",
            "the affected player receives a specific Russian disconnect reason");
        Call(fixture.Server, "KickAssholes");
        Assert(fixture.SecondPeer.ConnectionStatus == NetConnectionStatus.Connected,
            "a repeated quota tick ignores the departing peer and preserves the other player");
        typeof(ServerPed).GetField("_isInvincible", All)!.SetValue(fixture.Second.Player, true);
        settings.KickGodMode = false;
        Call(fixture.Server, "KickAssholes");
        Assert(fixture.SecondPeer.ConnectionStatus == NetConnectionStatus.Connected,
            "an explicit server opt-out of godmode enforcement is retained");
        settings.KickGodMode = true;
        Call(fixture.Server, "KickAssholes");
        WaitUntil(() => fixture.DisconnectReasons.ContainsKey(fixture.SecondPeer), "the enabled godmode policy did not disconnect its owner");
        Assert(fixture.DisconnectReasons[fixture.SecondPeer] == "На этом сервере бессмертие запрещено.",
            "the independent godmode protection still works when explicitly enabled");
        fixture.ThrowPumpFailure();
    }

    private static void CheckFailureAndStop(Fixture fixture)
    {
        // A malformed unconnected message used to throw again from the listener's error handler.
        Call(fixture.Server, "DisconnectAndLog", null, Enum.ToObject(typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!, 255),
            new InvalidDataException("isolated malformed message"));
        Assert(true, "logging a malformed unconnected message does not dereference a missing connection");
        object request = Packet("AllResourcesSent");
        Task<object?> pending = Task.Run(() => Invoke(RequestMethod(), null, fixture.Server, fixture.Second, request, Channel("File"), 5000));
        WaitUntil(() => EngineServerGuard.PendingResponseCount(fixture.Server) != 0, "shutdown request was not registered");
        fixture.Server.Stop();
        try { pending.GetAwaiter().GetResult(); throw new InvalidOperationException("shutdown request unexpectedly succeeded"); }
        catch (IOException ex) when (ex.InnerException is OperationCanceledException) { }
        Assert(EngineServerGuard.PendingResponseCount(fixture.Server) == 0 && !((Thread)Field(Field(fixture.Server, "_worker"), "_workerThread")).IsAlive,
            "actual Stop cancels pending replies and joins the worker before resource teardown");
        foreach (string name in new[] { "_playerUpdateTimer", "_announceTimer", "_antiAssholesTimer", "_updateTimer" })
            Assert(!((System.Timers.Timer)Field(fixture.Server, name)).Enabled, name + " stops on shutdown");
        fixture.Server.Stop();
    }

    private static MethodInfo DeleteHandler(string name) => (MethodInfo)EngineServerGuard.FindTargets().Single(method =>
        method.DeclaringType?.FullName == "RageCoop.Server.Scripting.BaseScript" &&
        PatchProcessor.GetOriginalInstructions(method).Any(instruction => instruction.operand is MethodInfo called && called.Name == name));
    private static MethodInfo RequestMethod() => typeof(EngineServerGuard).GetMethod(nameof(EngineServerGuard.RequestResponse), All)!;
    private static object Channel(string name) => Convert.ToInt32(Enum.Parse(typeof(Logger).Assembly.GetType("RageCoop.Core.ConnectionChannel", true)!, name));
    private static object Packet(string name) => Activator.CreateInstance(PacketContainer.GetNestedType(name, All)!, nonPublic: true)!;
    private static T Construct<T>(params object?[] args) => (T)Activator.CreateInstance(typeof(T), All, null, args, null)!;
    private static object Field(object value, string name) => value.GetType().GetField(name, All)!.GetValue(value)!;
    private static object Property(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value)!;
    private static void Set(object value, string name, object content) => value.GetType().GetProperty(name, All)!.SetValue(value, content);
    private static object? Call(object owner, string name, params object?[] args) => Invoke(owner.GetType().GetMethods(All).Single(method =>
        method.Name == name && method.GetParameters().Length == args.Length), owner, args);
    private static object? Invoke(MethodBase method, object? owner, params object?[] args)
    {
        try { return method.Invoke(owner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void WaitUntil(Func<bool> predicate, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.ElapsedMilliseconds > 7000) { throw new TimeoutException(failure); } Thread.Sleep(5); }
    }
    private static void Assert(bool value, string label) { if (!value) { throw new InvalidOperationException(label); } Console.WriteLine("PASS " + label); }

    private sealed record RequestEnvelope(NetClient Peer, int Id, byte Type);
    private sealed record FileOffer(string Name, long Length);

    private sealed class SnapshotBarrier(Client client) : IDisposable
    {
        internal Client Client { get; } = client;
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class FailingStream() : MemoryStream(new byte[13], writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("isolated read failure");
    }

    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private int readers;
        internal int ConcurrentReads;
        internal long BytesRead;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref readers) != 1) { Interlocked.Increment(ref ConcurrentReads); }
            try
            {
                Thread.Sleep(2);
                int read = base.Read(buffer, offset, count);
                Interlocked.Add(ref BytesRead, read);
                return read;
            }
            finally { Interlocked.Decrement(ref readers); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Server Server;
        internal readonly NetServer Transport;
        internal readonly NetClient FirstPeer, SecondPeer;
        internal readonly Client First, Second;
        internal readonly ConcurrentDictionary<(NetClient, int), byte[]> Downloads = new();
        internal readonly ConcurrentDictionary<(NetClient, int), FileOffer> FileOffers = new();
        internal readonly ConcurrentDictionary<NetClient, string> DisconnectReasons = new();
        internal readonly ConcurrentQueue<RequestEnvelope> Requests = new();
        internal readonly ConcurrentQueue<(NetClient Peer, byte Type, int Id)> SyncPackets = new();
        internal volatile bool AutomaticResponses = true;
        private readonly ConcurrentQueue<Exception> failures = new();
        private readonly Dictionary<(NetClient, int), MemoryStream> chunks = new();
        private readonly Thread pump;
        private volatile bool stopping;

        internal Fixture()
        {
            Logger logger = Construct<Logger>(true, false);
            logger.UseConsole = true;
            logger.LogLevel = 4;
            Server = new Server(new Settings { AnnounceSelf = false, AutoUpdate = false, UseP2P = false, LogLevel = 4 }, logger);
            string application = "friendslegacy.servercheck." + Guid.NewGuid().ToString("N");
            Transport = new NetServer(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback, Port = 0, MaximumConnections = 3 });
            FirstPeer = new NetClient(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback });
            SecondPeer = new NetClient(new NetPeerConfiguration(application) { LocalAddress = IPAddress.Loopback });
            Transport.Start(); FirstPeer.Start(); SecondPeer.Start();
            var endpoint = new IPEndPoint(IPAddress.Loopback, Transport.Port);
            FirstPeer.Connect(endpoint); SecondPeer.Connect(endpoint);
            WaitUntil(() =>
            {
                Drain(Transport); Drain(FirstPeer); Drain(SecondPeer);
                return Transport.ConnectionsCount == 2 && FirstPeer.ConnectionStatus == NetConnectionStatus.Connected && SecondPeer.ConnectionStatus == NetConnectionStatus.Connected;
            }, "isolated peers did not connect");
            typeof(Server).GetField("MainNetServer", All)!.SetValue(Server, Transport);
            First = MakeClient(FirstPeer, 41);
            Second = MakeClient(SecondPeer, 42);
            pump = new Thread(Pump);
            pump.Start();
        }

        private Client MakeClient(NetClient peer, int id)
        {
            NetConnection connection = Transport.Connections.Single(value => value.RemoteEndPoint.Port == peer.Port);
            Client client = Construct<Client>(Server);
            Set(client, "Connection", connection);
            typeof(Client).GetField("NetHandle", All)!.SetValue(client, connection.RemoteUniqueIdentifier);
            Set(client, "Username", "check" + id);
            ServerPed ped = Construct<ServerPed>(Server);
            Set(ped, "ID", id); Set(ped, "Owner", client); Set(client, "Player", ped);
            ((IDictionary)Field(Server, "ClientsByNetHandle")).Add(connection.RemoteUniqueIdentifier, client);
            ((IDictionary)Field(Server, "ClientsByID")).Add(id, client);
            ((IDictionary)Field(Server, "ClientsByName")).Add(client.Username.ToLowerInvariant(), client);
            Call(Field(Server, "Entities"), "Add", ped);
            Set(client, "EntitiesCount", 1); // Initial player registration normally reconciles this in GetHandshake.
            return client;
        }

        private void Pump()
        {
            while (!stopping)
            {
                try
                {
                    NetIncomingMessage? message;
                    while ((message = Transport.ReadMessage()) != null)
                    {
                        // This fixture registers authenticated players itself. Transport
                        // connection notifications must not repeat the host handshake setup.
                        if (message.MessageType == NetIncomingMessageType.Data) { Call(Server, "ProcessMessage", message); }
                        else { Transport.Recycle(message); }
                    }
                    Receive(FirstPeer); Receive(SecondPeer);
                }
                catch (Exception ex) { failures.Enqueue(ex); }
                Thread.Sleep(1);
            }
        }

        private void Receive(NetClient peer)
        {
            NetIncomingMessage? message;
            while ((message = peer.ReadMessage()) != null)
            {
                try
                {
                    if (message.MessageType == NetIncomingMessageType.StatusChanged)
                    {
                        NetConnectionStatus status = (NetConnectionStatus)message.ReadByte();
                        string reason = message.ReadString();
                        if (status == NetConnectionStatus.Disconnected) { DisconnectReasons[peer] = reason; }
                        continue;
                    }
                    if (message.MessageType != NetIncomingMessageType.Data) { continue; }
                    byte type = message.ReadByte();
                    if (type == PacketByte("PedSync") || type == PacketByte("VehicleSync"))
                        SyncPackets.Enqueue((peer, type, message.ReadInt32()));
                    if (type == 6)
                    {
                        int request = message.ReadInt32();
                        byte packetType = message.ReadByte();
                        Requests.Enqueue(new RequestEnvelope(peer, request, packetType));
                        if (!AutomaticResponses) { continue; }
                        int fileId = packetType is 12 or 14 ? message.ReadInt32() : 0;
                        if (packetType == 12)
                        {
                            FileOffers[(peer, fileId)] = new FileOffer(message.ReadString(), message.ReadInt64());
                            chunks[(peer, fileId)] = new MemoryStream();
                            Respond(peer, request, 13, fileId, "NeedToDownload");
                        }
                        else if (packetType == 14)
                        {
                            Downloads[(peer, fileId)] = chunks[(peer, fileId)].ToArray();
                            chunks[(peer, fileId)].Dispose(); chunks.Remove((peer, fileId));
                            Respond(peer, request, 13, fileId, "Completed");
                        }
                        else if (packetType == 15) { Respond(peer, request, 13, 0, "Loaded"); }
                    }
                    else if (type == 11)
                    {
                        int id = message.ReadInt32();
                        byte[] bytes = message.ReadBytes(message.ReadInt32());
                        chunks[(peer, id)].Write(bytes);
                    }
                }
                finally { peer.Recycle(message); }
            }
        }

        internal void Respond(NetClient peer, int request, byte type, int file, string response)
        {
            var message = peer.CreateMessage();
            message.Write((byte)7); message.Write(request); message.Write(type); message.Write(file);
            message.Write(Convert.ToByte(Enum.Parse(typeof(Logger).Assembly.GetType("RageCoop.Core.FileResponse", true)!, response)));
            peer.SendMessage(message, NetDeliveryMethod.ReliableOrdered, 7);
        }

        internal void ThrowPumpFailure() { if (failures.TryDequeue(out Exception? exception)) { throw new InvalidOperationException("Isolated transport pump failed.", exception); } }
        public void Dispose()
        {
            stopping = true;
            pump.Join(3000);
            Server.Stop();
            Transport.Shutdown("check finished"); FirstPeer.Shutdown("check finished"); SecondPeer.Shutdown("check finished");
            foreach (MemoryStream stream in chunks.Values) { stream.Dispose(); }
            ThrowPumpFailure();
        }
        private static void Drain(NetPeer peer) { NetIncomingMessage? message; while ((message = peer.ReadMessage()) != null) { peer.Recycle(message); } }
    }
}
