using System;
using System.Collections.Generic;
using System.Diagnostics;
using GTA;
using GTA.Native;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

// The confirmed repair event can arrive before RageCoop creates the local
// vehicle. Keep its network ID briefly and apply the repair once it is loaded.
internal static class RepairSync
{
    private const int RepairStateEvent = 20774136;
    private const int RepairOwnerRequestEvent = 20774140;
    private const int RepairOwnerAckEvent = 20774141;
    private static readonly RepairSyncQueue Pending = new(Stopwatch.Frequency);
    private static readonly RepairSyncQueue PendingOwners = new(Stopwatch.Frequency, 5);
    private static bool _registered;
    private static bool _active;
    private static Action<string>? _warning;
    private static long _lastWarningTick;

    internal static void Start(Action<string> warning)
    {
        if (!_registered)
        {
            API.RegisterCustomEventHandler(RepairStateEvent, OnRepairReceived);
            API.RegisterCustomEventHandler(RepairOwnerRequestEvent, OnOwnerRepairReceived);
            _registered = true;
        }

        Pending.Clear();
        PendingOwners.Clear();
        _warning = warning;
        _lastWarningTick = 0;
        _active = true;
        if (!VehicleRepairCache.IsAvailable)
        {
            warning("Coop Control: repair cache contract does not match the installed RageCoop client.");
        }
    }

    internal static void Stop()
    {
        _active = false;
        Pending.Clear();
        PendingOwners.Clear();
        _warning = null;
    }

    internal static void Tick()
    {
        if (!_active) { return; }
        if (!API.IsOnServer)
        {
            Pending.Clear();
            PendingOwners.Clear();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        try
        {
            PendingOwners.Process(now, ApplyOwnedIfLoaded);
            Pending.Process(now, ApplyIfLoaded);
        }
        catch (Exception ex)
        {
            if (_lastWarningTick == 0 || now - _lastWarningTick >= Stopwatch.Frequency * 5)
            {
                _lastWarningTick = now;
                _warning?.Invoke("Coop Control: repair retry failed: " + ex.GetType().Name);
            }
        }
    }

    private static void OnOwnerRepairReceived(CustomEventReceivedArgs e)
    {
        if (_active && API.IsOnServer) { PendingOwners.TryReceive(e.Args, Stopwatch.GetTimestamp()); }
    }

    private static bool ApplyOwnedIfLoaded(int id, int modelHash)
    {
        SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(id);
        if (synced?.IsLocal != true || !TryGetLoadedVehicle(synced, modelHash, out Vehicle? vehicle) ||
            !VehicleRepairCache.IsAvailable)
        {
            return false;
        }

        vehicle!.Repair();
        Function.Call(Hash.SET_VEHICLE_DEFORMATION_FIXED, vehicle.Handle);
        VehicleRepairCache.Reset(synced, publish: true);
        long revision = PendingOwners.GetRevision(id);
        API.SendCustomEvent(RepairOwnerAckEvent, id, revision, modelHash);
        return true;
    }

    private static void OnRepairReceived(CustomEventReceivedArgs e)
    {
        if (_active && API.IsOnServer)
        {
            Pending.TryReceive(e.Args, Stopwatch.GetTimestamp());
        }
    }

    private static bool ApplyIfLoaded(int id, int modelHash)
    {
        SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(id);
        if (synced == null || !TryGetLoadedVehicle(synced, modelHash, out Vehicle? vehicle) ||
            !VehicleRepairCache.IsAvailable) { return false; }
        // The owner already repaired and queued a complete ordinary vehicle snapshot.
        if (synced.IsLocal) { return true; }

        vehicle!.Repair();
        Function.Call(Hash.SET_VEHICLE_DEFORMATION_FIXED, vehicle.Handle);
        VehicleRepairCache.Reset(synced, publish: false);
        return true;
    }

    private static bool TryGetLoadedVehicle(SyncedVehicle synced, int modelHash, out Vehicle? vehicle)
    {
        vehicle = synced.MainVehicle;
        return RuntimeEntityLookup.IsCurrentVehicle(synced) && vehicle != null && vehicle.Exists() &&
            vehicle.Model.Hash == modelHash;
    }
}

internal sealed class RepairSyncQueue
{
    private const int MaxEntries = 128;
    private const int MaxAttemptsPerTick = 8;
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly long _pendingLifetime;
    private readonly long _retryInterval;

    internal RepairSyncQueue(long ticksPerSecond, int lifetimeSeconds = 15)
    {
        if (ticksPerSecond <= 0) { throw new ArgumentOutOfRangeException(nameof(ticksPerSecond)); }
        if (lifetimeSeconds <= 0) { throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds)); }
        _pendingLifetime = ticksPerSecond * lifetimeSeconds;
        _retryInterval = Math.Max(1, ticksPerSecond / 4);
    }

    internal int Count => _entries.Count;
    internal long GetRevision(int id) => _entries.TryGetValue(id, out Entry? entry) ? entry.Revision : 0;
    internal int PendingCount
    {
        get
        {
            int count = 0;
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Pending) { count++; }
            }
            return count;
        }
    }

    internal bool TryReceive(object[]? args, long now)
    {
        if (args is not { Length: 3 } || args[0] is not int id || id == 0 ||
            args[1] is not long revision || revision <= 0 ||
            args[2] is not int modelHash || modelHash == 0)
        {
            return false;
        }

        Prune(now);
        if (_entries.TryGetValue(id, out Entry? previous) && revision <= previous.Revision)
        {
            return false;
        }

        if (previous == null && _entries.Count == MaxEntries) { EvictOldest(); }
        _entries[id] = new Entry(revision, modelHash, now);
        return true;
    }

    internal int Process(long now, Func<int, int, bool> apply)
    {
        if (apply == null) { throw new ArgumentNullException(nameof(apply)); }
        Prune(now);
        int applied = 0;
        int attempted = 0;
        foreach (KeyValuePair<int, Entry> pair in _entries)
        {
            Entry entry = pair.Value;
            if (!entry.Pending ||
                (entry.HasAttempted && now - entry.LastAttempt < _retryInterval))
            {
                continue;
            }

            if (attempted == MaxAttemptsPerTick) { break; }
            attempted++;
            entry.HasAttempted = true;
            entry.LastAttempt = now;
            if (!apply(pair.Key, entry.ModelHash)) { continue; }
            entry.Pending = false;
            entry.SeenAt = now;
            applied++;
        }

        return applied;
    }

    internal void Clear() => _entries.Clear();

    private void Prune(long now)
    {
        foreach (Entry entry in _entries.Values)
        {
            if (!entry.Pending || now - entry.SeenAt < _pendingLifetime) { continue; }
            // Retain the revision as a bounded high-water mark after expiry.
            entry.Pending = false;
            entry.SeenAt = now;
        }
    }

    private void EvictOldest()
    {
        int selected = 0;
        Entry? oldest = null;
        foreach (KeyValuePair<int, Entry> pair in _entries)
        {
            if (oldest != null &&
                ((!oldest.Pending && pair.Value.Pending) ||
                 (oldest.Pending == pair.Value.Pending && pair.Value.SeenAt >= oldest.SeenAt)))
            {
                continue;
            }

            selected = pair.Key;
            oldest = pair.Value;
        }

        _entries.Remove(selected);
    }

    private sealed class Entry
    {
        internal Entry(long revision, int modelHash, long now)
        {
            Revision = revision;
            ModelHash = modelHash;
            SeenAt = now;
        }

        internal long Revision { get; }
        internal int ModelHash { get; }
        internal long SeenAt { get; set; }
        internal long LastAttempt { get; set; }
        internal bool HasAttempted { get; set; }
        internal bool Pending { get; set; } = true;
    }
}
