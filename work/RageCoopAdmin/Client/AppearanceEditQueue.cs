using System;
using System.Collections.Generic;
using System.Linq;

namespace RageCoopAdmin.Client;

// Local changes are immediate; complete snapshots are coalesced at a bounded
// rate. A per-edit nonce keeps old server echoes from undoing selection.
internal sealed class AppearanceEditQueue
{
    internal const int IntervalMilliseconds = 450;
    private const int MaximumVehicles = 256;
    private readonly Dictionary<int, PendingAppearanceEdit> _pending = new Dictionary<int, PendingAppearanceEdit>();
    private long _lastSent = long.MinValue;
    private int _nextNonce;

    internal int Stage(int vehicleId, string xml)
    {
        if (vehicleId == 0 || string.IsNullOrEmpty(xml)) { throw new ArgumentException("A vehicle and appearance are required."); }
        if (!_pending.ContainsKey(vehicleId) && _pending.Count >= MaximumVehicles)
        {
            throw new InvalidOperationException("Too many pending vehicle changes.");
        }

        int nonce = unchecked(++_nextNonce);
        if (nonce == 0) { nonce = unchecked(++_nextNonce); }
        _pending[vehicleId] = new PendingAppearanceEdit(vehicleId, xml, nonce);
        return nonce;
    }

    internal bool TryTake(long nowMilliseconds, out PendingAppearanceEdit edit)
    {
        edit = null!;
        if (_pending.Count == 0 || (_lastSent != long.MinValue && nowMilliseconds - _lastSent < IntervalMilliseconds)) { return false; }
        PendingAppearanceEdit? pending = _pending.Values.FirstOrDefault(value => nowMilliseconds >= value.NextAttemptMilliseconds);
        if (pending == null) { return false; }
        edit = pending;
        if (edit.Attempts >= 4)
        {
            edit.Expired = true;
            _pending.Remove(edit.VehicleId);
        }
        else
        {
            edit.Attempts++;
            edit.NextAttemptMilliseconds = nowMilliseconds + 1500;
        }
        _lastSent = nowMilliseconds;
        return true;
    }

    internal bool IsStaleEcho(int vehicleId, int nonce) =>
        _pending.TryGetValue(vehicleId, out PendingAppearanceEdit? latest) && latest.Nonce != nonce;

    internal void Acknowledge(int vehicleId, int nonce)
    {
        if (_pending.TryGetValue(vehicleId, out PendingAppearanceEdit? edit) && edit.Nonce == nonce)
        {
            _pending.Remove(vehicleId);
        }
    }

    internal void Forget(int vehicleId)
    {
        _pending.Remove(vehicleId);
    }

    internal void Clear()
    {
        _pending.Clear();
        _lastSent = long.MinValue;
    }
}

internal sealed class PendingAppearanceEdit
{
    internal PendingAppearanceEdit(int vehicleId, string xml, int nonce)
    { VehicleId = vehicleId; Xml = xml; Nonce = nonce; }
    internal int VehicleId { get; }
    internal string Xml { get; }
    internal int Nonce { get; }
    internal int Attempts { get; set; }
    internal long NextAttemptMilliseconds { get; set; }
    internal bool Expired { get; set; }
}
