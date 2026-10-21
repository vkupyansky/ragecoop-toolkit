using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CoopAdmin.Server;
using Lidgren.Network;

internal static partial class Program
{
    private static void CheckServerEnvelopes(Fixture fixture)
    {
        MethodInfo create = typeof(NetPeer).GetMethod("CreateIncomingMessage", All, null,
            new[] { typeof(NetIncomingMessageType), typeof(int) }, null)!;
        NetIncomingMessage Incoming(byte type, int bits)
        {
            var message = (NetIncomingMessage)Invoke(create, fixture.Transport, NetIncomingMessageType.Data, 11)!;
            message.Write(type); message.Write(123); message.Write((byte)13); message.Write(0); message.Write((byte)0);
            message.LengthBits = bits;
            typeof(NetIncomingMessage).GetField("m_senderConnection", All)!.SetValue(message, Property(fixture.First, "Connection"));
            return message;
        }
        foreach ((byte type, int bits) in new[] { ((byte)20, 0), ((byte)20, 7), ((byte)6, 8), ((byte)6, 47), ((byte)7, 47) })
        {
            NetIncomingMessage message = Incoming(type, bits);
            try { Call(fixture.Server, "ProcessMessage", message); throw new InvalidOperationException("A partial envelope was accepted."); }
            catch (InvalidDataException) { }
            Assert(message.Data == null, "an incomplete actual server envelope is rejected and recycled without reading unused capacity");
        }
        fixture.Requests.Clear();
        Task<object?> waiting = Task.Run(() => Invoke(RequestMethod(), null, fixture.Server, fixture.First, Packet("AllResourcesSent"), Channel("File"), 3000));
        WaitUntil(() => fixture.Requests.Count != 0, "the partial response check did not register its request");
        RequestEnvelope envelope = fixture.Requests.Last();
        NetIncomingMessage response = Incoming(7, 87);
        response.Data[1] = (byte)envelope.Id; response.Data[2] = (byte)(envelope.Id >> 8);
        response.Data[3] = (byte)(envelope.Id >> 16); response.Data[4] = (byte)(envelope.Id >> 24);
        response.Data[10] = Convert.ToByte(Enum.Parse(typeof(RageCoop.Core.Logger).Assembly.GetType("RageCoop.Core.FileResponse", true)!, "Loaded"));
        Call(fixture.Server, "ProcessMessage", response);
        try { waiting.GetAwaiter().GetResult(); throw new InvalidOperationException("A partial final response byte was accepted."); }
        catch (IOException error) when (error.InnerException is InvalidDataException) { }
        Assert(EngineServerGuard.PendingResponseCount(fixture.Server) == 0,
            "a response whose last byte is only seven bits cannot complete readiness and releases its pending waiter");
    }
}
