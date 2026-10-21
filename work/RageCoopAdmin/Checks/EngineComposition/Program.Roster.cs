using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using Player = RageCoop.Client.Player;

internal static partial class Program
{
    private static readonly Type Roster = Engine.GetType("RageCoop.Client.PlayerList", true)!;
    private static readonly List<int> RosterCreatedBlips = new List<int>();
    private static readonly HashSet<int> RosterDeleted = new HashSet<int>();
    private static bool _rosterConnected = true, _rosterReplaceBeforeQueue, _rosterClearAtLimit;
    private static Action? _rosterFirstSlot;
    private static Action? _rosterDuringCreate;
    private static ManualResetEventSlim? _rosterCreating, _rosterReleaseCreate;
    private static object _rosterGate = null!;
    private static int _rosterSlots, _rosterExplosions;
    private static int _rosterCopies, _rosterCopyThread, _rosterErrors;

    private static void CheckRosterComposition()
    {
        var guard = new Guard("Roster");
        var gameplay = new Guard("Gameplay");
        var network = new Guard("Network");
        var boundary = new Harmony("friendslegacy.check.rosterboundary");
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        FieldInfo players = Roster.GetField("Players", Static)!;
        FieldInfo logger = host.GetField("Logger", Static)!, settings = host.GetField("Settings", Static)!;
        FieldInfo scaleform = Roster.GetField("_mainScaleform", Static)!;
        object? previousPlayers = players.GetValue(null), previousLogger = logger.GetValue(null), previousSettings = settings.GetValue(null), previousScaleform = scaleform.GetValue(null);
        bool queueResponsive = true;
        MethodInfo playerUpdate = Roster.GetMethod("UpdatePlayer", Static)!;
        MethodInfo pedSync = Network.GetMethod("PedSync", Static)!, vehicleSync = Network.GetMethod("VehicleSync", Static)!;
        void Patch(MethodBase method, string prefix) => boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), prefix));
        void Pump() => host.GetMethod("DoQueuedActions", Static)!.Invoke(null, Array.Empty<object>());
        void ClearQueue() => host.GetMethod("ClearQueuedActions", Static)!.Invoke(null, Array.Empty<object>());
        object Packet(MethodInfo method, int id = 99001)
        {
            Type type = method.GetParameters()[0].ParameterType;
            object packet = Activator.CreateInstance(type, true)!;
            type.GetProperty(method == playerUpdate ? "PedID" : "ID")!.SetValue(packet, id);
            if (method != playerUpdate) type.GetProperty("OwnerID")!.SetValue(packet, 2);
            return packet;
        }
        void Update(int id) => playerUpdate.Invoke(null, new[] { Packet(playerUpdate, id) });
        bool DivideByZero(MethodInfo method)
        {
            try { method.Invoke(null, new[] { Packet(method) }); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is DivideByZeroException) { return true; }
        }
        try
        {
            _rosterGate = guard.Type.GetField("Gate", Static)!.GetValue(null)!;
            logger.SetValue(null, FormatterServices.GetUninitializedObject(typeof(RageCoop.Core.Logger)));
            foreach (MethodInfo method in typeof(RageCoop.Core.Logger).GetMethods(Instance).Where(method => method.Name is "Debug" or "Trace" or "Warning" or "Error"))
                Patch(method, method.Name == "Error" ? nameof(RosterError) : nameof(SkipMenuLayout));
            Patch(guard.Method("Snapshot"), nameof(RosterSnapshot));
            Type configType = Engine.GetType("RageCoop.Client.Settings", true)!;
            object config = FormatterServices.GetUninitializedObject(configType);
            configType.GetProperty("WorldPedSoftLimit")!.SetValue(config, 128);
            configType.GetProperty("WorldVehicleSoftLimit")!.SetValue(config, 128);
            settings.SetValue(null, config);
            players.SetValue(null, new Dictionary<int, Player>());
            scaleform.SetValue(null, FormatterServices.GetUninitializedObject(typeof(Scaleform)));
            ClearQueue();
            Patch(typeof(Scaleform).GetMethod("CallFunction", Instance, null, new[] { typeof(string), typeof(object[]) }, null)!, nameof(RosterScaleform));
            Patch(typeof(API).GetProperty("IsOnServer")!.GetGetMethod()!, nameof(RosterConnection));
            Patch(Network.GetProperty("IsOnServer", Static)!.GetGetMethod()!, nameof(RosterConnection));
            Patch(typeof(World).GetMethod("CreateBlip", Static, null, new[] { typeof(Vector3) }, null)!, nameof(RosterCreateBlip));
            Patch(typeof(Blip).GetMethod("Exists", Instance)!, nameof(RosterExists));
            Patch(typeof(Blip).GetMethod("Delete", Instance)!, nameof(RosterDelete));
            foreach (string name in new[] { "DisplayType", "Color", "Scale", "Sprite", "Position" })
                Patch(typeof(Blip).GetProperty(name)!.GetSetMethod()!, nameof(RosterBlipWrite));
            Patch(typeof(Player).GetProperty("Position")!.GetSetMethod(true)!, nameof(RosterPlayerPosition));
            foreach (string name in new[] { "WorldPedSoftLimit", "WorldVehicleSoftLimit" })
                Patch(configType.GetProperty(name)!.GetGetMethod()!, nameof(RosterLimit));
            Assert(DivideByZero(pedSync) && DivideByZero(vehicleSync), "Actual unpatched new-entity receivers reproduce division by zero before the roster arrives.");
            Assert((bool)guard.Call("HasSupportedContract")!, "The roster guard accepts the exact installed reader, mutation and queued-player contracts.");
            Assert(!(bool)guard.Call("MatchesTarget", Roster.GetMethod("GetPlayer", Static, null, new[] { typeof(SyncedPed) }, null)!)! &&
                !(bool)guard.Call("MatchesTarget", Roster.GetMethod("Tick", Static)!)!,
                "Same-name overloads and unrelated roster methods cannot satisfy the exact body matcher.");
            guard.Install(); guard.Install();
            Assert(((IEnumerable<MethodInfo>)guard.Call("ReadTargets")!).All(method =>
                !PatchProcessor.GetCurrentInstructions(method).Any(code => code.LoadsField(players))),
                "Every actual roster reader uses a guarded snapshot or quota helper rather than the mutable dictionary field.");
            pedSync.Invoke(null, new[] { Packet(pedSync) }); vehicleSync.Invoke(null, new[] { Packet(vehicleSync) });
            Assert(API.Players.Count == 0 && !((IDictionary)Pool.GetField("PedsByID", Static)!.GetValue(null)!).Contains(99001) &&
                !((IDictionary)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!).Contains(99001),
                "Snapshots before roster readiness return without division, entity creation or invented players.");
            foreach (MethodInfo method in new[] { pedSync, vehicleSync })
            {
                RosterSet(2); _rosterClearAtLimit = true;
                method.Invoke(null, new[] { Packet(method) });
                Assert(!_rosterClearAtLimit && API.Players.Count == 0,
                    "Actual receiver quota calculation remains safe if cleanup clears the roster after the entry check.");
            }

            RosterSet(2); RosterSet(3);
            _rosterCopyThread = Thread.CurrentThread.ManagedThreadId; _rosterCopies = 0;
            for (int index = 0; index < 1000; index++) Assert(RosterGet(2)?.ID == 2, "The hot lookup resolves its actual roster member.");
            Assert(_rosterCopies == 0 && API.Players.Count == 2 && _rosterCopies == 1,
                "One thousand actual GetPlayer lookups create no roster snapshots; API.Players still returns a snapshot.");
            _rosterSlots = 0;
            _rosterFirstSlot = () =>
            {
                Task writer = Task.Run(() => { RosterSet(4); RosterRemove(3); });
                Assert(writer.Wait(TimeSpan.FromSeconds(3)), "A real SetPlayer/RemovePlayer writer completes while the first Scaleform slot is paused.");
                writer.GetAwaiter().GetResult();
            };
            Roster.GetMethod("Update", Static)!.Invoke(null, Array.Empty<object>());
            Assert(_rosterSlots == 2 && RosterGet(3) == null && RosterGet(4) != null,
                "Actual PlayerList.Update finishes its snapshot enumeration after concurrent membership changes inside the first native slot.");
            Type punch = Engine.GetType("RageCoop.Client.HolePunch", true)!;
            Task churn = Task.Run(() => { for (int index = 0; index < 150; index++) { RosterSet(10); RosterRemove(10); RosterSet(11); RosterRemove(11); } });
            for (int index = 0; index < 150; index++)
            {
                Dictionary<int, Player> copy = API.Players;
                Assert(copy.All(pair => pair.Value.ID == pair.Key), "The actual API returns a consistent independent dictionary during roster churn.");
                Pool.GetMethod("UpdateTargets", Static)!.Invoke(null, Array.Empty<object>());
                punch.GetMethod("DoPunch", Static)!.Invoke(null, new object?[] { null, null });
            }
            Assert(churn.Wait(TimeSpan.FromSeconds(5)), "Concurrent roster mutations finish without a queue/roster monitor cycle.");
            churn.GetAwaiter().GetResult(); Pump();
            Assert(_rosterErrors == 0, "HolePunch's actual exception boundary observed no enumeration or connection errors during concurrent reads.");
            object init = Activator.CreateInstance(punch.GetMethod("Add", Static)!.GetParameters()[0].ParameterType, true)!;
            init.GetType().GetProperty("TargetID")!.SetValue(init, 2);
            init.GetType().GetProperty("TargetInternal")!.SetValue(init, "127.0.0.1:1");
            init.GetType().GetProperty("TargetExternal")!.SetValue(init, "127.0.0.1:2");
            punch.GetMethod("Add", Static)!.Invoke(null, new[] { init });
            object punched = Activator.CreateInstance(punch.GetMethod("Punched", Static)!.GetParameters()[0].ParameterType, true)!;
            punched.GetType().GetProperty("Puncher")!.SetValue(punched, 2);
            punch.GetMethod("Punched", Static)!.Invoke(null, new object[] { punched, new IPEndPoint(IPAddress.Loopback, 2) });
            Assert(RosterGet(2)!.HolePunchStatus == 1, "Actual HolePunch Add/Punched still resolve and update the intended player.");
            RosterGet(2)!.GetType().GetProperty("InternalEndPoint")!.SetValue(RosterGet(2), null);
            RosterGet(2)!.GetType().GetProperty("ExternalEndPoint")!.SetValue(RosterGet(2), null);

            // Replace the player after UpdatePlayer captured its closure but
            // before that closure is handed to Main.QueueAction.
            Player beforeQueue = RosterGet(2)!;
            _rosterReplaceBeforeQueue = true;
            Update(2); Pump();
            Assert(!ReferenceEquals(beforeQueue, RosterGet(2)) && beforeQueue.FakeBlip == null && RosterCreatedBlips.Count == 0,
                "A queued update retains its captured player identity instead of adopting a replacement with the same ID.");

            Player duringCreation = RosterGet(2)!;
            _rosterCreating = new ManualResetEventSlim(); _rosterReleaseCreate = new ManualResetEventSlim();
            Update(2);
            Task gameQueue = Task.Run(Pump);
            Assert(_rosterCreating.Wait(TimeSpan.FromSeconds(3)), "The actual Main queue reaches the isolated World.CreateBlip boundary.");
            Task removal = Task.Run(() => RosterRemove(2));
            Task<bool> lookup = Task.Run(() => { Stopwatch watch = Stopwatch.StartNew(); while (RosterGet(2) != null && watch.Elapsed.TotalSeconds < 2) Thread.Yield(); return RosterGet(2) == null; });
            bool lookupFinished = lookup.Wait(TimeSpan.FromSeconds(3));
            _rosterReleaseCreate.Set();
            queueResponsive = gameQueue.Wait(TimeSpan.FromSeconds(3)) && removal.Wait(TimeSpan.FromSeconds(3));
            Assert(lookupFinished && lookup.Result && queueResponsive,
                "Removal releases the roster monitor before waiting for the real queue; lookup and the game callback cannot deadlock.");
            gameQueue.GetAwaiter().GetResult(); removal.GetAwaiter().GetResult(); Pump();
            Assert(duringCreation.FakeBlip == null && RosterCreatedBlips.All(RosterDeleted.Contains),
                "Membership removal during native blip creation is detected afterward and leaves no orphan FakeBlip.");
            _rosterCreating.Dispose(); _rosterReleaseCreate.Dispose(); _rosterCreating = _rosterReleaseCreate = null;

            RosterSet(2); Update(2); _rosterConnected = false; Pump(); _rosterConnected = true;
            Assert(RosterGet(2)!.FakeBlip == null, "A queued update cannot create a blip after disconnect.");
            _rosterDuringCreate = () => _rosterConnected = false;
            Update(2); Pump(); _rosterConnected = true;
            Assert(RosterGet(2)!.FakeBlip == null && RosterCreatedBlips.All(RosterDeleted.Contains),
                "Disconnect during native creation deletes the just-created FakeBlip after the action returns.");
            FieldInfo connection = Network.GetField("ServerConnection", Static)!;
            object? previousConnection = connection.GetValue(null);
            try
            {
                Update(2);
                connection.SetValue(null, FormatterServices.GetUninitializedObject(connection.FieldType));
                Pump();
                Assert(RosterGet(2)!.FakeBlip == null, "A quick reconnect cannot revive a queued blip update from the previous server connection.");
                connection.SetValue(null, previousConnection);
                _rosterDuringCreate = () => connection.SetValue(null, FormatterServices.GetUninitializedObject(connection.FieldType));
                Update(2); Pump();
                Assert(RosterGet(2)!.FakeBlip == null && RosterCreatedBlips.All(RosterDeleted.Contains),
                    "Replacing the server connection during native creation also removes the obsolete blip afterward.");
            }
            finally { connection.SetValue(null, previousConnection); }
            Update(2); Pump();
            int liveBlip = RosterGet(2)!.FakeBlip!.Handle;
            RosterRemove(2); Pump();
            Assert(RosterDeleted.Contains(liveBlip), "The original later-leave queue semantics still delete a previously completed blip.");
            RosterSet(5); Update(5); Pump();
            Player beforeCleanup = RosterGet(5)!;
            int cleanupBlip = beforeCleanup.FakeBlip!.Handle;
            Roster.GetMethod("Cleanup", Static)!.Invoke(null, Array.Empty<object>());
            Assert(API.Players.Count == 0 && beforeCleanup.FakeBlip == null && RosterDeleted.Contains(cleanupBlip),
                "Actual Cleanup swaps the roster atomically and removes existing blips outside its monitor.");
            CheckRosterCharacterRepair(network, boundary);
            CheckRosterProjectiles(network, gameplay, boundary);
            Console.WriteLine("Actual roster concurrency, queued blip identity, empty-roster packet handling and leaving-player projectile checks passed; GTA boundaries were isolated.");
        }
        finally
        {
            _rosterReleaseCreate?.Set();
            gameplay.Uninstall(); network.Uninstall(); guard.Uninstall();
            boundary.UnpatchAll(boundary.Id);
            if (queueResponsive)
            {
                ClearQueue(); players.SetValue(null, previousPlayers); logger.SetValue(null, previousLogger); settings.SetValue(null, previousSettings); scaleform.SetValue(null, previousScaleform);
                _rosterCreating?.Dispose(); _rosterReleaseCreate?.Dispose();
            }
            _rosterCreating = _rosterReleaseCreate = null; _rosterFirstSlot = _rosterDuringCreate = null;
            _rosterConnected = true; _rosterReplaceBeforeQueue = _rosterClearAtLimit = false;
            RosterCreatedBlips.Clear(); RosterDeleted.Clear(); _rosterExplosions = 0;
            _rosterCopies = _rosterCopyThread = _rosterErrors = 0;
        }
    }

    private static void CheckRosterProjectiles(Guard network, Guard gameplay, Harmony boundary)
    {
        Type projectile = Engine.GetType("RageCoop.Client.SyncedProjectile", true)!;
        IDictionary ids = (IDictionary)Pool.GetField("ProjectilesByID", Static)!.GetValue(null)!, handles = (IDictionary)Pool.GetField("ProjectilesByHandle", Static)!.GetValue(null)!;
        foreach (Type type in new[] { typeof(Entity), typeof(Prop), typeof(Ped), typeof(Vehicle) })
        {
            MethodInfo? exists = type.GetMethod("Exists", Instance | BindingFlags.DeclaredOnly);
            if (exists != null && !exists.IsAbstract) boundary.Patch(exists, prefix: new HarmonyMethod(typeof(Program), nameof(RosterExists)));
        }
        boundary.Patch(typeof(Entity).GetMethod("Delete", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(RosterDelete)));
        boundary.Patch(gameplay.Method("ExplodeProjectile"), prefix: new HarmonyMethod(typeof(Program), nameof(RosterExplosion)));
        void Add(int id, int shooterId, int owner)
        {
            var shooter = (SyncedPed)Activator.CreateInstance(typeof(SyncedPed), Instance, null, new object[] { shooterId }, null)!;
            typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(shooter, owner);
            object record = Activator.CreateInstance(projectile, Instance, null, new object[] { id }, null)!;
            Projectile physical = SpawnNative<Projectile>(id + 100);
            projectile.GetProperty("Shooter")!.SetValue(record, shooter);
            projectile.GetProperty("MainProjectile")!.SetValue(record, physical);
            ids.Add(id, record); handles.Add(physical.Handle, record);
        }
        try
        {
            network.Install(); gameplay.Install();
            Add(99801, 200, 200); Add(99802, 201, 200); Add(99803, 300, 300);
            Pool.GetMethod("RemoveAllFromPlayer", Static)!.Invoke(null, new object[] { 200 });
            Assert(!ids.Contains(99801) && !ids.Contains(99802) && ids.Contains(99803) && !handles.Contains(99901) && !handles.Contains(99902) &&
                RosterDeleted.Contains(99901) && RosterDeleted.Contains(99902) && _rosterExplosions == 0,
                "Actual RemoveAllFromPlayer also deletes the leaving player's and owned NPC's projectiles through GameplayRemove without creating timeout explosions.");
        }
        finally { foreach (int id in new[] { 99801, 99802, 99803 }) ids.Remove(id); foreach (int id in new[] { 99901, 99902, 99903 }) handles.Remove(id); }
    }

    private static Action? _rosterDuringCharacterRepair;
    private static void CheckRosterCharacterRepair(Guard network, Harmony boundary)
    {
        MethodInfo setter = typeof(Player).GetProperty("Character")!.GetSetMethod(true)!;
        boundary.Patch(setter, prefix: new HarmonyMethod(typeof(Program), nameof(RosterCharacterRepair)));
        try
        {
            RosterSet(2); RosterSet(3);
            _rosterDuringCharacterRepair = () => { RosterSet(4); RosterRemove(3); };
            network.Install();
            Assert(_rosterDuringCharacterRepair == null && API.Players.Keys.OrderBy(id => id).SequenceEqual(new[] { 2, 4 }),
                "Actual network guard installation repairs a roster snapshot while SetPlayer/RemovePlayer change live membership.");
        }
        finally
        {
            network.Uninstall();
            _rosterDuringCharacterRepair = null;
            boundary.Unpatch(setter, HarmonyPatchType.Prefix, boundary.Id);
            Roster.GetMethod("Cleanup", Static)!.Invoke(null, Array.Empty<object>());
        }
    }
    private static void RosterCharacterRepair()
    {
        Action? during = _rosterDuringCharacterRepair;
        _rosterDuringCharacterRepair = null;
        during?.Invoke();
    }

    private static void RosterSet(int id) => Roster.GetMethod("SetPlayer", Static)!.Invoke(null, new object[] { id, "Player " + id, 0f });
    private static void RosterRemove(int id) => Roster.GetMethod("RemovePlayer", Static)!.Invoke(null, new object[] { id });
    private static Player? RosterGet(int id) => (Player?)Roster.GetMethod("GetPlayer", Static, null, new[] { typeof(int) }, null)!.Invoke(null, new object[] { id });
    private static bool RosterConnection(ref bool __result) { __result = _rosterConnected; return false; }
    private static bool RosterError() { Interlocked.Increment(ref _rosterErrors); return false; }
    private static void RosterSnapshot() { if (Thread.CurrentThread.ManagedThreadId == _rosterCopyThread) _rosterCopies++; }
    private static void RosterNoGate() => Assert(!Monitor.IsEntered(_rosterGate), "No roster monitor is held across a GTA boundary.");
    private static bool RosterScaleform(string __0)
    {
        RosterNoGate();
        if (__0 == "SET_DATA_SLOT" && _rosterSlots++ == 0) { Action? first = _rosterFirstSlot; _rosterFirstSlot = null; first?.Invoke(); }
        return false;
    }
    private static bool RosterCreateBlip(ref Blip __result)
    {
        RosterNoGate();
        Action? during = _rosterDuringCreate; _rosterDuringCreate = null; during?.Invoke();
        _rosterCreating?.Set();
        if (_rosterReleaseCreate != null && !_rosterReleaseCreate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Blip race test was not released.");
        int handle = 99700 + RosterCreatedBlips.Count;
        RosterCreatedBlips.Add(handle);
        __result = (Blip)typeof(Blip).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });
        return false;
    }
    private static bool RosterExists(PoolObject __instance, ref bool __result) { RosterNoGate(); __result = !RosterDeleted.Contains(__instance.Handle); return false; }
    private static bool RosterDelete(PoolObject __instance) { RosterNoGate(); RosterDeleted.Add(__instance.Handle); return false; }
    private static bool RosterBlipWrite() { RosterNoGate(); return false; }
    private static bool RosterExplosion() { _rosterExplosions++; return false; }
    private static void RosterPlayerPosition(Player __instance)
    {
        if (!_rosterReplaceBeforeQueue) return;
        _rosterReplaceBeforeQueue = false; RosterRemove(__instance.ID); RosterSet(__instance.ID);
    }
    private static void RosterLimit()
    {
        if (!_rosterClearAtLimit) return;
        _rosterClearAtLimit = false; Roster.GetMethod("Cleanup", Static)!.Invoke(null, Array.Empty<object>());
    }

}
