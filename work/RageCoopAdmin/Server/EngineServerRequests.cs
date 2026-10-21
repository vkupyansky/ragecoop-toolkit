using HostServer = RageCoop.Server.Server;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using RageCoop.Server;

namespace CoopAdmin.Server;

internal static partial class EngineServerGuard
{
    private const int MaximumPendingResponses = 128;

    internal static object? RequestResponse(HostServer server, Client client, object request, int channel, int timeout)
    {
        if (timeout < 1 || timeout > 60000) { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        if (Thread.CurrentThread == Current.Listener.GetValue(server))
            throw new InvalidOperationException("Cannot wait for response from the listener thread!");
        ServerState state = State(server);
        object response = Activator.CreateInstance(Current.FileResponseType, nonPublic: true)!;
        byte responseType = Convert.ToByte(Current.FileResponseType.GetProperty("Type")!.GetValue(response));
        byte requestType = Convert.ToByte(request.GetType().GetProperty("Type")!.GetValue(request));
        // AllResourcesSent has no file ID; its protocol response explicitly uses zero.
        int fileId = request.GetType().GetProperty("ID")?.GetValue(request) is int requestedId ? requestedId : 0;
        using var pending = new PendingResponse(client, response, responseType, fileId, requestType);
        int id;
        lock (state.ClientGate)
        {
            if (!Connected(server, client)) { throw new OperationCanceledException("The response peer disconnected."); }
            lock (state.ResponseGate)
            {
                if (state.Responses.Count >= MaximumPendingResponses) { throw new InvalidOperationException("Too many pending server requests."); }
                do { id = RandomNumberGenerator.GetInt32(1, int.MaxValue); } while (state.Responses.ContainsKey(id));
                state.Responses.Add(id, pending);
            }
        }
        try
        {
            object peer = Peer(server);
            object message = Invoke(peer.GetType().GetMethod("CreateMessage", Type.EmptyTypes)!, peer)!;
            Write(message, (byte)6);
            Write(message, id);
            Invoke(request.GetType().GetMethod("Pack")!, request, message);
            Send(peer, message, client, channel);
            bool received = pending.Received.Wait(timeout);
            lock (state.ResponseGate)
            {
                if (pending.Failure != null) { throw new IOException("The server request did not complete.", pending.Failure); }
                if (!received && requestType != Convert.ToByte(Enum.Parse(Current.PacketType, "AllResourcesSent")))
                    throw new TimeoutException("The client did not confirm the file transfer.");
                return received ? pending.Response : null;
            }
        }
        finally
        {
            // The same gate protects Set from racing with disposal of the waiter.
            lock (state.ResponseGate) { state.Responses.Remove(id); }
        }
    }

    private static bool TryHandleResponse(HostServer server, ServerState state, object message)
    {
        Type incoming = message.GetType();
        if (Convert.ToInt32(incoming.GetProperty("MessageType")!.GetValue(message)) != 8) { return false; }
        int length = (int)incoming.GetProperty("LengthBytes")!.GetValue(message)!;
        int bits = (int)incoming.GetProperty("LengthBits")!.GetValue(message)!;
        byte[] bytes = (byte[])incoming.GetProperty("Data")!.GetValue(message)!;
        if (length == 0 || bytes[0] != 7) { return false; }
        bool handled = false;
        try
        {
            if (bits < 48 || length < 6) { throw new InvalidDataException("Truncated response header."); }
            int id = BitConverter.ToInt32(bytes, 1);
            lock (state.ResponseGate)
            {
                if (!state.Responses.TryGetValue(id, out PendingResponse? pending)) { handled = true; return true; }
                object? connection = incoming.GetProperty("SenderConnection")!.GetValue(message);
                if (!ReferenceEquals(Connection(pending.Client), connection) || bytes[5] != pending.Type) { handled = true; return true; }
                if (length >= 10 && BitConverter.ToInt32(bytes, 6) != pending.FileId) { handled = true; return true; }
                try
                {
                    Type responseCode = pending.Response.GetType().GetProperty("Response")!.PropertyType;
                    if (bits != 88 || length != 11 || !Enum.IsDefined(responseCode, Enum.ToObject(responseCode, bytes[10])))
                        throw new InvalidDataException("Invalid file response payload.");
                    string? code = Enum.GetName(responseCode, Enum.ToObject(responseCode, bytes[10]));
                    string? phase = Enum.GetName(Current.PacketType, Enum.ToObject(Current.PacketType, pending.RequestType));
                    bool validPhase = phase switch
                    {
                        "FileTransferRequest" => code is "NeedToDownload" or "AlreadyExists",
                        "FileTransferComplete" => code == "Completed",
                        "AllResourcesSent" => code is "Loaded" or "LoadFailed",
                        _ => false
                    };
                    if (!validPhase) { throw new InvalidDataException("Unexpected file response phase."); }
                    Invoke(incoming.GetMethod("ReadByte", Type.EmptyTypes)!, message);
                    Invoke(incoming.GetMethod("ReadInt32", Type.EmptyTypes)!, message);
                    Invoke(incoming.GetMethod("ReadByte", Type.EmptyTypes)!, message);
                    Invoke(pending.Response.GetType().GetMethod("Deserialize")!, pending.Response, message);
                }
                catch (Exception ex) { pending.Failure = ex; }
                state.Responses.Remove(id);
                pending.Received.Set();
            }
            handled = true;
            return true;
        }
        finally { if (handled) { Recycle(server, message); } }
    }

    private static void CancelResponses(ServerState state, Client? client)
    {
        lock (state.ResponseGate)
        {
            foreach (int id in state.Responses.Where(pair => client == null || ReferenceEquals(pair.Value.Client, client)).Select(pair => pair.Key).ToArray())
            {
                PendingResponse pending = state.Responses[id];
                pending.Failure = new OperationCanceledException("The response session closed.");
                state.Responses.Remove(id);
                pending.Received.Set();
            }
        }
    }

    internal static int PendingResponseCount(HostServer server)
    {
        ServerState state = State(server);
        lock (state.ResponseGate) { return state.Responses.Count; }
    }

    private static object Peer(HostServer server) => typeof(HostServer).GetField("MainNetServer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
    private static object? Connection(Client client) => typeof(Client).GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

    private static void Write<T>(object message, T value) where T : struct =>
        Invoke(message.GetType().GetMethod("Write", new[] { typeof(T) })!, message, value);

    private static void Send(object peer, object message, Client client, int channel)
    {
        MethodInfo method = peer.GetType().GetMethods().Single(candidate => candidate.Name == "SendMessage" &&
            candidate.GetParameters().Length == 4 && candidate.GetParameters()[1].ParameterType.FullName == "Lidgren.Network.NetConnection");
        object reliable = Enum.ToObject(method.GetParameters()[2].ParameterType, 67);
        Invoke(method, peer, message, Connection(client), reliable, channel);
    }

    private static void Recycle(HostServer server, object message) =>
        Invoke(Peer(server).GetType().GetMethod("Recycle", new[] { message.GetType() })!, Peer(server), message);

    private sealed class PendingResponse : IDisposable
    {
        internal PendingResponse(Client client, object response, byte type, int fileId, byte requestType)
        { Client = client; Response = response; Type = type; FileId = fileId; RequestType = requestType; }
        internal Client Client { get; }
        internal object Response { get; }
        internal byte Type { get; }
        internal int FileId { get; }
        internal byte RequestType { get; }
        internal readonly ManualResetEventSlim Received = new(false);
        internal Exception? Failure;
        public void Dispose() => Received.Dispose();
    }
}
