using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        try
        {
            if (!RuntimeEntityLookup.IsAvailable || RuntimeEntityLookup.GetVehicleByID(987654321) != null ||
                RuntimeEntityLookup.GetVehicleByHandle(987654321) != null || RuntimeEntityLookup.GetPlayerPed(987654321) != null)
            {
                throw new InvalidOperationException("The installed RageCoop lookup contract did not pass.");
            }

            CheckRemoteEntries();
            CheckPlayerSnapshots();
            CheckRecallCorrelation();

            Console.WriteLine("Installed RageCoop cached entity lookup check passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckRemoteEntries()
    {
        const int vehicleId = -1234;
        Type pool = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", throwOnError: true)!;
        IDictionary vehicles = (IDictionary)pool.GetField("VehiclesByID", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        IDictionary handles = (IDictionary)pool.GetField("VehiclesByHandle", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        IDictionary peds = (IDictionary)pool.GetField("PedsByID", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var vehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        var replacement = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        var player = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        var npc = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(vehicle, vehicleId);
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(replacement, vehicleId);
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(player, 4321);
        typeof(SyncedEntity).GetField("_ownerID", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, 4321);
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(npc, 5678);
        typeof(SyncedEntity).GetField("_ownerID", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(npc, 4321);
        try
        {
            vehicles.Add(vehicleId, vehicle);
            handles.Add(9876, vehicle);
            peds.Add(4321, player);
            peds.Add(5678, npc);
            if (!ReferenceEquals(RuntimeEntityLookup.GetVehicleByID(vehicleId), vehicle) ||
                !ReferenceEquals(RuntimeEntityLookup.GetVehicleByHandle(9876), vehicle) ||
                !ReferenceEquals(RuntimeEntityLookup.GetPlayerPed(4321), player) ||
                RuntimeEntityLookup.GetPlayerPed(5678) != null)
            {
                throw new InvalidOperationException("Remote vehicle/player lookup or NPC rejection failed.");
            }

            if (!RuntimeEntityLookup.IsCurrentVehicle(vehicle))
                throw new InvalidOperationException("A registered vehicle was rejected.");
            vehicles[vehicleId] = replacement;
            if (RuntimeEntityLookup.IsCurrentVehicle(vehicle) || !RuntimeEntityLookup.IsCurrentVehicle(replacement))
                throw new InvalidOperationException("A replaced vehicle instance remained current.");
            vehicles.Remove(vehicleId);
            if (RuntimeEntityLookup.IsCurrentVehicle(replacement))
                throw new InvalidOperationException("A removed vehicle instance remained current.");

            Console.WriteLine("PASS remote vehicle lookup, replaced/deleted instance rejection, player/NPC distinction");
        }
        finally
        {
            vehicles.Remove(vehicleId);
            handles.Remove(9876);
            peds.Remove(4321);
            peds.Remove(5678);
        }
    }

    private static void CheckPlayerSnapshots()
    {
        var roster = new Dictionary<int, Player>();
        var peer = new Player();
        typeof(Player).GetProperty("ID")!.SetValue(peer, 1234);
        using var finished = new ManualResetEvent(false);
        Exception? writerFailure = null;
        // The installed API getter performs this same Dictionary copy. Avoid
        // PlayerList's unrelated native Scaleform initializer outside GTA.
        Func<Dictionary<int, Player>> copyRoster = () => new Dictionary<int, Player>(roster);
        var writer = new Thread(() =>
        {
            try
            {
                while (!finished.WaitOne(0))
                {
                    roster[1234] = peer;
                    Thread.Yield();
                    roster.Remove(1234);
                }
            }
            catch (Exception ex) { writerFailure = ex; }
        });
        try
        {
            writer.Start();
            for (int attempt = 0; attempt < 10_000; attempt++)
            {
                if (PlayerSnapshots.TryGet(copyRoster, out Dictionary<int, Player> snapshot) &&
                    (ReferenceEquals(snapshot, roster) || snapshot.Count > 1 ||
                     (snapshot.Count == 1 && !snapshot.ContainsKey(1234))))
                {
                    throw new InvalidOperationException("Player snapshot was not an isolated valid dictionary.");
                }
            }
        }
        finally
        {
            finished.Set();
            if (writer.IsAlive && !writer.Join(5000))
                throw new InvalidOperationException("Roster writer did not terminate.");
        }
        if (writerFailure != null) throw new InvalidOperationException("Roster writer failed.", writerFailure);
        if (!PlayerSnapshots.TryGet(copyRoster, out _))
            throw new InvalidOperationException("A stable roster snapshot failed after contention stopped.");
        bool unexpectedErrorPropagated = false;
        try
        {
            PlayerSnapshots.TryGet(() => throw new ApplicationException("unrelated failure"), out _);
        }
        catch (ApplicationException) { unexpectedErrorPropagated = true; }
        if (!unexpectedErrorPropagated) throw new InvalidOperationException("An unrelated snapshot error was hidden.");
        Console.WriteLine("PASS concurrent roster copies recover; unrelated errors propagate (no live GTA API call)");
    }

    private static void CheckRecallCorrelation()
    {
        var requests = new RecallRequestCorrelation();
        const long now = 1000;
        int first = requests.Start(now);
        int current = requests.Start(now + 1);
        AssertRecall(!requests.TryAccept(new object[] { first, -42, 100, 200, 300 }, now + 2, out _),
            "a delayed response cannot complete a newer request");
        AssertRecall(!requests.TryAccept(new object[] { first, "Старый отказ" }, now + 2, out _),
            "a delayed error cannot cancel a newer request");
        AssertRecall(!requests.TryAccept(new object[] { current, 0, 100, 200, 300 }, now + 2, out _),
            "zero is never accepted as a vehicle identifier");
        AssertRecall(!requests.TryAccept(new object[] { current, -42, 1_000_001, 200, 300 }, now + 2, out _),
            "invalid coordinates do not consume the pending response");
        AssertRecall(!requests.TryAccept(new object[] { current, -42, 100, 200 }, now + 2, out _),
            "an incomplete response is rejected");
        AssertRecall(requests.TryAccept(new object[] { current, -42, -100, 200, 300 }, now + 2, out RecallReply reply) &&
            reply.VehicleId == -42 && reply.X == -100 && reply.Y == 200 && reply.Z == 300 && reply.Error == null,
            "a current response preserves a signed vehicle ID and its coordinates");
        AssertRecall(!requests.TryAccept(new object[] { current, -42, 100, 200, 300 }, now + 3, out _),
            "a duplicate response cannot start a second movement");

        int cancelled = requests.Start(now + 4);
        requests.Cancel();
        AssertRecall(!requests.TryAccept(new object[] { cancelled, -42, 100, 200, 300 }, now + 5, out _),
            "choosing another movement invalidates the outstanding recall response");

        int failure = requests.Start(now + 6);
        AssertRecall(requests.TryAccept(new object[] { failure, "Машина больше не существует" }, now + 7, out RecallReply rejected) &&
            rejected.Error == "Машина больше не существует" && !requests.TryExpire(now + Stopwatch.Frequency * 9),
            "a correlated server error completes waiting without a later false timeout");

        int expired = requests.Start(now);
        long late = now + Stopwatch.Frequency * 9;
        AssertRecall(!requests.TryAccept(new object[] { expired, -42, 100, 200, 300 }, late, out _) &&
            requests.TryExpire(late) && !requests.TryExpire(late + 1),
            "expired requests reject late replies and report timeout only once");

        typeof(RecallRequestCorrelation).GetField("_nextToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(requests, int.MaxValue);
        int negative = requests.Start(now);
        AssertRecall(negative == int.MinValue && requests.TryAccept(new object[] { negative, int.MinValue, 100, 200, 300 }, now, out _),
            "request tokens and vehicle IDs retain their full signed range");
        typeof(RecallRequestCorrelation).GetField("_nextToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(requests, -1);
        AssertRecall(requests.Start(now) != 0, "token generation skips reserved zero");
    }

    private static void AssertRecall(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS recall " + description);
    }
}
