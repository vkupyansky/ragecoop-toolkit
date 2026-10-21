using System;
using System.Collections.Generic;

namespace RageCoopAdmin.Client;

internal sealed class PassengerSeatSelector
{
    private readonly HashSet<int> _rejected = new HashSet<int>();

    internal int FindNext(int passengerCapacity, Func<int, bool> isSeatFree)
    {
        for (int seat = 0; seat < Math.Min(16, passengerCapacity); seat++)
        {
            if (!_rejected.Contains(seat) && isSeatFree(seat)) { return seat; }
        }

        return -1;
    }

    internal void Reject(int seat)
    {
        if (seat >= 0 && seat < 16) { _rejected.Add(seat); }
    }
}
