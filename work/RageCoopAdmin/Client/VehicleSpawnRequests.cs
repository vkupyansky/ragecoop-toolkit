using System;
using System.Diagnostics;
using GTA;
using GTA.UI;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private const int VehicleSpawnResponse = 20774137;
    private const double VehicleSpawnTimeoutSeconds = 15.0;
    private int _nextVehicleSpawnToken = unchecked((int)Stopwatch.GetTimestamp());
    private PendingVehicleSpawn? _pendingVehicleSpawn;

    private void RegisterVehicleSpawnEvents() =>
        _eventSubscriptions.Register(VehicleSpawnResponse, OnVehicleSpawnResponse);

    private void RequestVehicleSpawn(int modelHash, string? encodedAppearance)
    {
        // A held or repeated menu activation must not replace a request that is
        // still waiting for its specific game vehicle to appear.
        if (_pendingVehicleSpawn != null || _stopping || !API.IsOnServer) { return; }
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Машина:~s~ дождитесь возрождения и повторите.", false);
            return;
        }
        if (!RuntimeEntityLookup.IsAvailable)
        {
            Notification.PostTicker("~o~Не удалось проверить появление машины. Переподключитесь к игре.", false);
            return;
        }
        if (!EnsureSpawnModelAvailable(modelHash)) { return; }

        _nextVehicleSpawnToken = unchecked(_nextVehicleSpawnToken + 1);
        if (_nextVehicleSpawnToken == 0) { _nextVehicleSpawnToken = 1; }
        var pending = new PendingVehicleSpawn(_nextVehicleSpawnToken, modelHash, Stopwatch.GetTimestamp());
        _pendingVehicleSpawn = pending;
        PreloadSpawnModel(modelHash);
        if (encodedAppearance == null)
            API.SendCustomEvent(SpawnRequest, pending.Token, modelHash);
        else
            API.SendCustomEvent(SpawnVehiclePresetRequest, pending.Token, encodedAppearance);
        Notification.PostTicker("~o~Машина:~s~ создаём…", false);
    }

    private void OnVehicleSpawnResponse(CustomEventReceivedArgs e)
    {
        if (_stopping || e.Args.Length < 2 || e.Args[0] is not int token || token == 0) { return; }
        int vehicleId = 0;
        int modelHash = 0;
        string? error = null;
        if (e.Args.Length == 2 && e.Args[1] is string message &&
            !string.IsNullOrWhiteSpace(message) && message.Length <= 160)
            error = message.Replace("~", string.Empty);
        else if (e.Args.Length == 3 && e.Args[1] is int id && id != 0 &&
                 e.Args[2] is int hash && hash != 0)
        { vehicleId = id; modelHash = hash; }
        else
            return;

        API.QueueAction(() =>
        {
            PendingVehicleSpawn? pending = _pendingVehicleSpawn;
            if (!_active || _stopping || !API.IsOnServer || pending == null || pending.Token != token ||
                SpawnExpired(pending, Stopwatch.GetTimestamp())) { return; }
            if (error != null)
            {
                _pendingVehicleSpawn = null;
                Notification.PostTicker("~o~Машина:~s~ " + error, false);
            }
            else if (pending.ModelHash == modelHash && pending.VehicleId == 0)
            {
                // This confirms server creation only. The next ticks still
                // require this ID and model in the live local game pool.
                pending.VehicleId = vehicleId;
            }
        });
    }

    private void TickVehicleSpawn(long now)
    {
        PendingVehicleSpawn? pending = _pendingVehicleSpawn;
        if (pending == null) { return; }
        if (_stopping || !API.IsOnServer)
        {
            CleanupVehicleSpawn();
            return;
        }
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            _pendingVehicleSpawn = null;
            Notification.PostTicker("~o~Создание машины отменено.~s~ Дождитесь возрождения и повторите.", false);
            return;
        }
        if (SpawnExpired(pending, now))
        {
            _pendingVehicleSpawn = null;
            Notification.PostTicker(pending.VehicleId == 0
                ? "~o~Машина:~s~ сервер не ответил. Повторите попытку."
                : "~o~Машина не появилась.~s~ Проверьте установленную модель и повторите попытку.", false);
            return;
        }
        if (pending.VehicleId == 0) { return; }
        SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(pending.VehicleId);
        Vehicle? vehicle = synced?.MainVehicle;
        if (synced == null || synced.ID != pending.VehicleId || !RuntimeEntityLookup.IsCurrentVehicle(synced) ||
            vehicle == null || !vehicle.Exists() || vehicle.Model.Hash != pending.ModelHash) { return; }
        _pendingVehicleSpawn = null;
        Notification.PostTicker("~g~Машина появилась рядом.", false);
    }

    private void CleanupVehicleSpawn() => _pendingVehicleSpawn = null;

    private static bool SpawnExpired(PendingVehicleSpawn pending, long now) =>
        (now - pending.Started) / (double)Stopwatch.Frequency > VehicleSpawnTimeoutSeconds;

    private sealed class PendingVehicleSpawn
    {
        internal PendingVehicleSpawn(int token, int modelHash, long started)
        { Token = token; ModelHash = modelHash; Started = started; }
        internal int Token { get; }
        internal int ModelHash { get; }
        internal long Started { get; }
        internal int VehicleId { get; set; }
    }
}
