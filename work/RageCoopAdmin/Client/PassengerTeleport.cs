using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private const int PassengerTeleportRequest = 20774128;
    private const int PassengerVehicleProbe = 20774129;
    private const int PassengerVehicleProbeReply = 20774130;
    private const int PassengerTeleportState = 20774131;
    private const int PassengerSeatCommitRequest = 20774132;
    private const int PassengerSeatCommitState = 20774133;
    private const int PassengerSeatResult = 20774134;
    private int _nextPassengerRequestNonce = unchecked((int)Stopwatch.GetTimestamp());
    private int _passengerRequestNonce;
    private int _passengerTargetId;
    private long _passengerRequestStarted;
    private PendingPassengerTeleport? _passengerTeleport;

    private void RegisterPassengerTeleportEvents()
    {
        _eventSubscriptions.Register(PassengerVehicleProbe, OnPassengerVehicleProbe);
        _eventSubscriptions.Register(PassengerTeleportState, OnPassengerTeleportState);
        _eventSubscriptions.Register(PassengerSeatCommitState, OnPassengerSeatCommitState);
    }

    private void RequestPassengerTeleport(int targetClientId)
    {
        if (!API.IsOnServer || _passengerTargetId != 0 || _passengerTeleport != null ||
            _pendingPlayerTeleportToken != 0 ||
            _pendingTeleport != null || _pendingRecall != null)
        {
            Notification.PostTicker("~o~Дождитесь текущего перемещения.", false);
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Сначала дождитесь возрождения.", false);
            return;
        }

        _recallRequests.Cancel();

        if (player.IsInVehicle())
        {
            RequestTeleportToPlayer(targetClientId);
            return;
        }

        if (!RuntimeEntityLookup.IsAvailable)
        {
            RequestTeleportToPlayer(targetClientId);
            return;
        }

        if (!PlayerSnapshots.TryGet(out Dictionary<int, RageCoop.Client.Player> players))
        {
            Notification.PostTicker("~o~Список игроков обновляется. Повторите действие.", false);
            return;
        }
        if (targetClientId == API.LocalPlayerID || !players.ContainsKey(targetClientId))
        {
            Notification.PostTicker("~o~Игрок уже вышел. Выберите другого.", false);
            return;
        }

        CancelPlayerTeleportRequest();
        _nextPassengerRequestNonce = unchecked(_nextPassengerRequestNonce + 1);
        if (_nextPassengerRequestNonce == 0) { _nextPassengerRequestNonce = 1; }
        _passengerRequestNonce = _nextPassengerRequestNonce;
        _passengerTargetId = targetClientId;
        _passengerRequestStarted = Stopwatch.GetTimestamp();
        API.SendCustomEvent(PassengerTeleportRequest, targetClientId, _passengerRequestNonce);
        Notification.PostTicker("Проверяем транспорт и свободные места…", false);
    }

    private void OnPassengerVehicleProbe(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args is not { Length: 2 or 3 } || e.Args[0] is not int requesterId ||
            e.Args[1] is not int token)
        {
            return;
        }

        int? requestedSeat = null;
        if (e.Args.Length == 3)
        {
            if (e.Args[2] is not int seat || seat is < 0 or > 15) { return; }
            requestedSeat = seat;
        }

        Ped player = Game.Player.Character;
        Vehicle? current = player?.CurrentVehicle;
        int vehicleId = 0;
        int seatAvailable = 0;
        if (current != null && current.Exists() && !current.IsDead && current.IsDriveable)
        {
            RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(current.Handle);
            if (synced != null)
            {
                int capacity = Math.Max(0, Math.Min(16, current.PassengerCapacity));
                bool hasFreePassenger = requestedSeat is int candidate
                    ? candidate < capacity && current.IsSeatFree((VehicleSeat)candidate)
                    : Enumerable.Range(0, capacity).Any(seat => current.IsSeatFree((VehicleSeat)seat));
                vehicleId = synced.ID;
                seatAvailable = hasFreePassenger ? 1 : 0;
            }
        }

        API.SendCustomEvent(PassengerVehicleProbeReply, requesterId, token, vehicleId, seatAvailable);
    }

    private void OnPassengerTeleportState(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args is not { Length: 7 } || e.Args[0] is not int token ||
            e.Args[1] is not int targetId || e.Args[2] is not int vehicleId || e.Args[3] is not int x ||
            e.Args[4] is not int y || e.Args[5] is not int z || e.Args[6] is not int requestNonce ||
            Math.Abs((long)x) > 1_000_000 || Math.Abs((long)y) > 1_000_000 || Math.Abs((long)z) > 1_000_000)
        {
            return;
        }

        if (targetId != _passengerTargetId || requestNonce == 0 || requestNonce != _passengerRequestNonce ||
            _passengerRequestStarted == 0 ||
            (Stopwatch.GetTimestamp() - _passengerRequestStarted) / (double)Stopwatch.Frequency > 8)
        {
            return;
        }

        Ped player = Game.Player.Character;
        if (vehicleId == 0)
        {
            _passengerRequestNonce = 0;
            _passengerTargetId = 0;
            _passengerRequestStarted = 0;
            RequestTeleportToPlayer(targetId);
            return;
        }
        if (player == null || !player.Exists() || player.IsDead || player.IsInVehicle() ||
            _pendingTeleport != null || _pendingRecall != null || _passengerTeleport != null)
        {
            return;
        }

        _passengerRequestNonce = 0;
        _passengerTeleport = new PendingPassengerTeleport(token, targetId, vehicleId,
            new Vector3(x / 100f, y / 100f, z / 100f), player, Stopwatch.GetTimestamp());
        if (Vector3.Distance(player.Position, _passengerTeleport.TargetPosition) > 100f)
        {
            // RageCoop creates distant dynamic entities relative to the ped, not the camera.
            // Hold the ped briefly while they stream in; roll back if a seat cannot be confirmed.
            player.IsPositionFrozen = true;
            _passengerTeleport.MovedForStreaming = true;
            player.Position = _passengerTeleport.TargetPosition + new Vector3(0f, 0f, 3f);
        }
    }

    private void TickPassengerTeleport()
    {
        PendingPassengerTeleport? pending = _passengerTeleport;
        if (pending == null)
        {
            if (_passengerTargetId != 0 && (Stopwatch.GetTimestamp() - _passengerRequestStarted) / (double)Stopwatch.Frequency > 5)
            {
                FallbackPassengerTeleport("Не удалось проверить места. Перемещаемся рядом с игроком");
            }
            return;
        }
        if (_pendingTeleport != null || _pendingRecall != null)
        {
            CancelPassengerTeleport("Выбрано другое перемещение");
            return;
        }
        long now = Stopwatch.GetTimestamp();
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead || player.Handle != pending.Ped.Handle)
        {
            CancelPassengerTeleport("Перемещение отменено: персонаж недоступен");
            return;
        }

        if (pending.SeatAttempted)
        {
            Vehicle? current = player.CurrentVehicle;
            int actualSeat = (int)player.SeatIndex;
            if (current != null && current.Exists() &&
                RuntimeEntityLookup.GetVehicleByID(pending.VehicleId)?.MainVehicle?.Handle == current.Handle &&
                actualSeat is >= 0 and <= 15)
            {
                pending.ActualSeat = actualSeat;
                pending.Succeeded = true;
                CancelPassengerTeleport(null);
                Notification.PostTicker("~g~Вы на пассажирском месте.", false);
                return;
            }

            if (current != null && current.Exists() && player.IsInVehicle())
            {
                CancelPassengerTeleport("Перемещение отменено: вы уже в машине");
                return;
            }

            if ((now - pending.SeatAttemptStarted) / (double)Stopwatch.Frequency > 1.0)
            {
                FallbackPassengerTeleport("Не удалось сесть. Перемещаемся рядом с игроком");
            }
            return;
        }

        if (player.IsInVehicle())
        {
            CancelPassengerTeleport("Перемещение отменено: вы уже в машине");
            return;
        }

        if ((now - pending.Started) / (double)Stopwatch.Frequency > 10.0)
        {
            FallbackPassengerTeleport("Транспорт не успел загрузиться. Перемещаемся рядом с игроком");
            return;
        }

        if ((now - pending.LastProbe) / (double)Stopwatch.Frequency < 0.2) { return; }
        pending.LastProbe = now;
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pending.TargetPosition.X, pending.TargetPosition.Y, pending.TargetPosition.Z);
        if (pending.MovedForStreaming)
        {
            Function.Call(Hash.SET_FOCUS_POS_AND_VEL, pending.TargetPosition.X, pending.TargetPosition.Y, pending.TargetPosition.Z, 0f, 0f, 0f);
        }

        RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(pending.VehicleId);
        Vehicle? vehicle = synced?.MainVehicle;
        if (vehicle == null || !vehicle.Exists()) { return; }
        if (vehicle.IsDead || !vehicle.IsDriveable)
        {
            FallbackPassengerTeleport("Транспорт уничтожен. Перемещаемся рядом с игроком");
            return;
        }

        if (!TargetStillInVehicle(pending, vehicle))
        {
            FallbackPassengerTeleport("Игрок уже вышел из транспорта. Перемещаемся рядом");
            return;
        }

        if (pending.AwaitingSeat) { return; }
        int seat = pending.Seats.FindNext(vehicle.PassengerCapacity,
            candidate => vehicle.IsSeatFree((VehicleSeat)candidate));
        if (seat >= 0)
        {
            pending.AwaitingSeat = true;
            pending.RequestedSeat = seat;
            pending.PendingSeatAttempt = ++pending.NextSeatAttempt;
            API.SendCustomEvent(PassengerSeatCommitRequest, pending.Token, pending.VehicleId, seat,
                pending.PendingSeatAttempt);
            return;
        }

        FallbackPassengerTeleport("Свободных пассажирских мест нет. Перемещаемся рядом");
    }

    private bool TargetStillInVehicle(PendingPassengerTeleport pending, Vehicle vehicle)
    {
        if (!PlayerSnapshots.TryGet(out Dictionary<int, RageCoop.Client.Player> players)) { return true; }
        if (!players.TryGetValue(pending.TargetId, out RageCoop.Client.Player? target)) { return false; }
        Ped? character = RuntimeEntityLookup.GetPlayerPed(target.ID)?.MainPed;
        // The dynamic vehicle can arrive before its remote ped; the server just probed
        // the target's real current vehicle. Wait for the ped when it is still streaming.
        if (character == null || !character.Exists()) { return true; }
        return character.CurrentVehicle?.Handle == vehicle.Handle;
    }

    private void OnPassengerSeatCommitState(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args is not { Length: 4 } || e.Args[0] is not int token ||
            e.Args[1] is not int vehicleId || e.Args[2] is not int seatAttempt || e.Args[3] is not int seat)
        {
            return;
        }

        PendingPassengerTeleport? pending = _passengerTeleport;
        if (pending == null || token != pending.Token || vehicleId != pending.VehicleId ||
            !pending.AwaitingSeat || seatAttempt == 0 || seatAttempt != pending.PendingSeatAttempt ||
            (seat != -1 && seat != pending.RequestedSeat))
        {
            return;
        }

        pending.PendingSeatAttempt = 0;
        pending.AwaitingSeat = false;
        if (seat == -1)
        {
            pending.Seats.Reject(pending.RequestedSeat);
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead || player.Handle != pending.Ped.Handle || player.IsInVehicle() ||
            RuntimeEntityLookup.GetVehicleByID(vehicleId) is not RageCoop.Client.SyncedVehicle synced)
        {
            CancelPassengerTeleport("Перемещение отменено");
            return;
        }

        Vehicle vehicle = synced.MainVehicle;
        if (vehicle == null || !vehicle.Exists() || vehicle.IsDead || seat >= vehicle.PassengerCapacity ||
            !vehicle.IsSeatFree((VehicleSeat)seat) || !TargetStillInVehicle(pending, vehicle))
        {
            FallbackPassengerTeleport("Место уже занято. Перемещаемся рядом с игроком");
            return;
        }

        // The final local occupancy check is immediately next to the warp. Driver -1
        // and occupied passenger seats are never passed to SetIntoVehicle.
        player.SetIntoVehicle(vehicle, (VehicleSeat)seat);
        pending.SeatAttempted = true;
        pending.SeatAttemptStarted = Stopwatch.GetTimestamp();
    }

    private void CancelPassengerTeleport(string? message)
    {
        PendingPassengerTeleport? pending = _passengerTeleport;
        _passengerTeleport = null;
        _passengerRequestNonce = 0;
        _passengerTargetId = 0;
        _passengerRequestStarted = 0;
        if (pending != null)
        {
            try
            {
                if (pending.Ped.Exists())
                {
                    try
                    {
                        if (!pending.Succeeded && pending.MovedForStreaming && !pending.Ped.IsDead &&
                            !pending.Ped.IsInVehicle())
                        {
                            pending.Ped.Position = pending.OriginalPosition;
                            pending.Ped.Velocity = Vector3.Zero;
                        }
                    }
                    finally { pending.Ped.IsPositionFrozen = pending.WasFrozen; }
                }
            }
            finally
            {
                if (pending.MovedForStreaming) { Function.Call(Hash.CLEAR_FOCUS); }
            }
            // A disconnect can race the connected check and make SendCustomEvent
            // throw. Release local streaming state before reporting the result.
            if (API.IsOnServer)
            {
                API.SendCustomEvent(PassengerSeatResult, pending.Token, pending.Succeeded ? pending.ActualSeat : -1);
            }
        }

        if (message != null) { API.LocalChatMessage("Совместная игра", message); }
    }

    private void FallbackPassengerTeleport(string message)
    {
        Ped player = Game.Player.Character;
        if (player != null && player.Exists() && player.IsInVehicle())
        {
            CancelPassengerTeleport("Перемещение отменено: вы уже в машине");
            return;
        }

        int targetId = _passengerTeleport?.TargetId ?? _passengerTargetId;
        CancelPassengerTeleport(message);
        if (targetId != 0 && API.IsOnServer)
        {
            // The server validates the target again. A transient roster-copy failure
            // must not turn an otherwise usable fallback into a silent cancellation.
            RequestTeleportToPlayer(targetId);
        }
    }

    private sealed class PendingPassengerTeleport
    {
        internal PendingPassengerTeleport(int token, int targetId, int vehicleId, Vector3 targetPosition, Ped ped, long started)
        {
            Token = token; TargetId = targetId; VehicleId = vehicleId; TargetPosition = targetPosition; Ped = ped;
            Started = started; OriginalPosition = ped.Position; WasFrozen = ped.IsPositionFrozen;
        }

        internal int Token { get; }
        internal int TargetId { get; }
        internal int VehicleId { get; }
        internal Vector3 TargetPosition { get; }
        internal Ped Ped { get; }
        internal Vector3 OriginalPosition { get; }
        internal bool WasFrozen { get; }
        internal long Started { get; }
        internal long LastProbe { get; set; }
        internal bool MovedForStreaming { get; set; }
        internal bool AwaitingSeat { get; set; }
        internal int RequestedSeat { get; set; } = -1;
        internal int NextSeatAttempt { get; set; }
        internal int PendingSeatAttempt { get; set; }
        internal PassengerSeatSelector Seats { get; } = new PassengerSeatSelector();
        internal bool SeatAttempted { get; set; }
        internal int ActualSeat { get; set; } = -1;
        internal long SeatAttemptStarted { get; set; }
        internal bool Succeeded { get; set; }
    }
}
