using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GTA;
using GTA.UI;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;

internal static partial class Program
{
    private static readonly List<(int Event, object[] Args)> SpawnSent = new List<(int, object[])>();
    private static readonly List<string> SpawnNotices = new List<string>();
    private static readonly Dictionary<int, int> SpawnModels = new Dictionary<int, int>();
    private static readonly HashSet<int> SpawnMissing = new HashSet<int>();
    private static readonly List<Action> SpawnQueued = new List<Action>();
    private static readonly GTA.Player SpawnPlayer = (GTA.Player)FormatterServices.GetUninitializedObject(typeof(GTA.Player));
    private static readonly Ped SpawnPed = SpawnNative<Ped>(830001);
    private static bool _spawnConnected = true, _spawnDead;
    private static bool _spawnDeferQueue;

    private static void CheckVehicleSpawnComposition()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        MethodInfo Method(string name) => script.GetMethod(name, Instance)!;
        bool Calls(string caller, string target) => PatchProcessor.GetOriginalInstructions(Method(caller))
            .Any(code => code.Calls(Method(target)));
        Assert(Calls("SendSpawn", "RequestVehicleSpawn") && Calls("SpawnVehiclePreset", "RequestVehicleSpawn"),
            "Ordinary and saved vehicle menu actions route through the same actual pending spawn helper.");
        Assert(Calls("OnStart", "RegisterVehicleSpawnEvents") && Calls("OnTick", "TickVehicleSpawn") &&
            Calls("OnTick", "CleanupVehicleSpawn") && Calls("Cleanup", "CleanupVehicleSpawn"),
            "The assembled resource registers spawn replies, advances pending creation and clears it on disconnect and cleanup.");
        object instance = FormatterServices.GetUninitializedObject(script);
        script.GetField("_active", Instance)!.SetValue(instance, true);
        script.GetField("_nextVehicleSpawnToken", Instance)!.SetValue(instance, 100);
        FieldInfo pending = script.GetField("_pendingVehicleSpawn", Instance)!;
        void Call(string name, params object?[] args) => Method(name).Invoke(instance, args);
        object? Pending() => pending.GetValue(instance);
        int Token() => (int)Pending()!.GetType().GetProperty("Token", Instance)!.GetValue(Pending())!;
        long Started() => (long)Pending()!.GetType().GetProperty("Started", Instance)!.GetValue(Pending())!;
        void Spawn(int model = 444, string? appearance = null) => Call("RequestVehicleSpawn", model, appearance);
        void Reply(params object[] args) => Call("OnVehicleSpawnResponse", new CustomEventReceivedArgs { Args = args });
        void Tick() => Call("TickVehicleSpawn", Stopwatch.GetTimestamp());
        var boundary = new Harmony("friendslegacy.check.vehiclespawnboundary");
        void Patch(MethodBase method, string prefix) => boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), prefix));
        IDictionary vehicles = (IDictionary)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!;
        const int vehicleId = -830002;
        try
        {
            // Requests, correlation, expiry and the installed pool lookup remain
            // real; the account, transport and GTA native boundaries are isolated.
            Patch(typeof(API).GetProperty("IsOnServer")!.GetGetMethod()!, nameof(SpawnConnection));
            Patch(typeof(API).GetMethod("SendCustomEvent", Static, null, new[] { typeof(int), typeof(object[]) }, null)!, nameof(CaptureSpawnSend));
            Patch(typeof(API).GetMethod("QueueAction", Static, null, new[] { typeof(Action) }, null)!, nameof(RunSpawnAction));
            foreach (MethodInfo notification in typeof(Notification).GetMethods(Static).Where(method => method.Name == "PostTicker"))
                Patch(notification, nameof(CaptureSpawnNotice));
            foreach (string name in new[] { "RequestVehicleSpawn", "TickVehicleSpawn" })
                boundary.Patch(Method(name), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateSpawnGameBoundaries)));
            Patch(Method("EnsureSpawnModelAvailable"), nameof(SpawnAvailableModel));
            Patch(Method("PreloadSpawnModel"), nameof(SkipMenuLayout));

            Spawn(); int first = Token();
            for (int repeat = 0; repeat < 8; repeat++) Spawn(555, "different-preset");
            Assert(SpawnSent.Count == 1 && SpawnSent[0].Event == 20774111 &&
                (int)SpawnSent[0].Args[0] == first && (int)SpawnSent[0].Args[1] == 444 && SpawnNotices.Count == 1,
                "Repeated actual spawn activations send one correlated request and one progress notice while pending.");
            Call("OnStatusReceived", new CustomEventReceivedArgs { Args = new object[] { "Погода изменена" } });
            Spawn();
            Assert(SpawnSent.Count == 1 && Pending() != null,
                "An unrelated real status notification cannot release pending spawn input.");
            Reply(first, vehicleId, 555); Reply(first, 0, 444); Reply(first, vehicleId);
            Tick();
            Assert(Pending() != null && SpawnNotices.All(text => !text.Contains("появилась")),
                "Malformed and wrong-model spawn replies cannot report completion.");
            Reply(first, vehicleId, 444); Tick(); Spawn();
            Assert(Pending() != null && SpawnSent.Count == 1,
                "A server-created vehicle acknowledgement keeps spawn pending while the actual game vehicle is absent.");

            var record = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
            typeof(SyncedEntity).GetProperty("ID")!.SetValue(record, vehicleId);
            vehicles.Add(vehicleId, record);
            Tick();
            Assert(Pending() != null, "A real installed pool record without its game vehicle cannot complete spawn.");
            Vehicle local = SpawnNative<Vehicle>(830003);
            typeof(SyncedVehicle).GetProperty("MainVehicle")!.SetValue(record, local);
            SpawnModels[local.Handle] = 555;
            Tick();
            Assert(Pending() != null, "A live pool ID carrying another model cannot complete spawn.");
            SpawnModels[local.Handle] = 444;
            SpawnMissing.Add(local.Handle); Tick();
            Assert(Pending() != null, "A deleted local vehicle cannot complete spawn.");
            SpawnMissing.Clear(); Tick(); Tick();
            Assert(Pending() == null && SpawnNotices.Count(text => text.Contains("появилась")) == 1,
                "Only the matching live game vehicle completes the actual request, with one completion notice.");

            Spawn(444, "encoded-appearance"); int second = Token();
            Assert(SpawnSent.Count == 2 && SpawnSent[1].Event == 20774127 &&
                (int)SpawnSent[1].Args[0] == second && (string)SpawnSent[1].Args[1] == "encoded-appearance",
                "Saved vehicle creation uses the same pending protocol immediately after actual completion, without an extra client cooldown.");
            Reply(first, "Старый отказ"); Reply(first, vehicleId, 444);
            Assert(Token() == second, "Late success and failure for an older spawn cannot release a newer pending request.");
            Reply(second, "Не удалось создать машину.");
            Assert(Pending() == null && SpawnNotices.Last().Contains("Не удалось создать"),
                "A correlated server failure releases pending spawn and shows its specific error.");
            Spawn(); int expired = Token();
            Call("TickVehicleSpawn", Started() + Stopwatch.Frequency * 16);
            int noticesAfterExpiry = SpawnNotices.Count;
            Tick(); Reply(expired, vehicleId, 444);
            Assert(Pending() == null && SpawnNotices.Count == noticesAfterExpiry && SpawnNotices.Last().Contains("сервер не ответил"),
                "A missing server response times out once and a late reply cannot revive it.");
            Spawn(); Reply(Token(), vehicleId, 444);
            Call("TickVehicleSpawn", Started() + Stopwatch.Frequency * 16);
            Assert(Pending() == null && SpawnNotices.Last().Contains("не появилась"),
                "Acknowledged creation still expires when the game vehicle does not arrive in time.");
            Spawn(); _spawnDead = true; Tick(); _spawnDead = false;
            Assert(Pending() == null && SpawnNotices.Last().Contains("возрождения"),
                "Player death cancels pending spawn with a clear message.");
            Spawn(); int disconnected = Token(); _spawnConnected = false; Tick(); _spawnConnected = true;
            Spawn(); int reconnected = Token(); Reply(disconnected, "Старый отказ");
            Assert(reconnected != disconnected && Token() == reconnected,
                "Disconnect clears waiting and a prior connection response cannot cancel the next request.");
            Call("CleanupVehicleSpawn"); Reply(reconnected, vehicleId, 444);
            Assert(Pending() == null, "Resource cleanup prevents a queued spawn reply from restoring waiting.");
            Spawn(); int queued = Token(); _spawnDeferQueue = true;
            Reply(queued, "Отказ для отменённой машины");
            Call("CleanupVehicleSpawn"); Spawn(); int afterQueued = Token();
            foreach (Action action in SpawnQueued) action();
            Assert(Token() == afterQueued,
                "A reply queued before cleanup rechecks its token on the game thread and cannot cancel a newer request.");
            Call("CleanupVehicleSpawn");
        }
        finally
        {
            vehicles.Remove(vehicleId);
            boundary.UnpatchAll(boundary.Id);
            SpawnSent.Clear(); SpawnNotices.Clear(); SpawnModels.Clear(); SpawnMissing.Clear(); SpawnQueued.Clear();
            _spawnConnected = true; _spawnDead = false; _spawnDeferQueue = false;
        }
    }

    private static bool SpawnConnection(ref bool __result) { __result = _spawnConnected; return false; }
    private static bool CaptureSpawnSend(int __0, object[] __1) { SpawnSent.Add((__0, __1)); return false; }
    private static bool RunSpawnAction(Action __0)
    { if (_spawnDeferQueue) SpawnQueued.Add(__0); else __0(); return false; }
    private static bool CaptureSpawnNotice(string __0) { SpawnNotices.Add(__0); return false; }
    private static IEnumerable<CodeInstruction> IsolateSpawnGameBoundaries(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method)
            {
                string? replacement = method.DeclaringType == typeof(Game) && method.Name == "get_Player" ? nameof(SpawnGamePlayer) :
                    method.DeclaringType == typeof(GTA.Player) && method.Name == "get_Character" ? nameof(SpawnCharacter) :
                    typeof(Entity).IsAssignableFrom(method.DeclaringType) && method.Name == "Exists" ? nameof(SpawnEntityExists) :
                    typeof(Entity).IsAssignableFrom(method.DeclaringType) && method.Name == "get_IsDead" ? nameof(SpawnEntityDead) :
                    typeof(Entity).IsAssignableFrom(method.DeclaringType) && method.Name == "get_Model" ? nameof(SpawnEntityModel) : null;
                if (replacement != null)
                { code.opcode = OpCodes.Call; code.operand = typeof(Program).GetMethod(replacement, Static)!; }
            }
            yield return code;
        }
    }
    private static GTA.Player SpawnGamePlayer() => SpawnPlayer;
    private static Ped SpawnCharacter(GTA.Player player) => SpawnPed;
    private static bool SpawnEntityExists(Entity entity) => !SpawnMissing.Contains(entity.Handle);
    private static bool SpawnEntityDead(Entity entity) => _spawnDead;
    private static Model SpawnEntityModel(Entity entity) => new Model(SpawnModels.TryGetValue(entity.Handle, out int hash) ? hash : 0);
    private static bool SpawnAvailableModel(ref bool __result) { __result = true; return false; }
    private static T SpawnNative<T>(int handle) where T : Entity =>
        (T)typeof(T).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });
}
