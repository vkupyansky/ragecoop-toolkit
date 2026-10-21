using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Core;
using RageCoopAdmin.Client;
using Player = RageCoop.Client.Player;

internal static class Program
{
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly ClientAssembly = typeof(SyncedPed).Assembly;
    private static readonly Type Pool = ClientAssembly.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly Type Host = ClientAssembly.GetType("RageCoop.Client.Main", true)!;
    private static readonly Type Networking = ClientAssembly.GetType("RageCoop.Client.Networking", true)!;
    private static readonly Type PlayerList = ClientAssembly.GetType("RageCoop.Client.PlayerList", true)!;

    private static int Main()
    {
        var nativeBoundary = new Harmony("friendslegacy.tests.scaleformentry");
        MethodInfo movie = typeof(Scaleform).GetMethod("RequestMovie", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)!;
        try
        {
            // PlayerList requests a HUD movie in its type initializer. Replace
            // only that native boundary; all player and receive methods are real.
            nativeBoundary.Patch(movie, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
            Host.GetField("LocalPlayerID", StaticFlags)!.SetValue(null, 1);
            Type settingsType = ClientAssembly.GetType("RageCoop.Client.Settings", true)!;
            object settings = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("WorldPedSoftLimit", InstanceFlags)!.SetValue(settings, 10000);
            settingsType.GetProperty("WorldVehicleSoftLimit", InstanceFlags)!.SetValue(settings, 10000);
            Host.GetField("Settings", StaticFlags)!.SetValue(null, settings);
            Host.GetField("Ticked", StaticFlags)!.SetValue(null, 100ul);
            IDictionary players = (IDictionary)PlayerList.GetField("Players", StaticFlags)!.GetValue(null)!;
            IDictionary peds = Peds;
            Player owner = NewPlayer(11);
            players.Add(11, owner);
            SyncedPed player = NewPed(11);
            SetOwner(player, 11);
            peds.Add(11, player);
            SyncedPed npc = NewPed(12);
            SetOwner(npc, 11);
            peds.Add(12, npc);
            Assert(ReferenceEquals(owner.Character, npc), "the exact unpatched owner setter reproduces NPC character corruption");

            EngineNetworkGuard.Install();
            EngineNetworkGuard.Install();
            Assert(Harmony.GetPatchInfo(Pool.GetMethod("DoSync", StaticFlags)!)?.Owners.Contains("friendslegacy.ragecoop.enginenetwork") != true &&
                Harmony.GetPatchInfo(Host.GetMethod("DoQueuedActions", StaticFlags)!)?.Owners.Contains("friendslegacy.ragecoop.enginenetwork") != true,
                "the guard leaves original game phases and queued callbacks free of additional locks across model-loading yields");
            Assert(ReferenceEquals(owner.Character, player), "installation repairs an already corrupted player roster");
            SetOwner(NewPed(13), 11);
            Assert(ReferenceEquals(owner.Character, player), "the patched real owner setter keeps NPCs out of Player.Character");
            Player second = NewPlayer(22);
            players.Add(22, second);
            SyncedPed secondPed = NewPed(22);
            SetOwner(secondPed, 22);
            peds.Add(22, secondPed);
            Assert(ReferenceEquals(second.Character, secondPed), "a real player's character assignment remains functional");
            SetOwner(npc, 22);
            Assert(ReferenceEquals(owner.Character, player) && ReferenceEquals(second.Character, secondPed), "NPC ownership changes preserve both players");

            CheckExpiry();
            CheckOwnerTransferLiveness();
            CheckAtomicReceive();
            CheckConcurrentCleanup();
            CheckExceptionRelease();
            bool rejected = false;
            try { EngineNetworkGuard.ProtectPlayerCharacter(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "an unknown owner setter cannot receive a guessed IL patch");

            EngineNetworkGuard.Uninstall();
            Assert(Harmony.GetPatchInfo(typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.IsOutOfSync))!.GetGetMethod()!)?
                .Owners.Contains("friendslegacy.ragecoop.enginenetwork") != true, "unload removes this guard's actual engine patches");
            Console.WriteLine("Engine network checks passed against the installed client; GTA rendering was not exercised.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            EngineNetworkGuard.Uninstall();
            nativeBoundary.Unpatch(movie, HarmonyPatchType.Prefix, nativeBoundary.Id);
        }
    }

    private static bool SkipMovie() => false;

    private static void CheckExpiry()
    {
        SyncedPed ped = NewPed(77);
        ped.LastSynced = 1;
        Host.GetField("Ticked", StaticFlags)!.SetValue(null, 1000000ul);
        Assert(!ped.IsOutOfSync, "an entity without a received snapshot does not expire from frame count");
        ped.LastSyncedStopWatch.Restart();
        foreach (ulong ticks in new[] { 30ul, 60ul, 144ul, 1000000ul })
        {
            Host.GetField("Ticked", StaticFlags)!.SetValue(null, ticks);
            Assert(!ped.IsOutOfSync, "the actual expiry getter ignores an arbitrary frame advance of " + ticks);
        }
        Assert(!EngineNetworkGuard.HasExpired(77, true, 9999) && EngineNetworkGuard.HasExpired(77, true, 10000) &&
            !EngineNetworkGuard.HasExpired(77, false, 20000) && !EngineNetworkGuard.HasExpired(0, true, 20000),
            "expiry has one ten-second monotonic boundary and requires an initialized nonzero entity");
        Host.GetField("Ticked", StaticFlags)!.SetValue(null, 100ul);
    }

    private static void CheckAtomicReceive()
    {
        MethodInfo pedReceive = Networking.GetMethod("PedSync", StaticFlags)!;
        object vehicles = Pool.GetField("VehiclesLock", StaticFlags)!.GetValue(null)!;
        byte[] clothes = { 1, 2, 3 };
        object packet = NewPacket("PedSync", ("ID", 91), ("OwnerID", 11), ("Health", 321), ("Position", new Vector3(321f, 2f, 3f)),
            ("Clothes", clothes), ("ModelHash", -1686040670));
        PropertyInfo flags = packet.GetType().GetProperty("Flags", InstanceFlags)!;
        flags.SetValue(packet, Enum.Parse(flags.PropertyType, "IsFullSync"));
        object peds = Pool.GetField("PedsLock", StaticFlags)!.GetValue(null)!;
        long skipped = EngineNetworkGuard.SkippedSnapshots;
        Monitor.Enter(peds);
        try
        {
            Task apply = Task.Run(() => pedReceive.Invoke(null, new object[] { packet }));
            Assert(apply.Wait(1000), "a received full snapshot returns without waiting for the game's held ped monitor");
            Assert(!Peds.Contains(91) && EngineNetworkGuard.SkippedSnapshots == skipped + 1,
                "a busy snapshot is counted and cannot publish a partially initialized ped");
            Task<bool> probe = Task.Run(() =>
            {
                bool taken = Monitor.TryEnter(vehicles, 100);
                if (taken) { Monitor.Exit(vehicles); }
                return taken;
            });
            Assert(probe.Wait(1000) && probe.Result,
                "a skipped snapshot releases the vehicle monitor while the game still holds peds");
        }
        finally { Monitor.Exit(peds); }
        pedReceive.Invoke(null, new object[] { packet });
        SyncedPed actual = (SyncedPed)Peds[91]!;
        Assert((int)typeof(SyncedPed).GetProperty("Health", InstanceFlags)!.GetValue(actual)! == 321 &&
            ((Vector3)typeof(SyncedEntity).GetProperty("Position", InstanceFlags)!.GetValue(actual)!).X == 321f &&
            actual.LastSyncedStopWatch.IsRunning && actual.LastFullSynced == 100 &&
            ReferenceEquals(typeof(SyncedPed).GetProperty("Clothes", InstanceFlags)!.GetValue(actual), clothes),
            "the next full snapshot publishes movement, appearance, full revision and arrival timer atomically");

        MethodInfo vehicleReceive = Networking.GetMethod("VehicleSync", StaticFlags)!;
        object vehiclePacket = NewPacket("VehicleSync", ("ID", 92), ("OwnerID", 11), ("Position", new Vector3(99f, 1f, 2f)));
        skipped = EngineNetworkGuard.SkippedSnapshots;
        Monitor.Enter(vehicles);
        try
        {
            Task vehicleApply = Task.Run(() => vehicleReceive.Invoke(null, new object[] { vehiclePacket }));
            Assert(vehicleApply.Wait(1000) && EngineNetworkGuard.SkippedSnapshots == skipped + 1,
                "vehicle receive also skips immediately when its game monitor is busy");
        }
        finally { Monitor.Exit(vehicles); }
        vehicleReceive.Invoke(null, new object[] { vehiclePacket });
        IDictionary vehiclesById = (IDictionary)Pool.GetField("VehiclesByID", StaticFlags)!.GetValue(null)!;
        Assert(vehiclesById.Contains(92), "a subsequent vehicle snapshot is admitted after contention ends");
    }

    private static void CheckOwnerTransferLiveness()
    {
        var uninitialized = (SyncedVehicle)Activator.CreateInstance(typeof(SyncedVehicle), InstanceFlags, null, new object[] { 1776 }, null)!;
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))!.SetValue(uninitialized, 11);
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))!.SetValue(uninitialized, 22);
        Assert(!uninitialized.LastSyncedStopWatch.IsRunning && !uninitialized.IsOutOfSync,
            "ownership changes cannot initialize an entity that has never received a snapshot");

        MethodInfo receive = Networking.GetMethod("VehicleSync", StaticFlags)!;
        receive.Invoke(null, new[] { NewPacket("VehicleSync", ("ID", 1777), ("OwnerID", 11)) });
        IDictionary vehicles = (IDictionary)Pool.GetField("VehiclesByID", StaticFlags)!.GetValue(null)!;
        var vehicle = (SyncedVehicle)vehicles[1777]!;
        PropertyInfo owner = typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))!;
        Assert(vehicle.OwnerID == 11 && vehicle.LastSyncedStopWatch.IsRunning,
            "the actual remote vehicle receive initializes the arrival clock");
        owner.SetValue(vehicle, 1);
        Assert(vehicle.IsLocal, "the actual owner setter hands the remote vehicle to this client");
        var unchangedClock = Stopwatch.StartNew();
        while (unchangedClock.ElapsedMilliseconds <= EngineNetworkGuard.ExpiryMilliseconds)
        {
            Thread.Sleep(10);
        }
        owner.SetValue(vehicle, 1);
        Assert(vehicle.LastSyncedStopWatch.ElapsedMilliseconds > EngineNetworkGuard.ExpiryMilliseconds,
            "same-owner assignments do not refresh an expired arrival clock");
        owner.SetValue(vehicle, 22);
        Assert(!vehicle.IsLocal && vehicle.OwnerID == 22 && !vehicle.IsOutOfSync &&
            vehicle.LastSyncedStopWatch.ElapsedMilliseconds < 1000,
            "remote-to-local-to-remote ownership after ten seconds grants the new owner a fresh grace period");
        vehicle.LastSyncedStopWatch = unchangedClock;
        owner.SetValue(vehicle, 0);
        Assert(vehicle.LastSyncedStopWatch.ElapsedMilliseconds > EngineNetworkGuard.ExpiryMilliseconds && vehicle.IsOutOfSync,
            "an invalid zero owner cannot keep stale initialized state alive");
        vehicles.Remove(1777);
    }

    private static void CheckConcurrentCleanup()
    {
        MethodInfo receive = Networking.GetMethod("PedSync", StaticFlags)!;
        MethodInfo cleanup = Pool.GetMethod("RemoveAllFromPlayer", StaticFlags)!;
        Task producer = Task.Run(() =>
        {
            for (int index = 0; index < 600; index++)
            {
                receive.Invoke(null, new[] { NewPacket("PedSync", ("ID", 100 + index % 24), ("OwnerID", 22), ("Health", index)) });
            }
        });
        Task remover = Task.Run(() =>
        {
            for (int index = 0; index < 600; index++) { cleanup.Invoke(null, new object[] { 22 }); }
        });
        Assert(Task.WaitAll(new[] { producer, remover }, 15000), "real snapshot admission and real owner cleanup survive concurrent dictionary churn");
        receive.Invoke(null, new[] { NewPacket("PedSync", ("ID", 999), ("OwnerID", 22), ("Health", 777)) });
        Assert((int)typeof(SyncedPed).GetProperty("Health", InstanceFlags)!.GetValue(Peds[999])! == 777,
            "updates continue normally after concurrent cleanup");
    }

    private static void CheckExceptionRelease()
    {
        bool failed = false;
        MethodInfo receive = Networking.GetMethod("PedSync", StaticFlags)!;
        var fault = new Harmony("friendslegacy.tests.snapshotfault");
        try
        {
            fault.Patch(receive, prefix: new HarmonyMethod(typeof(Program), nameof(FailReceive)) { priority = Priority.Normal });
            receive.Invoke(null, new[] { NewPacket("PedSync", ("ID", 1888), ("OwnerID", 11)) });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException failure && failure.Message == "injected receive failure") { failed = true; }
        finally { fault.Unpatch(receive, HarmonyPatchType.Prefix, fault.Id); }
        Assert(failed, "the guard preserves a receive failure instead of hiding it");
        Task probe = Task.Run(() => Pool.GetMethod("RemoveAllFromPlayer", StaticFlags)!.Invoke(null, new object[] { -999 }));
        Assert(probe.Wait(5000), "the finalizer releases every monitor when the original receive method throws");
    }

    private static void FailReceive() => throw new InvalidOperationException("injected receive failure");

    private static IDictionary Peds => (IDictionary)Pool.GetField("PedsByID", StaticFlags)!.GetValue(null)!;

    private static Player NewPlayer(int id)
    {
        var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        typeof(Player).GetProperty(nameof(Player.ID))!.SetValue(player, id);
        return player;
    }

    private static SyncedPed NewPed(int id) => (SyncedPed)Activator.CreateInstance(typeof(SyncedPed), InstanceFlags, null, new object[] { id }, null)!;

    private static void SetOwner(SyncedPed ped, int owner) => typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))!.SetValue(ped, owner);

    private static object NewPacket(string name, params (string Name, object Value)[] fields)
    {
        Type type = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+" + name, true)!;
        object packet = Activator.CreateInstance(type)!;
        foreach (var field in fields) { type.GetProperty(field.Name, InstanceFlags)!.SetValue(packet, field.Value); }
        return packet;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
        Console.WriteLine("PASS " + message);
    }
}
