using System;
using System.Collections.Generic;
using System.Linq;
using RageCoop.Server;
using RageCoop.Server.Scripting;

namespace CoopAdmin.Server;

public sealed partial class CoopAdminServer
{
    private const int RepairOwnerRequestEvent = 20774140;
    private const int RepairOwnerAckEvent = 20774141;
    private readonly Dictionary<int, PendingRepair> pendingRepairs = new();
    private long lastRepairPrune;

    private void OnRepairOwnerAcknowledged(CustomEventReceivedArgs e)
    {
        if (!HasReadyPlayer(e) || e.Args is not { Length: 3 } ||
            e.Args[0] is not int id || e.Args[1] is not long revision ||
            e.Args[2] is not int modelHash ||
            !pendingRepairs.TryGetValue(id, out PendingRepair? pending) ||
            pending.Revision != revision || pending.ModelHash != modelHash ||
            !ReferenceEquals(pending.Owner, e.Client) ||
            !ReferenceEquals(pending.Vehicle.Owner, e.Client) ||
            !ReferenceEquals(API.Entities.GetVehicleByID(id), pending.Vehicle) ||
            Environment.TickCount64 - pending.Started > 5000)
        {
            return;
        }

        pendingRepairs.Remove(id);
        API.SendCustomEventQueued(ReadyClients(), RepairStateEvent, id, revision, modelHash);
        if (pending.Requester.IsReady) { SendStatus(pending.Requester, "Машина отремонтирована"); }
    }

    private void OnRepairPlayerUpdate(object? sender, Client client)
    {
        long now = Environment.TickCount64;
        if (now - lastRepairPrune < 1000) { return; }
        lastRepairPrune = now;
        PrunePendingRepairs();
    }

    private void PrunePendingRepairs()
    {
        foreach (KeyValuePair<int, PendingRepair> pair in pendingRepairs.ToArray())
        {
            PendingRepair pending = pair.Value;
            if (Environment.TickCount64 - pending.Started <= 5000 &&
                pending.Owner.IsReady && pending.Requester.IsReady &&
                ReferenceEquals(API.Entities.GetVehicleByID(pair.Key), pending.Vehicle) &&
                ReferenceEquals(pending.Vehicle.Owner, pending.Owner))
            {
                continue;
            }

            pendingRepairs.Remove(pair.Key);
            if (pending.Requester.IsReady)
            {
                SendStatus(pending.Requester, "Не получили подтверждение ремонта. Повторите действие");
            }
        }
    }

    private void RemovePendingRepairs(Client client)
    {
        foreach (int id in pendingRepairs.Where(pair =>
                     ReferenceEquals(pair.Value.Owner, client) || ReferenceEquals(pair.Value.Requester, client))
                 .Select(pair => pair.Key).ToArray())
        {
            pendingRepairs.Remove(id);
        }
    }

    private sealed class PendingRepair
    {
        internal PendingRepair(ServerVehicle vehicle, Client owner, Client requester, long revision,
            int modelHash, long started)
        {
            Vehicle = vehicle;
            Owner = owner;
            Requester = requester;
            Revision = revision;
            ModelHash = modelHash;
            Started = started;
        }

        internal ServerVehicle Vehicle { get; }
        internal Client Owner { get; }
        internal Client Requester { get; }
        internal long Revision { get; }
        internal int ModelHash { get; }
        internal long Started { get; }
    }
}
