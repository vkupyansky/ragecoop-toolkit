using System;
using System.Reflection;
using CoopControl.Shared;
using Lidgren.Network;

internal static partial class Program
{
    private static void CheckAcknowledgements()
    {
        NetPeer peer = FragmentPeer();
        NetConnection connection = FragmentConnection(peer, 42101);
        MethodInfo acknowledge = EngineFragmentGuard.FindAcknowledgeTarget();
        Type channelType = acknowledge.DeclaringType!;
        object Channel() => Activator.CreateInstance(channelType, FragmentInstance, null, new object[] { connection, 64 }, null)!;
        object original = Channel();
        try
        {
            acknowledge.Invoke(original, new object[] { 10d, 0 });
            throw new InvalidOperationException("The expected original empty-window ACK defect did not reproduce.");
        }
        catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { }
        EngineFragmentGuard.Install();
        try
        {
            object channel = Channel();
            void Ack(int sequence) => acknowledge.Invoke(channel, new object[] { 10d, sequence });
            FieldInfo start = channelType.GetField("m_windowStart", FragmentInstance)!;
            FieldInfo sent = channelType.GetField("m_sendStart", FragmentInstance)!;
            FieldInfo messages = channelType.GetField("m_storedMessages", FragmentInstance)!;
            foreach (int sequence in new[] { -1, 0, 1, 1023, 1024, int.MaxValue }) { Ack(sequence); }
            Assert((int)start.GetValue(channel)! == 0 && (int)sent.GetValue(channel)! == 0,
                "real reliable ACK handling ignores empty-window, out-of-range and unsent sequences without dereferencing an empty slot");
            start.SetValue(channel, 1023); sent.SetValue(channel, 1);
            Array stored = (Array)messages.GetValue(channel)!;
            foreach (int sequence in new[] { 1023, 0 })
            {
                object item = Activator.CreateInstance(stored.GetType().GetElementType()!)!;
                NetOutgoingMessage outgoing = peer.CreateMessage(); outgoing.Write(sequence);
                typeof(NetOutgoingMessage).GetField("m_recyclingCount", FragmentInstance)!.SetValue(outgoing, 1);
                item.GetType().GetField("Message", FragmentInstance)!.SetValue(item, outgoing);
                item.GetType().GetField("SequenceNumber", FragmentInstance)!.SetValue(item, sequence);
                item.GetType().GetField("NumSent", FragmentInstance)!.SetValue(item, 1);
                item.GetType().GetField("LastSent", FragmentInstance)!.SetValue(item, 10d);
                stored.SetValue(item, sequence % 64);
            }
            Ack(1); Ack(1022);
            Assert((int)start.GetValue(channel)! == 1023, "the next unsent and old sequences cannot move a wrapped reliable window");
            Ack(0);
            Assert((int)start.GetValue(channel)! == 1023, "a real out-of-order ACK waits for its still-outstanding predecessor");
            Ack(1023);
            Assert((int)start.GetValue(channel)! == 1, "real reliable ACK handling releases both sent messages across sequence wrap-around");
            Ack(0); Ack(1023); Ack(1);
            Assert((int)start.GetValue(channel)! == 1, "late duplicate ACKs cannot reopen the now-empty window");
            Console.WriteLine("PASS original reliable ACK null dereference reproduced; guarded real ACK method preserves valid ordering and sequence wrap-around.");
        }
        finally { EngineFragmentGuard.Uninstall(); }
    }
}
