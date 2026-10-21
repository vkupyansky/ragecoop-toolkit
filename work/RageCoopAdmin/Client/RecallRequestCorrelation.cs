using System;
using System.Diagnostics;

namespace RageCoopAdmin.Client;

// Accessed only by the game-thread request and queued response handlers.
internal sealed class RecallRequestCorrelation
{
    private int _nextToken;
    private int _pendingToken;
    private long _started;

    internal int Start(long now)
    {
        _nextToken = unchecked(_nextToken + 1);
        if (_nextToken == 0) _nextToken = 1;
        _pendingToken = _nextToken;
        _started = now;
        return _pendingToken;
    }

    internal void Cancel() => _pendingToken = 0;

    internal bool TryExpire(long now)
    {
        if (_pendingToken == 0 || !Expired(now)) return false;
        Cancel();
        return true;
    }

    internal bool TryAccept(object[] args, long now, out RecallReply reply)
    {
        reply = default;
        if (_pendingToken == 0 || Expired(now) || args.Length < 1 ||
            args[0] is not int token || token == 0 || token != _pendingToken) return false;

        if (args.Length == 2 && args[1] is string error &&
            !string.IsNullOrWhiteSpace(error) && error.Length <= 160)
        {
            reply = new RecallReply(0, 0, 0, 0, error);
        }
        else if (args.Length == 5 && args[1] is int vehicleId && vehicleId != 0 &&
                 args[2] is int x && args[3] is int y && args[4] is int z &&
                 ValidCoordinate(x) && ValidCoordinate(y) && ValidCoordinate(z))
        {
            reply = new RecallReply(vehicleId, x, y, z, null);
        }
        else
        {
            return false;
        }

        Cancel();
        return true;
    }

    private bool Expired(long now) => (now - _started) / (double)Stopwatch.Frequency > 8.0;
    private static bool ValidCoordinate(int value) => value >= -1_000_000 && value <= 1_000_000;
}

internal readonly struct RecallReply
{
    internal RecallReply(int vehicleId, int x, int y, int z, string? error)
    { VehicleId = vehicleId; X = x; Y = y; Z = z; Error = error; }

    internal int VehicleId { get; }
    internal int X { get; }
    internal int Y { get; }
    internal int Z { get; }
    internal string? Error { get; }
}
