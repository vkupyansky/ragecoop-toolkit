using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using CoopControl.Shared;
using GTA.Native;
using RageCoop.Server;
using RageCoop.Server.Scripting;

namespace CoopAdmin.Server;

public sealed partial class CoopAdminServer
{
    private const int FlipVehicleRequest = 20774124;
    private const int VehicleAppearanceRequest = 20774125;
    private const int VehicleAppearanceState = 20774126;
    private const int SpawnVehiclePresetRequest = 20774127;
    private const int PassengerTeleportRequest = 20774128;
    private const int PassengerVehicleProbe = 20774129;
    private const int PassengerVehicleProbeReply = 20774130;
    private const int PassengerTeleportState = 20774131;
    private const int PassengerSeatCommitRequest = 20774132;
    private const int PassengerSeatCommitState = 20774133;
    private const int PassengerSeatResult = 20774134;
    private const int VehicleAppearanceRemoved = 20774135;
    private const int MaxVehicleAppearanceStates = 256;
    private readonly Dictionary<Client, long> lastVehicleTools = new();
    private readonly Dictionary<Client, long> lastVehicleAppearance = new();
    private readonly Dictionary<Client, long> lastPassengerTeleport = new();
    private readonly Dictionary<Client, PassengerRequest> passengerRequests = new();
    private readonly Dictionary<int, AppearanceState> vehicleAppearances = new();
    private int nextVehicleToolsToken;
    private int appearanceRevision;

    private void RegisterVehicleToolsEvents()
    {
        API.RegisterCustomEventHandler(FlipVehicleRequest, OnFlipVehicle);
        API.RegisterCustomEventHandler(VehicleAppearanceRequest, OnVehicleAppearance);
        API.RegisterCustomEventHandler(SpawnVehiclePresetRequest, OnSpawnVehiclePreset);
        API.RegisterCustomEventHandler(PassengerTeleportRequest, OnPassengerTeleport);
        API.RegisterCustomEventHandler(PassengerVehicleProbeReply, OnPassengerVehicleProbeReply);
        API.RegisterCustomEventHandler(PassengerSeatCommitRequest, OnPassengerSeatCommit);
        API.RegisterCustomEventHandler(PassengerSeatResult, OnPassengerSeatResult);
    }

    private void CleanupVehicleTools()
    {
        lastVehicleTools.Clear();
        lastVehicleAppearance.Clear();
        lastPassengerTeleport.Clear();
        passengerRequests.Clear();
        vehicleAppearances.Clear();
    }

    private void OnVehicleToolsPlayerDisconnected(Client client)
    {
        lastVehicleTools.Remove(client);
        lastVehicleAppearance.Remove(client);
        lastPassengerTeleport.Remove(client);
        passengerRequests.Remove(client);
        foreach (Client requester in passengerRequests.Where(pair => ReferenceEquals(pair.Value.Target, client)).Select(pair => pair.Key).ToArray())
        {
            SendStatus(requester, "Игрок вышел. Перемещение отменено");
            passengerRequests.Remove(requester);
        }
    }

    private void OnVehicleToolsPlayerReady(Client client)
    {
        PruneVehicleAppearanceStates();
        foreach (KeyValuePair<int, AppearanceState> pair in vehicleAppearances)
        {
            API.SendCustomEventQueued(new List<Client> { client }, VehicleAppearanceState,
                pair.Key, pair.Value.Revision, pair.Value.Xml, 0, 0);
        }
    }

    private void OnFlipVehicle(CustomEventReceivedArgs e)
    {
        if (!IsValidRequest(e, out int vehicleId) || !TryOwnedNearbyVehicle(e.Client, vehicleId, out ServerVehicle? vehicle))
        {
            return;
        }

        if (!CanRun(e.Client, lastVehicleTools, 700))
        {
            return;
        }

        // Physics has one authority: only the vehicle owner changes its transform.
        // RageCoop then distributes the resulting position and rotation normally.
        vehicle.Owner.SendNativeCall(Hash.SET_ENTITY_ROTATION, vehicle.Handle, 0f, 0f, vehicle.Rotation.Z, 2, true);
        vehicle.Owner.SendNativeCall(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, vehicle.Handle, 5f);
        SendStatus(e.Client, "Переворот отправлен в игру");
    }

    private void OnVehicleAppearance(CustomEventReceivedArgs e)
    {
        if (!HasReadyPlayer(e) || e.Args is not { Length: 3 } || e.Args[0] is not int vehicleId ||
            e.Args[1] is not string xml || e.Args[2] is not int nonce || nonce == 0 ||
            !TryOwnedNearbyVehicle(e.Client, vehicleId, out ServerVehicle? vehicle))
        {
            return;
        }

        if (!CanRun(e.Client, lastVehicleAppearance, 350) || !TryDecodeAppearance(e.Client, xml, out VehicleAppearance? appearance))
        {
            return;
        }

        // RageCoop leaves Model unset for ordinary client-discovered traffic.
        // Ownership, distance and the bounded appearance were checked above.
        int knownModelHash = vehicle.Model.Hash;
        if (knownModelHash != 0 && appearance.ModelHash != knownModelHash)
        {
            SendStatus(e.Client, "Настройки не подходят этой машине");
            return;
        }

        BroadcastAppearance(vehicle, appearance, e.Client.Player.ID, nonce);
    }

    private void OnSpawnVehiclePreset(CustomEventReceivedArgs e)
    {
        if (!TryGetSpawnRequestToken(e, out int token)) { return; }
        if (e.Args.Length != 2 || e.Args[1] is not string xml)
        {
            SendVehicleSpawnFailure(e.Client, token, "Не удалось прочитать настройки машины. Выберите её заново.");
            return;
        }
        if (!CanRun(e.Client, lastSpawn, SpawnCooldownMs))
        {
            SendVehicleSpawnFailure(e.Client, token, "Подождите секунду перед созданием следующей машины.");
            return;
        }
        if (!TryDecodeAppearance(e.Client, xml, out VehicleAppearance? appearance, token)) { return; }
        if (!CanStoreAppearanceForSpawn(e.Client))
        {
            SendVehicleSpawnFailure(e.Client, token, "Слишком много сохранённых изменений в этой сессии. Переподключитесь.");
            return;
        }

        ServerVehicle? vehicle = TryCreateVehicleFor(e.Client, appearance.ModelHash, out string error);
        if (vehicle is null) { SendVehicleSpawnFailure(e.Client, token, error); return; }

        BroadcastAppearance(vehicle, appearance, e.Client.Player.ID);
        SendVehicleSpawnCreated(e.Client, token, vehicle, appearance.ModelHash);
    }

    private bool CanStoreAppearanceForSpawn(Client client)
    {
        PruneVehicleAppearanceStates();
        if (vehicleAppearances.Count < MaxVehicleAppearanceStates)
        {
            return true;
        }

        // Replacing this player's customized car frees its retained state.
        if (spawnedVehicles.TryGetValue(client, out ServerVehicle? previous) &&
            vehicleAppearances.TryGetValue(previous.ID, out AppearanceState? state) &&
            ReferenceEquals(state.Vehicle, previous))
        {
            return true;
        }

        return false;
    }

    private bool TryOwnedNearbyVehicle(Client client, int vehicleId, out ServerVehicle vehicle)
    {
        vehicle = API.Entities.GetVehicleByID(vehicleId)!;
        if (vehicle is null)
        {
            SendStatus(client, "Машины больше нет рядом");
            return false;
        }

        if (!ReferenceEquals(vehicle.Owner, client) ||
            !(DistanceSquared(client.Player.Position, vehicle.Position) <= 35f * 35f))
        {
            SendStatus(client, "Сядьте за руль своей машины или подойдите к ней");
            return false;
        }

        return true;
    }

    private bool TryDecodeAppearance(Client client, string xml, out VehicleAppearance appearance, int spawnToken = 0)
    {
        try
        {
            appearance = VehicleAppearanceCodec.Decode(xml);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is XmlException)
        {
            appearance = null!;
            if (spawnToken != 0)
                SendVehicleSpawnFailure(client, spawnToken, "Не удалось прочитать настройки машины. Выберите её заново.");
            else
                SendStatus(client, "Не удалось прочитать настройки машины");
            return false;
        }
    }

    private void BroadcastAppearance(ServerVehicle vehicle, VehicleAppearance appearance, int requesterId, int nonce = 0)
    {
        PruneVehicleAppearanceStates();
        if (!vehicleAppearances.ContainsKey(vehicle.ID) && vehicleAppearances.Count >= MaxVehicleAppearanceStates)
        {
            // A real bound on retained cosmetics, independent from the traffic soft limit.
            SendStatus(vehicle.Owner, "Слишком много сохранённых изменений в этой сессии. Переподключитесь");
            return;
        }

        string xml = VehicleAppearanceCodec.Encode(appearance);
        int revision = ++appearanceRevision;
        vehicleAppearances[vehicle.ID] = new AppearanceState(vehicle, revision, xml);
        API.SendCustomEventQueued(ReadyClients(), VehicleAppearanceState, vehicle.ID, revision, xml, requesterId, nonce);
    }

    private void PruneVehicleAppearanceStates()
    {
        foreach (int id in vehicleAppearances.Where(pair =>
                     !ReferenceEquals(API.Entities.GetVehicleByID(pair.Key), pair.Value.Vehicle))
                 .Select(pair => pair.Key).ToArray())
        {
            vehicleAppearances.Remove(id);
            API.SendCustomEventQueued(ReadyClients(), VehicleAppearanceRemoved, id);
        }
    }

    private void ForgetVehicleAppearance(ServerVehicle vehicle)
    {
        if (vehicleAppearances.TryGetValue(vehicle.ID, out AppearanceState? state) &&
            ReferenceEquals(state.Vehicle, vehicle))
        {
            vehicleAppearances.Remove(vehicle.ID);
            API.SendCustomEventQueued(ReadyClients(), VehicleAppearanceRemoved, vehicle.ID);
        }
    }

    private void OnPassengerTeleport(CustomEventReceivedArgs e)
    {
        if (!HasReadyPlayer(e) || e.Args is not { Length: 2 } || e.Args[0] is not int targetId ||
            e.Args[1] is not int requestNonce || requestNonce == 0 ||
            !CanRun(e.Client, lastPassengerTeleport, 1500))
        {
            return;
        }

        if (!API.GetAllClients().TryGetValue(targetId, out Client? target) || !target.IsReady || target.Player is null || ReferenceEquals(target, e.Client))
        {
            SendStatus(e.Client, "Игрок уже вышел. Выберите другого");
            return;
        }

        PrunePassengerRequests();
        int token = ++nextVehicleToolsToken;
        passengerRequests[e.Client] = new PassengerRequest(target, token, requestNonce, Environment.TickCount64);
        API.SendCustomEventQueued(new List<Client> { target }, PassengerVehicleProbe, e.Client.Player.ID, token);
    }

    private void OnPassengerVehicleProbeReply(CustomEventReceivedArgs e)
    {
        if (!HasReadyPlayer(e) || e.Args is not { Length: 4 } || e.Args[0] is not int requesterId ||
            e.Args[1] is not int token || e.Args[2] is not int vehicleId ||
            e.Args[3] is not int seatAvailable || seatAvailable is < 0 or > 1 ||
            !API.GetAllClients().TryGetValue(requesterId, out Client? requester) ||
            !passengerRequests.TryGetValue(requester, out PassengerRequest? request) ||
            !ReferenceEquals(request.Target, e.Client) || Environment.TickCount64 - request.Started > 5000)
        {
            return;
        }

        if (request.SeatProbeToken == token && request.PendingSeat >= 0 && request.PendingSeatAttempt > 0)
        {
            int seat = request.PendingSeat;
            int seatAttempt = request.PendingSeatAttempt;
            request.PendingSeat = -1;
            request.PendingSeatAttempt = 0;
            request.SeatProbeToken = 0;
            bool reserved = seatAvailable == 1 && vehicleId == request.VehicleId && request.Vehicle is not null &&
                TargetStillNearVehicle(request.Target, request.Vehicle) &&
                TryReservePassengerSeat(requester, request, seat);
            API.SendCustomEventQueued(new List<Client> { requester }, PassengerSeatCommitState,
                request.Token, request.VehicleId, seatAttempt, reserved ? seat : -1);
            return;
        }

        if (request.Token != token || request.VehicleId != 0)
        {
            return;
        }

        ServerVehicle? vehicle = seatAvailable == 1 ? API.Entities.GetVehicleByID(vehicleId) : null;
        if (seatAvailable == 0 || vehicle is null || !TargetStillNearVehicle(e.Client, vehicle))
        {
            passengerRequests.Remove(requester);
            API.SendCustomEventQueued(new List<Client> { requester }, PassengerTeleportState,
                token, e.Client.Player.ID, 0, 0, 0, 0, request.ClientNonce);
            return;
        }

        if (!TryEncodeCoordinate(vehicle.Position.X, out int x) || !TryEncodeCoordinate(vehicle.Position.Y, out int y) ||
            !TryEncodeCoordinate(vehicle.Position.Z, out int z))
        {
            passengerRequests.Remove(requester);
            SendStatus(requester, "Местоположение транспорта пока недоступно");
            return;
        }

        request.VehicleId = vehicleId;
        request.Vehicle = vehicle;
        request.Started = Environment.TickCount64;
        API.SendCustomEventQueued(new List<Client> { requester }, PassengerTeleportState,
            token, e.Client.Player.ID, vehicleId, x, y, z, request.ClientNonce);
    }

    private void OnPassengerSeatCommit(CustomEventReceivedArgs e)
    {
        if (!HasReadyPlayer(e) || e.Args is not { Length: 4 } || e.Args[0] is not int token ||
            e.Args[1] is not int vehicleId || e.Args[2] is not int seat || seat < 0 || seat > 15 ||
            e.Args[3] is not int seatAttempt || seatAttempt is < 1 or > 16 ||
            !passengerRequests.TryGetValue(e.Client, out PassengerRequest? request) || request.Token != token ||
            request.VehicleId != vehicleId || request.Vehicle is null || request.SeatProbeToken != 0 ||
            request.Seat >= 0 || seatAttempt <= request.LastSeatAttempt ||
            Environment.TickCount64 - request.Started > 12000)
        {
            return;
        }

        PrunePassengerRequests();
        request.LastSeatAttempt = seatAttempt;
        if (!TargetStillNearVehicle(request.Target, request.Vehicle))
        {
            API.SendCustomEventQueued(new List<Client> { e.Client }, PassengerSeatCommitState,
                token, vehicleId, seatAttempt, -1);
            return;
        }

        // The target checks this exact seat in its current car before it can be reserved.
        request.PendingSeat = seat;
        request.PendingSeatAttempt = seatAttempt;
        request.SeatProbeToken = ++nextVehicleToolsToken;
        request.Started = Environment.TickCount64;
        API.SendCustomEventQueued(new List<Client> { request.Target }, PassengerVehicleProbe,
            e.Client.Player.ID, request.SeatProbeToken, seat);
    }

    private bool TargetStillNearVehicle(Client target, ServerVehicle vehicle) =>
        target.IsReady && target.Player is not null &&
        ReferenceEquals(API.Entities.GetVehicleByID(vehicle.ID), vehicle) &&
        target.Player.LastVehicle?.ID == vehicle.ID &&
        DistanceSquared(target.Player.Position, vehicle.Position) <= 25f * 25f;

    private bool TryReservePassengerSeat(Client requester, PassengerRequest request, int seat)
    {
        if (seat < 0 || seat > 15 || passengerRequests.Any(pair => !ReferenceEquals(pair.Key, requester) &&
                pair.Value.VehicleId == request.VehicleId &&
                ReferenceEquals(pair.Value.Vehicle, request.Vehicle) && pair.Value.Seat == seat &&
                Environment.TickCount64 - pair.Value.Started <= 12000))
        {
            return false;
        }

        request.Seat = seat;
        request.Started = Environment.TickCount64;
        return true;
    }

    private void OnPassengerSeatResult(CustomEventReceivedArgs e)
    {
        if (HasReadyPlayer(e) && e.Args is { Length: 2 } && e.Args[0] is int token &&
            e.Args[1] is int actualSeat && actualSeat is >= -1 and <= 15 &&
            passengerRequests.TryGetValue(e.Client, out PassengerRequest? request) && request.Token == token &&
            Environment.TickCount64 - request.Started <= 12000)
        {
            if (actualSeat >= 0 && request.Seat >= 0 && request.Vehicle is not null)
            {
                request.Seat = actualSeat;
            }

            // Keep the reported seat reserved briefly until ordinary ped sync catches up.
            request.Started = Environment.TickCount64 - 10000;
        }
    }

    private void PrunePassengerRequests()
    {
        foreach (Client client in passengerRequests.Where(pair => Environment.TickCount64 - pair.Value.Started > 12000).Select(pair => pair.Key).ToArray())
        {
            passengerRequests.Remove(client);
        }
    }

    private sealed class PassengerRequest
    {
        internal PassengerRequest(Client target, int token, int clientNonce, long started)
        {
            Target = target;
            Token = token;
            ClientNonce = clientNonce;
            Started = started;
        }

        internal Client Target { get; }
        internal int Token { get; }
        internal int ClientNonce { get; }
        internal long Started { get; set; }
        internal int VehicleId { get; set; }
        internal ServerVehicle? Vehicle { get; set; }
        internal int PendingSeat { get; set; } = -1;
        internal int PendingSeatAttempt { get; set; }
        internal int LastSeatAttempt { get; set; }
        internal int SeatProbeToken { get; set; }
        internal int Seat { get; set; } = -1;
    }

    private sealed class AppearanceState
    {
        internal AppearanceState(ServerVehicle vehicle, int revision, string xml)
        { Vehicle = vehicle; Revision = revision; Xml = xml; }
        internal ServerVehicle Vehicle { get; }
        internal int Revision { get; }
        internal string Xml { get; }
    }
}
