using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoopAdmin.Server;
using RageCoop.Server;
using RageCoop.Server.Scripting;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        try
        {
            CheckPassengerSeatSelection();
            VehicleSpawnProtocolChecks.Run();
            CheckRepairOwnerAuthorization();
            var server = new CoopAdminServer();
            Client first = ClientForTest(11);
            Client second = ClientForTest(22);
            Client target = ClientForTest(33);
            Type requestType = typeof(CoopAdminServer).GetNestedType("PassengerRequest", BindingFlags.NonPublic) ?? throw new InvalidOperationException("PassengerRequest missing");
            object request1 = Request(requestType, target, 101);
            object request2 = Request(requestType, target, 202);
            ServerVehicle firstVehicle = (ServerVehicle)RuntimeHelpers.GetUninitializedObject(typeof(ServerVehicle));
            ServerVehicle replacementVehicle = (ServerVehicle)RuntimeHelpers.GetUninitializedObject(typeof(ServerVehicle));
            Set(request1, "VehicleId", 9);
            Set(request2, "VehicleId", 9);
            Set(request1, "Vehicle", firstVehicle);
            Set(request2, "Vehicle", firstVehicle);
            IDictionary requests = (IDictionary)(typeof(CoopAdminServer).GetField("passengerRequests", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server) ?? throw new InvalidOperationException("request store missing"));
            requests.Add(first, request1);
            requests.Add(second, request2);
            Assert(!Reserve(server, first, request1, -1), "driver seat is never reserved");
            Assert(!Reserve(server, first, request1, 16), "out-of-range passenger seat is rejected");
            Assert(Reserve(server, first, request1, 0), "first passenger reserves a free seat");
            Assert(!Reserve(server, second, request2, 0), "concurrent passenger cannot reserve the same seat");
            Assert(Reserve(server, second, request2, 1), "another free seat remains available");
            Set(request2, "Vehicle", replacementVehicle);
            Assert(Reserve(server, second, request2, 0), "a reused network ID does not inherit a seat reservation from the old car");
            Set(request2, "VehicleId", 10);
            Assert(Reserve(server, second, request2, 0), "same seat number on a different car is independent");
            Set(request2, "VehicleId", 9);
            Set(request2, "Vehicle", firstVehicle);
            Set(request1, "Started", Environment.TickCount64 - 12001);
            Assert(Reserve(server, second, request2, 0), "expired reservation does not block a seat");

            // These call the production handler. It must reject invalid/stale packets
            // before accessing API; this isolated script intentionally has no live API.
            DispatchCommit(server, first, 101, 9, -1);
            DispatchCommit(server, first, 999, 9, 0);
            DispatchCommit(server, first, 101, 999, 0);
            DispatchCommit(server, first, 101, 9, 0);
            Assert(true, "invalid seat, stale token, wrong entity and expired request are rejected before game access");
            Set(request1, "Started", Environment.TickCount64);
            DispatchRequest(server, "OnPassengerSeatResult", first, new object[] { 999, 2 });
            DispatchRequest(server, "OnPassengerSeatResult", first, new object[] { 101, 16 });
            Assert(GetInt(request1, "Seat") == 0, "wrong token and invalid reported seat cannot change a reservation");
            DispatchRequest(server, "OnPassengerSeatResult", first, new object[] { 101, 2 });
            Assert(GetInt(request1, "Seat") == 2, "reservation follows the passenger seat actually reported after warp");
            Set(request1, "Started", Environment.TickCount64 - 12001);
            DispatchRequest(server, "OnPassengerSeatResult", first, new object[] { 101, 1 });
            Assert(GetInt(request1, "Seat") == 2, "expired seat result cannot revive a stale reservation");
            Client third = ClientForTest(44);
            object request3 = Request(requestType, target, 303);
            Set(request3, "VehicleId", 9);
            Set(request3, "Vehicle", firstVehicle);
            requests.Add(third, request3);
            DispatchRequest(server, "OnPassengerSeatCommit", third, new object[] { 303, 9, 0 });
            DispatchCommit(server, third, 303, 9, 0, 0);
            DispatchCommit(server, third, 303, 9, 0, 17);
            Set(request3, "LastSeatAttempt", 2);
            DispatchCommit(server, third, 303, 9, 0, 1);
            DispatchCommit(server, third, 303, 9, 0, 2);
            DispatchRequest(server, "OnPassengerVehicleProbeReply", target, new object[] { 44, 303, 9 });
            Assert(true, "missing, invalid and stale seat attempts and probe replies without availability are rejected before game access");
            DispatchRequest(server, "OnPassengerTeleport", first, new object[] { 33 });
            DispatchRequest(server, "OnPassengerTeleport", first, new object[] { 33, 0 });
            DispatchRequest(server, "OnTeleportToPlayer", first, new object[] { 33 });
            DispatchRequest(server, "OnTeleportToPlayer", first, new object[] { 33, 0 });
            Assert(true, "teleport requests without nonzero correlation tokens are rejected before game access");
            DispatchRequest(server, "OnVehicleAppearance", first, new object[] { 9, "old-format" });
            DispatchRequest(server, "OnVehicleAppearance", first, new object[] { 9, "uncorrelated", 0 });
            Assert(true, "appearance requests without a nonzero edit nonce are rejected before game access");
            MethodInfo cooldown = typeof(CoopAdminServer).GetMethod("CanRun", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("cooldown method missing");
            object passengerCooldown = typeof(CoopAdminServer).GetField("lastPassengerTeleport", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server)
                ?? throw new InvalidOperationException("passenger cooldown missing");
            object coordinateCooldown = typeof(CoopAdminServer).GetField("lastTeleport", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server)
                ?? throw new InvalidOperationException("coordinate cooldown missing");
            bool passengerAccepted = (bool)(cooldown.Invoke(server, new object[] { first, passengerCooldown, 1500L }) ?? false);
            bool fallbackAccepted = (bool)(cooldown.Invoke(server, new object[] { first, coordinateCooldown, 3000L }) ?? false);
            Assert(passengerAccepted && fallbackAccepted, "no-seat coordinate fallback is not blocked by the passenger request cooldown");
            Console.WriteLine("Vehicle passenger protocol checks passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckPassengerSeatSelection()
    {
        var seats = new PassengerSeatSelector();
        Assert(seats.FindNext(3, _ => true) == 0, "passenger selection starts with the first free passenger seat");
        seats.Reject(0);
        Assert(seats.FindNext(3, _ => true) == 1, "a rejected first seat advances to the next passenger seat");
        seats.Reject(1);
        Assert(seats.FindNext(3, _ => true) == 2, "two rejected seats do not hide the third free seat");
        seats.Reject(0);
        Assert(seats.FindNext(3, seat => seat != 2) == -1, "occupied and rejected seats are not selected");
        seats.Reject(2);
        Assert(seats.FindNext(3, _ => true) == -1, "all rejected seats finish selection without retrying them");
        Assert(new PassengerSeatSelector().FindNext(0, _ => true) == -1, "a vehicle with no passenger seats cannot select the driver");
        Assert(new PassengerSeatSelector().FindNext(20, seat => seat == 16) == -1, "selection stays within the passenger protocol seat limit");
    }

    private static void CheckRepairOwnerAuthorization()
    {
        var server = new CoopAdminServer();
        Client owner = ClientForTest(71);
        Client passenger = ClientForTest(72);
        ServerVehicle vehicle = (ServerVehicle)RuntimeHelpers.GetUninitializedObject(typeof(ServerVehicle));
        Set(vehicle, "ID", 91);
        Set(vehicle, "Owner", owner);
        Type pendingType = typeof(CoopAdminServer).GetNestedType("PendingRepair", BindingFlags.NonPublic)!;
        object pending = Activator.CreateInstance(pendingType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { vehicle, owner, passenger, 4L, 999, Environment.TickCount64 }, null)!;
        IDictionary repairs = (IDictionary)typeof(CoopAdminServer).GetField("pendingRepairs",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        repairs.Add(91, pending);
        DispatchRequest(server, "OnRepairOwnerAcknowledged", passenger, new object[] { 91, 4L, 999 });
        DispatchRequest(server, "OnRepairOwnerAcknowledged", owner, new object[] { 91, 3L, 999 });
        DispatchRequest(server, "OnRepairOwnerAcknowledged", owner, new object[] { 91, 4L, 998 });
        DispatchRequest(server, "OnRepairOwnerAcknowledged", owner, new object[] { 91, 4, 999 });
        Assert(repairs.Count == 1, "passenger, stale revision and wrong model cannot acknowledge an owner repair");
        typeof(CoopAdminServer).GetMethod("RemovePendingRepairs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, new object[] { owner });
        Assert(repairs.Count == 0, "disconnect removes pending owner repairs");
    }

    private static Client ClientForTest(int id)
    {
        Client client = (Client)(Activator.CreateInstance(typeof(Client), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { null }, null) ?? throw new InvalidOperationException("client constructor failed"));
        ServerPed ped = (ServerPed)(Activator.CreateInstance(typeof(ServerPed), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { null }, null) ?? throw new InvalidOperationException("ped constructor failed"));
        Set(ped, "ID", id);
        Set(client, "Player", ped);
        Set(client, "IsReady", true);
        return client;
    }

    private static object Request(Type type, Client target, int token) =>
        Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { target, token, 303, Environment.TickCount64 }, null) ?? throw new InvalidOperationException("request constructor failed");

    private static bool Reserve(CoopAdminServer server, Client client, object request, int seat) =>
        (bool)(typeof(CoopAdminServer).GetMethod("TryReservePassengerSeat", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(server,
            new[] { client, request, seat }) ?? throw new InvalidOperationException("reservation method missing"));

    private static void DispatchCommit(CoopAdminServer server, Client client, int token, int vehicleId, int seat, int seatAttempt = 1) =>
        (typeof(CoopAdminServer).GetMethod("OnPassengerSeatCommit", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("commit handler missing"))
        .Invoke(server, new object[] { new CustomEventReceivedArgs { Client = client, Args = new object[] { token, vehicleId, seat, seatAttempt } } });

    private static void DispatchRequest(CoopAdminServer server, string handler, Client client, object[] args) =>
        (typeof(CoopAdminServer).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException(handler + " missing"))
        .Invoke(server, new object[] { new CustomEventReceivedArgs { Client = client, Args = args } });

    private static void Set(object value, string name, object content) =>
        (value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new InvalidOperationException(name + " missing"))
        .SetValue(value, content);

    private static int GetInt(object value, string name) =>
        (int)((value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(name + " missing")).GetValue(value) ??
            throw new InvalidOperationException(name + " missing value"));

    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        Console.WriteLine("PASS " + label);
    }
}
