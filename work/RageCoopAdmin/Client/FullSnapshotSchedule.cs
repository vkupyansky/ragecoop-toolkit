namespace RageCoopAdmin.Client;

// A skipped ordinary send must not consume the next full snapshot's deadline.
internal sealed class FullSnapshotSchedule
{
    private bool _hasSent;
    private long _lastSent;

    internal bool IsDue(long now, long interval, bool requested) =>
        requested || !_hasSent || now - _lastSent >= interval;

    internal void Sent(long now)
    {
        _lastSent = now;
        _hasSent = true;
    }
}
