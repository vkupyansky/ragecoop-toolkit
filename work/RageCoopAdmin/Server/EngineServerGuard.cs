using HostServer = RageCoop.Server.Server;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Core;
using RageCoop.Server;
using RageCoop.Server.Scripting;

namespace CoopAdmin.Server;

// Keep the installed host's worker, transport and resource format. Synchronize its
// shared state at their existing boundaries rather than adding another scheduler.
internal static partial class EngineServerGuard
{
    private const string PatchId = "friendslegacy.ragecoop.engineserver";
    private static readonly ConditionalWeakTable<HostServer, ServerState> States = new();
    private static readonly ConditionalWeakTable<Stream, ResourceIdentity> ResourceNames = new();
    private static readonly List<MethodBase> Patched = new();
    private static readonly object FailureConnectionKey = new();
    private static Harmony? harmony;
    private static Contract? contract;
    [ThreadStatic] private static FileOperation? fileOperation;

    internal static IReadOnlyList<MethodBase> FindTargets() => new Contract().Targets;

    internal static void Install()
    {
        if (harmony != null) { return; }
        contract = new Contract();
        var installing = new Harmony(PatchId);
        try
        {
            Patch(installing, contract.Process, nameof(BeforeProcess), finalizer: nameof(AfterProcess));
            foreach (MethodInfo target in contract.ClientReaders)
                Patch(installing, target, nameof(BeforeClientAccess), finalizer: nameof(AfterClientAccess));
            Patch(installing, contract.CheckLimits, nameof(BeforeCheckLimits), finalizer: nameof(AfterClientAccess));
            foreach (MethodInfo target in contract.EntityMutators)
                Patch(installing, target, nameof(BeforeEntityChange), nameof(AfterEntityChange), nameof(AfterClientAccess));
            foreach (MethodInfo target in contract.SyncReceivers)
                Patch(installing, target, nameof(BeforeSync), finalizer: nameof(AfterClientAccess));
            Patch(installing, contract.PedUpdate, transpiler: nameof(RemovePrematurePedOwner));
            Patch(installing, contract.Cleanup, nameof(BeforeCleanup));
            Patch(installing, contract.Handshake, nameof(BeforeHandshake), finalizer: nameof(AfterHandshake));
            InstallSecurity(installing, contract.Crypto);
            Patch(installing, contract.NewFileId, nameof(BeforeNewFileId));
            Patch(installing, contract.FileSend, nameof(BeforeFileSend), finalizer: nameof(AfterFileSend),
                transpiler: nameof(RewriteFileSend));
            Patch(installing, contract.Ready, nameof(BeforeReady));
            Patch(installing, contract.ResourceBody, nameof(BeforeResourceTask), finalizer: nameof(AfterResourceTask),
                transpiler: nameof(RewriteResourceTask));
            Patch(installing, contract.Disconnected, nameof(BeforeDisconnected), finalizer: nameof(AfterClientAccess));
            Patch(installing, contract.DisconnectLog, nameof(BeforeDisconnectLog));
            Patch(installing, contract.QueueJob, nameof(BeforeQueueJob));
            Patch(installing, contract.Unload, nameof(BeforeUnload));
            Patch(installing, contract.Stop, nameof(BeforeStop), nameof(AfterSecurityStop));
            foreach (MethodInfo target in contract.DeleteHandlers)
                Patch(installing, target, nameof(BeforeDelete));
            harmony = installing;
        }
        catch
        {
            foreach (MethodBase target in Patched) { installing.Unpatch(target, HarmonyPatchType.All, PatchId); }
            Patched.Clear();
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (harmony == null) { return; }
        // A resource can be stopped while a download is awaiting its peer.
        foreach (KeyValuePair<HostServer, ServerState> pair in States) { CancelResponses(pair.Value, null); }
        foreach (MethodBase target in Patched) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        Patched.Clear();
        harmony = null;
    }

    private static void Patch(Harmony installer, MethodInfo target, string? prefix = null, string? postfix = null,
        string? finalizer = null, string? transpiler = null)
    {
        if (!Patched.Contains(target)) { Patched.Add(target); }
        installer.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(EngineServerGuard), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(EngineServerGuard), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(EngineServerGuard), finalizer)
            { priority = finalizer == nameof(AfterProcess) ? Priority.Last : Priority.Normal },
            transpiler: transpiler == null ? null : new HarmonyMethod(typeof(EngineServerGuard), transpiler));
    }

    private static Contract Current => contract ?? throw Unsupported("not installed");
    private static ServerState State(HostServer server) => States.GetValue(server, _ => new ServerState());

    private static HostServer ServerFor(object owner) => owner is HostServer server ? server :
        (HostServer)(Current.ServerFields.TryGetValue(owner.GetType(), out FieldInfo? field) ? field.GetValue(owner)! :
            throw Unsupported("server owner " + owner.GetType().FullName));

    private static void BeforeClientAccess(object __instance, out ServerState __state)
    {
        __state = State(ServerFor(__instance));
        Monitor.Enter(__state.ClientGate);
    }

    private static Exception? AfterClientAccess(Exception? __exception, ServerState? __state)
    {
        if (__state != null) { Monitor.Exit(__state.ClientGate); }
        return __exception;
    }

    private static bool BeforeCheckLimits(HostServer __instance, out ServerState __state)
    {
        __state = State(__instance);
        Monitor.Enter(__state.ClientGate);
        if (__state.Stopping) { return false; }
        // This is an owned-world quota, not a packet rate or a count of menu spawns.
        // Reconcile from the pools before enforcing it, including ambient NPCs.
        Recount(__instance);
        var settings = (Settings)Current.Settings.GetValue(__instance)!;
        var logger = (Logger?)Current.Logger.GetValue(__instance);
        foreach (Client client in Current.Clients(__instance).Values.ToArray())
        {
            if (Connection(client) is not NetConnection { Status: NetConnectionStatus.Connected }) { continue; }
            if (settings.KickSpamming && client.EntitiesCount > settings.SpamLimit)
            {
                logger?.Warning($"Player {client.Username} disconnected: owned entity limit ({client.EntitiesCount} > {settings.SpamLimit}).");
                client.Kick("Превышен допустимый объём объектов. Переподключитесь к серверу.");
            }
            else if (settings.KickGodMode && client.Player.IsInvincible)
            {
                logger?.Warning($"Player {client.Username} disconnected: invincibility is disabled by server policy.");
                client.Kick("На этом сервере бессмертие запрещено.");
            }
        }
        return false;
    }

    private static bool BeforeProcess(HostServer __instance, object[] __args, out ServerState __state)
    {
        __state = State(__instance);
        Monitor.Enter(__state.ClientGate);
        object? message = __args[0];
        if (message == null) { return true; }
        if (message is NetIncomingMessage
            {
                MessageType: NetIncomingMessageType.Data or NetIncomingMessageType.StatusChanged,
                SenderConnection: NetConnection connection
            } sessionMessage &&
            Current.Clients(__instance).TryGetValue(connection.RemoteUniqueIdentifier, out Client? currentClient) &&
            !ReferenceEquals(Connection(currentClient), connection))
        {
            // A reconnect can reuse its peer identifier. A delayed message from
            // the previous connection must not act on the replacement session.
            Recycle(__instance, sessionMessage);
            return false;
        }
        if (message is NetIncomingMessage incoming && incoming.MessageType is NetIncomingMessageType.Data or
            NetIncomingMessageType.ConnectionApproval or NetIncomingMessageType.UnconnectedData)
        {
            if (incoming.Data == null || incoming.Position != 0 || incoming.LengthBits < 8 || incoming.LengthBits > (long)incoming.Data.Length * 8)
                throw new InvalidDataException("Truncated server message envelope.");
            if (incoming.MessageType == NetIncomingMessageType.Data && incoming.Data[0] == 6 && incoming.LengthBits < 48)
                throw new InvalidDataException("Truncated server request envelope.");
        }
        return !TryHandleResponse(__instance, __state, message);
    }

    private static Exception? AfterProcess(HostServer __instance, object[] __args, Exception? __exception, ServerState? __state)
    {
        try
        {
            // Stock ProcessMessage recycles at its normal exit, but misses every exceptional exit.
            if (__exception != null && __args[0] is object message)
            {
                // Listen reads SenderConnection after this finalizer. Recycle resets it,
                // and the network thread may already reuse the object for another peer.
                __exception.Data[FailureConnectionKey] = new FailureConnection(__instance,
                    message.GetType().GetProperty("SenderConnection")!.GetValue(message));
                Recycle(__instance, message);
            }
            return __exception;
        }
        finally { if (__state != null) { Monitor.Exit(__state.ClientGate); } }
    }

    private static bool Connected(HostServer server, Client client) =>
        !State(server).Stopping && Current.Clients(server).TryGetValue((long)Current.NetHandle.GetValue(client)!,
            out Client? actual) && ReferenceEquals(actual, client);

    private static bool BeforeEntityChange(object __instance, object[] __args, out ServerState __state)
    {
        HostServer server = ServerFor(__instance);
        __state = State(server);
        Monitor.Enter(__state.ClientGate);
        // Snapshots already queued before a disconnect must not recreate its entities.
        Client? sender = __args.Length > 1 ? __args[1] as Client : null;
        sender ??= __args.Length > 0 ? __args[0] as Client : null;
        return sender == null || Connected(server, sender) &&
            (__args.Length < 2 || !Current.SyncPacketTypes.Contains(__args[0]?.GetType()!) || CanUseSyncId(server, __args[0], sender));
    }

    private static bool BeforeSync(HostServer __instance, object[] __args, out ServerState __state)
    {
        __state = State(__instance);
        Monitor.Enter(__state.ClientGate);
        // Reject before both forwarding and queueing. Update checks again when
        // its worker job runs, because a handshake may reserve the ID meanwhile.
        if (__args[1] is not Client sender || !Connected(__instance, sender) || !CanUseSyncId(__instance, __args[0], sender)) { return false; }
        // Local clients only publish entities they currently own. The server is
        // the authority for the authenticated sender; its relay must agree with
        // ServerEntities.Update, which also assigns that sender as Owner.
        __args[0].GetType().GetProperty("OwnerID")!.SetValue(__args[0], sender.Player.ID);
        return true;
    }

    private static bool CanUseSyncId(HostServer server, object packet, Client sender)
    {
        int id = (int)packet.GetType().GetProperty("ID")!.GetValue(packet)!;
        if (id == 0) { return false; }
        if (!Current.ClientIds(server).TryGetValue(id, out Client? player)) { return true; }
        // Player IDs reserve the global entity ID even for vehicle packets.
        return packet.GetType() == Current.SyncPacketTypes[0] && ReferenceEquals(player, sender);
    }

    private static bool BeforeHandshake(HostServer __instance, NetConnection connection, object packet, out HandshakeLease __state)
    {
        __state = BeginHandshake(__instance, connection);
        int id = (int)packet.GetType().GetProperty("PedID")!.GetValue(packet)!;
        if (__state.State.Stopping || id == 0 || Current.Clients(__instance).ContainsKey(connection.RemoteUniqueIdentifier) ||
            Current.ClientIds(__instance).ContainsKey(id) || Current.Peds(__instance).ContainsKey(id) ||
            Current.Vehicles(__instance).ContainsKey(id) ||
            __instance.API.Entities.GetPropByID(id) != null || __instance.API.Entities.GetBlipByID(id) != null)
        {
            connection.Deny("Не удалось подключить персонажа. Переподключитесь к серверу.");
            return false;
        }
        return true;
    }

    private static void AfterEntityChange(object __instance, MethodBase __originalMethod)
    {
        // Snapshot updates retain the host's now-correct constant-time transfer counters.
        // Reconcile only creation/deletion boundaries, including server-created cars.
        if (__originalMethod.Name != "Update") { Recount(ServerFor(__instance)); }
    }

    private static void Recount(HostServer server)
    {
        var counts = Current.Clients(server).Values.ToDictionary(client => client, _ => 0);
        foreach (ServerObject entity in Current.Peds(server).Values.Cast<ServerObject>().Concat(Current.Vehicles(server).Values))
            if (entity.Owner is Client owner && counts.TryGetValue(owner, out int count)) { counts[owner] = count + 1; }
        foreach (KeyValuePair<Client, int> pair in counts) { Current.EntityCount.SetValue(pair.Key, pair.Value); }
    }

    internal static IEnumerable<CodeInstruction> RemovePrematurePedOwner(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> instructions = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        int first = FindPrematurePedOwner(instructions);
        if (first < 0) { throw Unsupported("ped ownership IL"); }
        // Discard just the premature assignment. The original ownership-transfer block remains.
        instructions[first].opcode = OpCodes.Pop;
        instructions[first].operand = null;
        instructions.Insert(first + 1, new CodeInstruction(OpCodes.Pop));
        return instructions;
    }

    internal static int FindPrematurePedOwner(IList<CodeInstruction> instructions)
    {
        MethodInfo setter = typeof(ServerObject).GetProperty(nameof(ServerObject.Owner))!.GetSetMethod(true)!;
        int[] assignments = Enumerable.Range(0, instructions.Count).Where(index => instructions[index].Calls(setter)).ToArray();
        return assignments.Length == 2 && assignments[0] >= 2 && instructions[assignments[0] - 1].opcode == OpCodes.Ldarg_2 &&
            instructions[assignments[0] - 2].IsLdloc() ? assignments[0] : -1;
    }

    private static bool BeforeCleanup(object __instance, Client left)
    {
        HostServer server = ServerFor(__instance);
        Invoke(Current.QueueJob, server, new Action(() =>
        {
            lock (State(server).ClientGate)
            {
                RemoveOwned(Current.Peds(server), left);
                RemoveOwned(Current.Vehicles(server), left);
                Current.EntityCount.SetValue(left, 0);
                Recount(server);
            }
        }));
        return false;
    }

    private static void RemoveOwned<T>(ConcurrentDictionary<int, T> entities, Client left) where T : ServerObject
    {
        foreach (KeyValuePair<int, T> pair in entities)
            if (ReferenceEquals(pair.Value.Owner, left))
                ((ICollection<KeyValuePair<int, T>>)entities).Remove(pair);
    }

    private static bool BeforeDelete(object __instance, CustomEventReceivedArgs e, MethodBase __originalMethod)
    {
        HostServer server = ServerFor(__instance);
        lock (State(server).ClientGate)
        {
            if (e.Client == null || !Connected(server, e.Client) || e.Args is not { Length: 1 } ||
                e.Args[0] is not int id || id == 0) { return false; }
            ServerObject? entity = __originalMethod == Current.DeleteHandlers[0] ?
                server.API.Entities.GetVehicleByID(id) : server.API.Entities.GetPedByID(id);
            return entity != null && ReferenceEquals(entity.Owner, e.Client) &&
                !Current.ClientIds(server).ContainsKey(id);
        }
    }

    private static bool BeforeFileSend(HostServer __instance, Stream stream, ref string name, Client client, out FileOperation? __state)
    {
        __state = null;
        lock (State(__instance).ClientGate)
            if (!Connected(__instance, client)) { throw new OperationCanceledException("The file recipient disconnected."); }
        Monitor.Enter(stream);
        __state = new FileOperation(stream, fileOperation);
        fileOperation = __state;
        lock (State(__instance).ClientGate)
            if (!Connected(__instance, client)) { throw new OperationCanceledException("The file recipient disconnected."); }
        if (name == "CoopControl.res" && Current.IsOwnClientResource(__instance, stream))
        {
            // Resource packs remain immutable until UnloadAll. Their stream monitor
            // also protects hashing, so another transfer cannot move the position.
            if (!ResourceNames.TryGetValue(stream, out ResourceIdentity? identity))
            {
                long position = stream.Position;
                byte[] hash;
                try
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    using SHA256 algorithm = SHA256.Create();
                    hash = algorithm.ComputeHash(stream);
                }
                finally { stream.Seek(position, SeekOrigin.Begin); }
                identity = new ResourceIdentity("CoopControl." + Convert.ToHexStringLower(hash) + ".res");
                ResourceNames.Add(stream, identity);
            }
            name = identity.WireName;
        }
        return true;
    }

    private static Exception? AfterFileSend(Exception? __exception, FileOperation? __state)
    {
        if (__state != null)
        {
            try
            {
                if (__state.Table is IDictionary table)
                    lock (table)
                        if (ReferenceEquals(table[__state.Id], __state.Transfer)) { table.Remove(__state.Id); }
            }
            finally { fileOperation = __state.Previous; Monitor.Exit(__state.Stream); }
        }
        return __exception;
    }

    private static bool BeforeNewFileId(HostServer __instance, ref int __result)
    {
        var table = (IDictionary)Current.Transfers.GetValue(__instance)!;
        ServerState state = State(__instance);
        lock (table)
        {
            do { state.NextFileId = state.NextFileId == int.MaxValue ? 1 : state.NextFileId + 1; }
            while (table.Contains(state.NextFileId));
            __result = state.NextFileId;
        }
        return false;
    }

    private static void AddTransfer(object value, int id, object transfer)
    {
        var table = (IDictionary)value;
        lock (table) { table.Add(id, transfer); }
        FileOperation operation = fileOperation ?? throw Unsupported("file transfer scope");
        operation.Table = table; operation.Id = id; operation.Transfer = transfer;
    }

    private static bool RemoveTransfer(object value, int id)
    {
        var table = (IDictionary)value;
        lock (table)
        {
            bool present = table.Contains(id);
            table.Remove(id);
            return present;
        }
    }

    private static bool BeforeResourceTask(object __instance, out ServerState? __state)
    {
        HostServer server = ServerFor(Current.ResourceOwner.GetValue(__instance)!);
        ServerState state = State(server);
        lock (state.ClientGate)
        {
            __state = null;
            if (state.Stopping) { return false; }
            state.ResourceTasks++;
            state.ResourcesIdle.Reset();
            __state = state;
            return true;
        }
    }

    private static Exception? AfterResourceTask(Exception? __exception, ServerState? __state)
    {
        if (__state != null)
        {
            lock (__state.ClientGate)
                if (--__state.ResourceTasks == 0) { __state.ResourcesIdle.Set(); }
        }
        return __exception;
    }

    internal static IEnumerable<CodeInstruction> DeferReadyFlag(IEnumerable<CodeInstruction> original)
    {
        int matches = 0;
        int events = 0;
        foreach (CodeInstruction source in original)
        {
            CodeInstruction instruction = new(source);
            if (instruction.Calls(Current.ReadySetter))
            {
                matches++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EngineServerGuard), nameof(LeaveReadyToWorker));
            }
            if (instruction.Calls(Current.Ready))
            {
                events++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EngineServerGuard), nameof(ScheduleReady));
            }
            yield return instruction;
        }
        if (matches != 1 || events != 1) { throw Unsupported("resource readiness IL"); }
    }

    internal static IEnumerable<CodeInstruction> RewriteResourceTask(IEnumerable<CodeInstruction> original) =>
        ReplaceResponseCalls(DeferReadyFlag(original), 1);

    internal static IEnumerable<CodeInstruction> RewriteFileSend(IEnumerable<CodeInstruction> original) =>
        ReplaceTransferMutations(ReplaceResponseCalls(original, 2));

    private static IEnumerable<CodeInstruction> ReplaceTransferMutations(IEnumerable<CodeInstruction> original)
    {
        int added = 0, removed = 0;
        foreach (CodeInstruction source in original)
        {
            CodeInstruction instruction = new(source);
            if (instruction.operand is MethodInfo method && method.DeclaringType == Current.Transfers.FieldType)
            {
                if (method.Name == "Add") { added++; instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(EngineServerGuard), nameof(AddTransfer)); }
                if (method.Name == "Remove") { removed++; instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(EngineServerGuard), nameof(RemoveTransfer)); }
            }
            yield return instruction;
        }
        if (added != 1 || removed != 1) { throw Unsupported("file transfer table IL"); }
    }

    private static IEnumerable<CodeInstruction> ReplaceResponseCalls(IEnumerable<CodeInstruction> original, int expected)
    {
        int matches = 0;
        foreach (CodeInstruction source in original)
        {
            CodeInstruction instruction = new(source);
            if (instruction.operand is MethodInfo method && method == Current.Response)
            {
                matches++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EngineServerGuard), nameof(RequestResponse));
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Castclass, Current.FileResponseType);
            }
            else { yield return instruction; }
        }
        if (matches != expected) { throw Unsupported("file response call sites"); }
    }

    private static void LeaveReadyToWorker(Client client, bool ready) { }
    private static void ScheduleReady(object events, Client client) => BeforeReady(events, client);

    private static bool BeforeReady(object __instance, Client client)
    {
        HostServer server = ServerFor(__instance);
        Invoke(Current.QueueJob, server, new Action(() =>
        {
            lock (State(server).ClientGate)
            {
                if (!Connected(server, client) || client.IsReady) { return; }
                Current.ReadySetter.Invoke(client, new object[] { true });
                if (Current.ReadyEvent.GetValue(__instance) is EventHandler<Client> handler) { handler(__instance, client); }
            }
        }));
        return false;
    }

    private static bool BeforeDisconnected(HostServer __instance, Client localClient, out ServerState __state)
    {
        __state = State(__instance);
        Monitor.Enter(__state.ClientGate);
        if (!Connected(__instance, localClient)) { return false; }
        Current.ReadySetter.Invoke(localClient, new object[] { false });
        CancelResponses(State(__instance), localClient);
        return true;
    }

    private static bool BeforeDisconnectLog(HostServer __instance, object[] __args)
    {
        if (__args[2] is Exception failure && failure.Data[FailureConnectionKey] is FailureConnection saved &&
            ReferenceEquals(saved.Server, __instance))
        {
            __args[0] = saved.Connection!;
            failure.Data.Remove(FailureConnectionKey);
        }
        if (__args[0] is NetConnection connection && __args[2] is InvalidDataException malformed)
        {
            if (Current.Logger.GetValue(__instance) is Logger details)
            {
                details.Error("Invalid server packet of type " + __args[1]);
                details.Error(malformed.Message);
                details.Error(malformed.StackTrace);
            }
            connection.Disconnect("Не удалось прочитать данные игры. Переподключитесь к серверу.");
            return false;
        }
        if (__args[0] != null) { return true; }
        if (Current.Logger.GetValue(__instance) is Logger logger)
            logger.Error("Invalid unconnected server message", (Exception)__args[2]);
        return false;
    }

    private static bool BeforeQueueJob(HostServer __instance) => !State(__instance).Stopping;

    private static void BeforeUnload(object __instance)
    {
        HostServer server = ServerFor(__instance);
        ServerState state = State(server);
        if (!state.ResourcesIdle.Wait(10000)) { throw new TimeoutException("Client resource transfers did not stop."); }
        StopWorker(server, state);
    }

    private static void StopWorker(HostServer server, ServerState state)
    {
        lock (state.ClientGate)
        {
            if (state.WorkerStopped) { return; }
            state.WorkerStopped = true;
        }
        ((IDisposable)Current.Worker.GetValue(server)!).Dispose();
    }

    private static bool BeforeStop(HostServer __instance)
    {
        ServerState state = State(__instance);
        lock (state.ClientGate)
        {
            if (state.Stopping) { return false; }
            state.Stopping = true;
            foreach (Client client in Current.Clients(__instance).Values) { Current.ReadySetter.Invoke(client, new object[] { false }); }
        }
        foreach (FieldInfo field in Current.Timers)
        {
            var timer = (System.Timers.Timer)field.GetValue(__instance)!;
            timer.Stop();
            timer.Dispose();
        }
        CancelResponses(state, null);
        if (!state.ResourcesIdle.Wait(10000)) { throw new TimeoutException("Client resource transfers did not stop."); }
        Current.Stopping.SetValue(__instance, true);
        var listener = (Thread)Current.Listener.GetValue(__instance)!;
        if (listener.IsAlive && !listener.Join(10000)) { throw new TimeoutException("HostServer listener did not stop."); }
        StopWorker(__instance, state);
        state.ResourcesIdle.Dispose();
        if (Current.Logger.GetValue(__instance) is Logger logger) { logger.Flush(); logger.Dispose(); }
        return false;
    }

    private static object? Invoke(MethodBase method, object? owner, params object?[] args)
    {
        try { return method.Invoke(owner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static InvalidOperationException Unsupported(string detail) => new("Unsupported RageCoop server contract: " + detail + ".");

    private sealed class ServerState
    {
        internal readonly object ClientGate = new();
        internal readonly object ResponseGate = new();
        internal readonly Dictionary<int, PendingResponse> Responses = new();
        internal readonly ManualResetEventSlim ResourcesIdle = new(true);
        internal volatile bool Stopping;
        internal bool WorkerStopped;
        internal int ResourceTasks;
        internal int NextFileId;
    }

    private sealed class FileOperation(Stream stream, FileOperation? previous)
    {
        internal Stream Stream { get; } = stream;
        internal FileOperation? Previous { get; } = previous;
        internal IDictionary? Table;
        internal int Id;
        internal object? Transfer;
    }

    private sealed record FailureConnection(HostServer Server, object? Connection);
    private sealed record ResourceIdentity(string WireName);

    private sealed class Contract
    {
        internal readonly SecurityContract Crypto;
        internal readonly Dictionary<Type, FieldInfo> ServerFields = new();
        internal readonly MethodInfo Process, PedUpdate, Cleanup, Handshake, FileSend, ResourceBody, Ready, Response,
            Disconnected, DisconnectLog, QueueJob, Unload, Stop, ReadySetter, NewFileId, CheckLimits;
        internal readonly MethodInfo[] ClientReaders, EntityMutators, DeleteHandlers, SyncReceivers;
        internal readonly Type[] SyncPacketTypes;
        internal readonly FieldInfo ClientsByNetHandle, ClientsById, Entities, NetHandle, Worker, Listener, Stopping, Logger, Settings,
            ReadyEvent, ResourceOwner, Transfers, Resources, ClientResources;
        internal readonly FieldInfo[] Timers;
        internal readonly PropertyInfo EntityCount, PedsProperty, VehiclesProperty;
        internal readonly Type FileResponseType, PacketType;
        internal readonly IReadOnlyList<MethodBase> Targets;

        internal Contract()
        {
            if (typeof(HostServer).Assembly.GetName().Version != new Version(1, 5, 12, 15)) { throw Unsupported("host version"); }
            Crypto = new SecurityContract();
            Type server = typeof(HostServer), api = typeof(API), entities = typeof(ServerEntities), events = typeof(ServerEvents);
            Type resources = server.Assembly.GetType("RageCoop.Server.Scripting.Resources", true)!;
            Type baseScript = server.Assembly.GetType("RageCoop.Server.Scripting.BaseScript", true)!;
            Type packet = typeof(Logger).Assembly.GetType("RageCoop.Core.Packet", true)!;
            PacketType = typeof(Logger).Assembly.GetType("RageCoop.Core.PacketType", true)!;
            FileResponseType = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+FileTransferResponse", true)!;
            Type pedPacket = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+PedSync", true)!;
            Type vehiclePacket = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", true)!;
            SyncPacketTypes = new[] { pedPacket, vehiclePacket };
            SyncReceivers = new[]
            {
                NeedMethod(server, "PedSync", typeof(void), pedPacket.FullName!, typeof(Client).FullName!),
                NeedMethod(server, "VehicleSync", typeof(void), vehiclePacket.FullName!, typeof(Client).FullName!)
            };
            Process = NeedMethod(server, "ProcessMessage", typeof(void), "Lidgren.Network.NetIncomingMessage");
            PedUpdate = NeedMethod(entities, "Update", typeof(void), pedPacket.FullName!, typeof(Client).FullName!);
            MethodInfo vehicleUpdate = NeedMethod(entities, "Update", typeof(void), vehiclePacket.FullName!, typeof(Client).FullName!);
            if (FindPrematurePedOwner(PatchProcessor.GetOriginalInstructions(PedUpdate)) < 0) { throw Unsupported("ped ownership IL"); }
            Cleanup = NeedMethod(entities, "CleanUp", typeof(void), typeof(Client).FullName!);
            Handshake = NeedMethod(server, "GetHandshake", typeof(void), "Lidgren.Network.NetConnection", "RageCoop.Core.Packets+Handshake");
            FileSend = NeedMethod(server, "SendFile", typeof(void), typeof(Stream).FullName!, typeof(string).FullName!,
                typeof(Client).FullName!, typeof(int).FullName!, typeof(Action<float>).FullName!);
            ReadySetter = typeof(Client).GetProperty(nameof(Client.IsReady))!.GetSetMethod(true)!;
            Ready = NeedMethod(events, "InvokePlayerReady", typeof(void), typeof(Client).FullName!);
            ResourceBody = resources.GetNestedTypes(BindingFlags.NonPublic).SelectMany(type => type.GetMethods(All | BindingFlags.DeclaredOnly))
                .Single(method => method.ReturnType == typeof(void) && method.GetParameters().Length == 0 &&
                    PatchProcessor.GetOriginalInstructions(method).Count(instruction => instruction.Calls(ReadySetter)) == 1 &&
                    PatchProcessor.GetOriginalInstructions(method).Any(instruction => instruction.Calls(Ready)));
            ResourceOwner = ResourceBody.DeclaringType!.GetFields(All).Single(field => field.FieldType == resources);
            MethodInfo responseDefinition = server.GetMethods(All).Single(method => method.Name == "GetResponse" && method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == typeof(Client) &&
                method.GetParameters()[1].ParameterType == packet && method.GetParameters()[3].ParameterType == typeof(int));
            Response = responseDefinition.MakeGenericMethod(FileResponseType);
            if (PatchProcessor.GetOriginalInstructions(FileSend).Count(instruction => instruction.Calls(Response)) != 2 ||
                PatchProcessor.GetOriginalInstructions(ResourceBody).Count(instruction => instruction.Calls(Response)) != 1)
                throw Unsupported("file response call sites");
            Disconnected = NeedMethod(server, "PlayerDisconnected", typeof(void), typeof(Client).FullName!);
            DisconnectLog = NeedMethod(server, "DisconnectAndLog", typeof(void), "Lidgren.Network.NetConnection", PacketType.FullName!, typeof(Exception).FullName!);
            QueueJob = NeedMethod(server, "QueueJob", typeof(void), typeof(Action).FullName!);
            Unload = NeedMethod(resources, "UnloadAll", typeof(void));
            Stop = NeedMethod(server, "Stop", typeof(void));
            NewFileId = NeedMethod(server, "NewFileID", typeof(int));
            CheckLimits = NeedMethod(server, "KickAssholes", typeof(void));
            ClientReaders = new[]
            {
                NeedMethod(server, "SendPlayerUpdate", typeof(void)),
                NeedMethod(server, "ChatMessageReceived", typeof(void), typeof(string).FullName!, typeof(string).FullName!, typeof(Client).FullName!),
                NeedMethod(server, "SendChatMessage", typeof(void), typeof(string).FullName!, typeof(string).FullName!, typeof(Client).FullName!),
                NeedMethod(api, "GetAllClients", typeof(Dictionary<int, Client>)),
                NeedMethod(api, "GetClientByUsername", typeof(Client), typeof(string).FullName!),
                NeedMethod(api, "SendCustomEvent", typeof(void), typeof(List<Client>).FullName!, typeof(int).FullName!, typeof(object[]).FullName!),
                NeedMethod(api, "SendCustomEventQueued", typeof(void), typeof(List<Client>).FullName!, typeof(int).FullName!, typeof(object[]).FullName!),
                NeedMethod(api, "SendChatMessage", typeof(void), typeof(string).FullName!, typeof(List<Client>).FullName!, typeof(string).FullName!, typeof(bool?).FullName!),
                api.GetProperty(nameof(API.Host))!.GetSetMethod()!
            };
            EntityMutators = new[]
            {
                PedUpdate, vehicleUpdate,
                NeedMethod(entities, "CreateVehicle", typeof(ServerVehicle), typeof(Client).FullName!, typeof(GTA.Model).FullName!, typeof(GTA.Math.Vector3).FullName!, typeof(float).FullName!),
                NeedMethod(entities, "RemoveVehicle", typeof(void), typeof(int).FullName!),
                NeedMethod(entities, "RemovePed", typeof(void), typeof(int).FullName!)
            };
            DeleteHandlers = new[] { "RemoveVehicle", "RemovePed" }.Select(name => baseScript.GetMethods(All).Single(method =>
                method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(CustomEventReceivedArgs) &&
                PatchProcessor.GetOriginalInstructions(method).Any(instruction => instruction.operand is MethodInfo called &&
                    called.DeclaringType == entities && called.Name == name))).ToArray();
            foreach (Type type in new[] { api, entities, events, resources, baseScript })
                ServerFields.Add(type, NeedField(type, "Server", typeof(HostServer)));
            ClientsByNetHandle = NeedField(server, "ClientsByNetHandle", typeof(Dictionary<long, Client>));
            ClientsById = NeedField(server, "ClientsByID", typeof(Dictionary<int, Client>));
            Entities = NeedField(server, "Entities", typeof(ServerEntities));
            NetHandle = NeedField(typeof(Client), "NetHandle", typeof(long));
            Worker = NeedField(server, "_worker", typeof(Worker));
            Listener = NeedField(server, "_listenerThread", typeof(Thread));
            Stopping = NeedField(server, "_stopping", typeof(bool));
            Logger = NeedField(server, "Logger", typeof(Logger));
            Settings = NeedField(server, "Settings", typeof(Settings));
            Type fileTransfer = server.Assembly.GetType("RageCoop.Server.FileTransfer", true)!;
            Transfers = NeedField(server, "InProgressFileTransfers", typeof(Dictionary<,>).MakeGenericType(typeof(int), fileTransfer));
            Resources = NeedField(server, "Resources", resources);
            ClientResources = NeedField(resources, "ClientResources", typeof(Dictionary<string, Stream>));
            ReadyEvent = PatchProcessor.GetOriginalInstructions(Ready).Select(instruction => instruction.operand).OfType<FieldInfo>()
                .Single(field => field.FieldType == typeof(EventHandler<Client>) && field.DeclaringType == events);
            EntityCount = typeof(Client).GetProperty(nameof(Client.EntitiesCount))!;
            PedsProperty = entities.GetProperty("Peds", All) ?? throw Unsupported("ped pool");
            VehiclesProperty = entities.GetProperty("Vehicles", All) ?? throw Unsupported("vehicle pool");
            if (PedsProperty.PropertyType != typeof(ConcurrentDictionary<int, ServerPed>) ||
                VehiclesProperty.PropertyType != typeof(ConcurrentDictionary<int, ServerVehicle>)) { throw Unsupported("entity pool types"); }
            Timers = new[] { "_playerUpdateTimer", "_announceTimer", "_antiAssholesTimer", "_updateTimer" }
                .Select(name => NeedField(server, name, typeof(System.Timers.Timer))).ToArray();
            Targets = ClientReaders.Concat(EntityMutators).Concat(DeleteHandlers).Concat(SyncReceivers).Concat(Crypto.Methods).Concat(new[]
            { Process, Cleanup, Handshake, FileSend, NewFileId, ResourceBody, Ready, Disconnected, DisconnectLog, QueueJob, Unload, Stop, CheckLimits }).Distinct().Cast<MethodBase>().ToArray();
        }

        internal Dictionary<long, Client> Clients(HostServer server) => (Dictionary<long, Client>)ClientsByNetHandle.GetValue(server)!;
        internal Dictionary<int, Client> ClientIds(HostServer server) => (Dictionary<int, Client>)ClientsById.GetValue(server)!;
        internal ConcurrentDictionary<int, ServerPed> Peds(HostServer server) => (ConcurrentDictionary<int, ServerPed>)PedsProperty.GetValue(Entities.GetValue(server))!;
        internal ConcurrentDictionary<int, ServerVehicle> Vehicles(HostServer server) => (ConcurrentDictionary<int, ServerVehicle>)VehiclesProperty.GetValue(Entities.GetValue(server))!;
        internal bool IsOwnClientResource(HostServer server, Stream stream) =>
            ((Dictionary<string, Stream>)ClientResources.GetValue(Resources.GetValue(server))!).TryGetValue("CoopControl", out Stream? pack) &&
            ReferenceEquals(pack, stream);
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo NeedMethod(Type type, string name, Type returns, params string[] arguments) =>
            type.GetMethods(All).SingleOrDefault(method => method.Name == name && method.ReturnType == returns &&
                method.GetParameters().Select(parameter => parameter.ParameterType.FullName).SequenceEqual(arguments)) ?? throw Unsupported(type.Name + "." + name);

        private static FieldInfo NeedField(Type type, string name, Type fieldType) =>
            type.GetField(name, All) is FieldInfo field && field.FieldType == fieldType ? field : throw Unsupported(type.Name + "." + name);
    }
}
