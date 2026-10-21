using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoopAdmin.Server;
using HarmonyLib;
using RageCoop.Server;
using RageCoop.Server.Scripting;

internal static class VehicleSpawnProtocolChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly API TestApi = (API)RuntimeHelpers.GetUninitializedObject(typeof(API));
    private static readonly ServerEntities TestEntities = (ServerEntities)RuntimeHelpers.GetUninitializedObject(typeof(ServerEntities));
    private static readonly List<(Client Client, int Event, object[] Args)> Sent = new();
    private static readonly Dictionary<int, ServerVehicle> Vehicles = new();
    private static readonly List<int> CreatedModels = new();
    private static int nextVehicleId = -810000;
    private static int created, deleted;
    private static bool creationFails;

    internal static void Run()
    {
        var boundary = new Harmony("friendslegacy.check.vehiclespawnserver");
        void Patch(MethodBase method, string prefix) => boundary.Patch(method,
            prefix: new HarmonyMethod(typeof(VehicleSpawnProtocolChecks), prefix));
        try
        {
            // Exercise production handlers, validation, shared 750 ms limit,
            // replacement and appearance retention. Only host/transport APIs
            // are isolated; no live server or game account is accessed.
            Patch(typeof(ServerScript).GetProperty("API", Instance)!.GetGetMethod(true)!, nameof(GetApi));
            Patch(typeof(API).GetProperty("Entities", Instance)!.GetGetMethod(true)!, nameof(GetEntities));
            Patch(typeof(API).GetMethod("SendCustomEventQueued", Instance)!, nameof(CaptureSend));
            Patch(typeof(ServerEntities).GetMethod("CreateVehicle", Instance)!, nameof(CreateVehicle));
            Patch(typeof(ServerEntities).GetMethod("GetVehicleByID", Instance)!, nameof(GetVehicle));
            Patch(typeof(ServerObject).GetMethod("Delete", Instance)!, nameof(DeleteVehicle));
            Patch(typeof(CoopAdminServer).GetMethod("ReadyClients", Instance)!, nameof(NoOtherClients));

            var server = new CoopAdminServer();
            Client client = ClientForTest(810001);
            void Dispatch(string handler, params object[] args) => typeof(CoopAdminServer).GetMethod(handler, Instance)!
                .Invoke(server, new object[] { new CustomEventReceivedArgs { Client = client, Args = args } });
            IDictionary cooldown = (IDictionary)typeof(CoopAdminServer).GetField("lastSpawn", Instance)!.GetValue(server)!;
            void ResetLimit() => cooldown.Clear();
            void Failure(int token) => Assert(Sent.Last().Event == 20774137 &&
                ReferenceEquals(Sent.Last().Client, client) && Sent.Last().Args.Length == 2 &&
                (int)Sent.Last().Args[0] == token && Sent.Last().Args[1] is string,
                "A spawn rejection is correlated to its token and sent only to the requester.");

            Dispatch("OnSpawnVehicle", 0, 444);
            Dispatch("OnSpawnVehicle", "invalid-token", 444);
            Dispatch("OnSpawnVehiclePreset", 0, "not-a-preset");
            Assert(Sent.Count == 0 && created == 0, "Spawn requests without a valid correlation token cannot create or acknowledge a vehicle.");
            Dispatch("OnSpawnVehicle", 1, 0); Failure(1);
            Dispatch("OnSpawnVehicle", 2, "444"); Failure(2);
            Dispatch("OnSpawnVehiclePreset", 3, 444); Failure(3);
            Dispatch("OnSpawnVehicle", 4, 444, "extra"); Failure(4);
            Assert(created == 0, "Wrong spawn shapes and zero models are rejected before creation.");
            Set(client, "IsReady", false);
            Dispatch("OnSpawnVehicle", 5, 444); Failure(5);
            Set(client, "IsReady", true);
            Dispatch("OnSpawnVehiclePreset", 6, "<not-appearance/>"); Failure(6);
            Assert(created == 0, "Unready players and invalid saved appearances cannot reach vehicle creation.");
            ResetLimit();
            Dispatch("OnSpawnVehicle", 7, 444);
            var firstAck = Sent.Last();
            int firstId = (int)firstAck.Args[1];
            Assert(created == 1 && CreatedModels.Single() == 444 && firstAck.Event == 20774137 && firstAck.Args.Length == 3 &&
                (int)firstAck.Args[0] == 7 && firstId != 0 && (int)firstAck.Args[2] == 444 &&
                Vehicles.ContainsKey(firstId) && ReferenceEquals(Vehicles[firstId].Owner, client),
                "Normal spawn acknowledges its token, actual server vehicle ID and model after creating the owned vehicle.");
            string appearance = EncodeAppearance(555);
            cooldown[client] = Environment.TickCount64;
            Dispatch("OnSpawnVehiclePreset", 8, appearance); Failure(8);
            Assert(created == 1, "Saved and ordinary vehicle requests retain the same server 750 ms limit.");
            ResetLimit();
            Dispatch("OnSpawnVehiclePreset", int.MinValue, appearance);
            var presetAck = Sent.Last();
            int presetId = (int)presetAck.Args[1];
            IDictionary appearances = (IDictionary)typeof(CoopAdminServer).GetField("vehicleAppearances", Instance)!.GetValue(server)!;
            Assert(created == 2 && CreatedModels.Last() == 555 && deleted == 1 && !Vehicles.ContainsKey(firstId) &&
                presetAck.Event == 20774137 && presetAck.Args.Length == 3 &&
                (int)presetAck.Args[0] == int.MinValue && (int)presetAck.Args[2] == 555 &&
                appearances.Contains(presetId),
                "Saved spawn retains appearance and replaces the prior owned car before acknowledging its full signed token.");
            ResetLimit(); creationFails = true;
            Dispatch("OnSpawnVehicle", 9, 666); Failure(9);
            Assert(deleted == 1 && Vehicles.ContainsKey(presetId) && appearances.Contains(presetId),
                "Failed server creation preserves the previous car and its saved appearance.");
            creationFails = false;
            Console.WriteLine("Correlated vehicle spawn server protocol passed; transport and game creation boundaries were isolated.");
        }
        finally
        {
            boundary.UnpatchAll(boundary.Id);
            Sent.Clear(); Vehicles.Clear(); CreatedModels.Clear(); created = 0; deleted = 0; creationFails = false;
        }
    }

    private static string EncodeAppearance(int model)
    {
        Type appearance = typeof(CoopAdminServer).Assembly.GetType("CoopControl.Shared.VehicleAppearance", true)!;
        object value = Activator.CreateInstance(appearance)!;
        appearance.GetProperty("ModelHash")!.SetValue(value, model);
        Type codec = typeof(CoopAdminServer).Assembly.GetType("CoopControl.Shared.VehicleAppearanceCodec", true)!;
        return (string)codec.GetMethod("Encode", Static)!.Invoke(null, new[] { value })!;
    }

    private static Client ClientForTest(int id)
    {
        var client = (Client)Activator.CreateInstance(typeof(Client), Instance, null, new object?[] { null }, null)!;
        var ped = (ServerPed)Activator.CreateInstance(typeof(ServerPed), Instance, null, new object?[] { null }, null)!;
        Set(ped, "ID", id); Set(client, "Player", ped); Set(client, "IsReady", true);
        return client;
    }

    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Instance)!.SetValue(target, value);
    private static bool GetApi(ref API __result) { __result = TestApi; return false; }
    private static bool GetEntities(ref ServerEntities __result) { __result = TestEntities; return false; }
    private static bool CaptureSend(List<Client> __0, int __1, object[] __2)
    {
        foreach (Client client in __0) Sent.Add((client, __1, __2));
        return false;
    }
    private static bool CreateVehicle(Client __0, GTA.Model __1, ref ServerVehicle? __result)
    {
        if (creationFails) { __result = null; return false; }
        var vehicle = (ServerVehicle)RuntimeHelpers.GetUninitializedObject(typeof(ServerVehicle));
        Set(vehicle, "ID", --nextVehicleId); Set(vehicle, "Owner", __0);
        Vehicles.Add(vehicle.ID, vehicle); CreatedModels.Add(__1.Hash); created++;
        __result = vehicle; return false;
    }
    private static bool GetVehicle(int __0, ref ServerVehicle? __result)
    { Vehicles.TryGetValue(__0, out __result); return false; }
    private static bool DeleteVehicle(ServerObject __instance)
    { Vehicles.Remove(__instance.ID); deleted++; return false; }
    private static bool NoOtherClients(ref List<Client> __result) { __result = new List<Client>(); return false; }
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
