using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GTA;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;

internal static partial class Program
{
    private static bool _gameplayConnected = true, _gameplayModelLoaded, _gameplayCreateSucceeds;
    private static long _gameplayNow;
    private static int _gameplayCreates, _gameplayModelRequests, _gameplayShots;
    private static Projectile[] _gameplayBefore = Array.Empty<Projectile>(), _gameplayAfter = Array.Empty<Projectile>();
    private static readonly HashSet<int> GameplayDeleted = new HashSet<int>();
    private static readonly HashSet<int> GameplayExploded = new HashSet<int>();
    private static readonly Dictionary<int, Vector3> GameplayPositions = new Dictionary<int, Vector3>();
    private static readonly Dictionary<int, WeaponHash> GameplayWeapons = new Dictionary<int, WeaponHash>();
    private static readonly Dictionary<int, Entity?> GameplayOwners = new Dictionary<int, Entity?>();
    private static readonly Vehicle GameplayVehicle = SpawnNative<Vehicle>(840001);

    private static void CheckGameplayComposition()
    {
        var guard = new Guard("Gameplay");
        var boundary = new Harmony("friendslegacy.check.gameplayboundary");
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        Type projectileType = Engine.GetType("RageCoop.Client.SyncedProjectile", true)!;
        MethodInfo spawn = (MethodInfo)guard.Call("FindVehicleTarget")!, createProjectile = (MethodInfo)guard.Call("FindProjectileTarget")!;
        MethodInfo cleanup = (MethodInfo)guard.Call("FindCleanupTarget")!, remove = (MethodInfo)guard.Call("FindRemoveTarget")!;
        IDictionary ids = (IDictionary)Pool.GetField("ProjectilesByID", Static)!.GetValue(null)!;
        IDictionary handles = (IDictionary)Pool.GetField("ProjectilesByHandle", Static)!.GetValue(null)!;
        IDictionary vehicles = (IDictionary)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!;
        IDictionary vehicleHandles = (IDictionary)Pool.GetField("VehiclesByHandle", Static)!.GetValue(null)!;
        object baseScript = host.GetField("BaseScript", Static)!.GetValue(null)!;
        void Patch(MethodBase method, string prefix) => boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), prefix));
        void Pump() => host.GetMethod("DoQueuedActions", Static)!.Invoke(null, Array.Empty<object>());
        void Spawn(int id) => spawn.Invoke(baseScript, new object[] { new CustomEventReceivedArgs { Args = new object[] { id, new Model(444), new Vector3(1, 2, 3), 90f } } });
        object ProjectileRecord(int id, Projectile? physical = null)
        {
            object record = Activator.CreateInstance(projectileType, Instance, null, new object[] { id }, null)!;
            projectileType.GetProperty("MainProjectile")!.SetValue(record, physical);
            ids.Add(id, record);
            if (physical != null) { handles[physical.Handle] = record; }
            return record;
        }
        try
        {
            Assert((bool)guard.Call("HasSupportedContract", spawn, createProjectile, cleanup, remove)! &&
                !(bool)guard.Call("HasSupportedContract", createProjectile, spawn, cleanup, remove)!,
                "The gameplay guard accepts only its four exact installed targets.");
            Patch(typeof(API).GetProperty("IsOnServer")!.GetGetMethod()!, nameof(GameplayConnection));
            Patch(guard.Method("Timestamp"), nameof(GameplayTimestamp));
            Patch(guard.Method("CreateGameVehicle"), nameof(GameplayCreateVehicle));
            Patch(guard.Method("ShootProjectile"), nameof(GameplayShoot));
            Patch(guard.Method("InitializeVehicle"), nameof(SkipMenuLayout));
            Patch(guard.Method("GetProjectiles"), nameof(GameplayProjectiles));
            boundary.Patch(guard.Method("MatchesProjectile"), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateGameplayProjectileBoundary)));
            Patch(guard.Method("ExplodeProjectile"), nameof(GameplayExplode));
            foreach (string name in new[] { "IsInCdImage", "IsVehicle" })
                Patch(typeof(Model).GetProperty(name)!.GetGetMethod()!, nameof(GameplayTrue));
            Patch(typeof(Model).GetProperty("IsLoaded")!.GetGetMethod()!, nameof(GameplayModelLoaded));
            Patch(typeof(Model).GetMethod("Request", Instance, null, Type.EmptyTypes, null)!, nameof(GameplayRequestModel));
            Patch(typeof(Model).GetMethod("MarkAsNoLongerNeeded", Instance)!, nameof(SkipMenuLayout));
            Patch(typeof(WeaponAsset).GetProperty("IsLoaded")!.GetGetMethod()!, nameof(GameplayTrue));
            Patch(typeof(RageCoop.Client.Player).GetProperty("PacketTravelTime")!.GetGetMethod()!, nameof(GameplayTravel));
            foreach (Type type in new[] { typeof(Entity), typeof(Ped), typeof(Prop), typeof(Vehicle) })
            {
                MethodInfo? exists = type.GetMethod("Exists", Instance | BindingFlags.DeclaredOnly);
                if (exists != null && !exists.IsAbstract) { Patch(exists, nameof(GameplayExists)); }
            }
            Patch(typeof(Entity).GetMethod("Delete", Instance)!, nameof(GameplayDelete));
            Patch(typeof(Entity).GetProperty("Position")!.GetSetMethod()!, nameof(GameplayPosition));
            Patch(typeof(Entity).GetProperty("Rotation")!.GetSetMethod()!, nameof(SkipMenuLayout));
            Patch(typeof(Entity).GetProperty("Velocity")!.GetSetMethod()!, nameof(SkipMenuLayout));
            host.GetMethod("ClearQueuedActions", Static)!.Invoke(null, Array.Empty<object>());

            ProjectileRecord(840010);
            bool cleanupFailed = false;
            try { cleanup.Invoke(null, new object[] { true, true }); }
            catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { cleanupFailed = true; }
            Assert(cleanupFailed, "The real unpatched cleanup reproduces the null-shooter exception with no GTA calls.");
            guard.Install(); guard.Install();
            cleanup.Invoke(null, new object[] { true, true });
            Assert(ids.Count == 0 && handles.Count == 0, "The patched real cleanup completes for a projectile received before its shooter.");

            _gameplayNow = 100; _gameplayModelLoaded = false;
            Spawn(840020); Spawn(840020);
            Assert(_gameplayCreates == 0, "The real server vehicle handler only queues creation and returns without a blocking attempt.");
            Pump(); Pump();
            Assert(_gameplayModelRequests == 2 && _gameplayCreates == 0,
                "An unloaded valid model requests once per real queue tick without calling the blocking model overload.");
            _gameplayModelLoaded = true; Pump(); Pump();
            Assert(_gameplayCreates == 2 && !vehicles.Contains(840020), "A full GTA vehicle pool returns control after each failed creation.");
            _gameplayNow += Stopwatch.Frequency * 10; Pump(); Pump();
            Assert(_gameplayCreates == 2, "The real queued operation expires after ten seconds and stops retrying.");
            _gameplayCreateSucceeds = true;
            Spawn(840021); Spawn(840021); Pump(); Pump();
            Assert(_gameplayCreates == 3 && vehicles[840021] is SyncedVehicle created && created.MainVehicle == GameplayVehicle && created.OwnerID == API.LocalPlayerID &&
                ReferenceEquals(vehicleHandles[GameplayVehicle.Handle], created), "A duplicate server event creates and registers exactly one real installed pool record.");
            Spawn(840021); Pump(); Assert(_gameplayCreates == 3, "A repeated completed event cannot replace its existing network ID.");
            Spawn(840022); _gameplayConnected = false; Pump(); _gameplayConnected = true; Pump();
            Assert(_gameplayCreates == 3 && !vehicles.Contains(840022), "Disconnect cancels queued creation and reconnect cannot revive it.");
            Spawn(840023); guard.Uninstall(); guard.Install(); Pump();
            Assert(_gameplayCreates == 3 && !vehicles.Contains(840023), "Uninstall/reinstall cancels callbacks retained by the host queue.");
            Spawn(840024);
            object? originalConnection = Network.GetField("ServerConnection", Static)!.GetValue(null);
            Network.GetField("ServerConnection", Static)!.SetValue(null, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(Network.GetField("ServerConnection", Static)!.FieldType));
            try { Pump(); }
            finally { Network.GetField("ServerConnection", Static)!.SetValue(null, originalConnection); }
            Assert(_gameplayCreates == 3 && !vehicles.Contains(840024), "Changing the server session cancels an old queued creation even while connected remains true.");

            var shooter = (SyncedPed)Activator.CreateInstance(typeof(SyncedPed), Instance, null, new object[] { 840030 }, null)!;
            Ped source = SpawnNative<Ped>(840031);
            typeof(SyncedPed).GetProperty("MainPed")!.SetValue(shooter, source);
            typeof(SyncedEntity).GetField("<Owner>k__BackingField", Instance)!.SetValue(shooter, new RageCoop.Client.Player());
            object record = ProjectileRecord(840032);
            projectileType.GetProperty("Shooter")!.SetValue(record, shooter);
            projectileType.GetProperty("WeaponHash")!.SetValue(record, (WeaponHash)123);
            typeof(SyncedEntity).GetProperty("Position", Instance)!.SetValue(record, new Vector3(4, 5, 6));
            typeof(SyncedEntity).GetProperty("Velocity", Instance)!.SetValue(record, new Vector3(7, 8, 9));
            Projectile old = SpawnNative<Projectile>(840033), fresh = SpawnNative<Projectile>(840034);
            GameplayWeapons[old.Handle] = GameplayWeapons[fresh.Handle] = (WeaponHash)123;
            GameplayOwners[old.Handle] = GameplayOwners[fresh.Handle] = source;
            object unrelated = ProjectileRecord(840035, old);
            _gameplayBefore = new[] { old }; _gameplayAfter = new[] { fresh, old }; _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(ReferenceEquals(projectileType.GetProperty("MainProjectile")!.GetValue(record), fresh) &&
                ReferenceEquals(handles[old.Handle], unrelated) && ReferenceEquals(handles[fresh.Handle], record) &&
                !GameplayPositions.ContainsKey(old.Handle) && GameplayPositions[fresh.Handle] == new Vector3(4, 5, 6),
                "Real projectile creation chooses the newly occupied low slot and never moves or remaps the old last projectile.");
            ids.Remove(840032); handles.Remove(fresh.Handle);
            record = ProjectileRecord(840036);
            projectileType.GetProperty("Shooter")!.SetValue(record, shooter);
            projectileType.GetProperty("WeaponHash")!.SetValue(record, (WeaponHash)123);
            _gameplayBefore = _gameplayAfter = Array.Empty<Projectile>(); _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null, "An empty post-shot GTA pool leaves the real incoming projectile pending without an exception.");
            _gameplayBefore = _gameplayAfter = new[] { old }; _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null && ReferenceEquals(handles[old.Handle], unrelated),
                "A failed native shot cannot adopt an existing projectile.");
            _gameplayBefore = Array.Empty<Projectile>(); _gameplayAfter = new[] { fresh }; _gameplayShots = 0;
            GameplayWeapons[fresh.Handle] = (WeaponHash)456;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null, "A new projectile with another weapon cannot be adopted.");
            GameplayWeapons[fresh.Handle] = (WeaponHash)123;
            GameplayOwners[fresh.Handle] = SpawnNative<Ped>(840037); _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null, "A new projectile belonging to another shooter cannot be adopted.");
            GameplayOwners[fresh.Handle] = source;
            _gameplayBefore = Array.Empty<Projectile>(); _gameplayAfter = new[] { fresh, fresh }; _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null, "Duplicate or ambiguous new handles cannot authorize adoption.");

            var vehicleShooter = (SyncedVehicle)Activator.CreateInstance(typeof(SyncedVehicle), Instance, null, new object[] { 840050 }, null)!;
            typeof(SyncedVehicle).GetProperty("MainVehicle")!.SetValue(vehicleShooter, GameplayVehicle);
            typeof(SyncedEntity).GetField("<Owner>k__BackingField", Instance)!.SetValue(vehicleShooter, new RageCoop.Client.Player());
            projectileType.GetProperty("Shooter")!.SetValue(record, vehicleShooter);
            typeof(SyncedEntity).GetProperty("Position", Instance)!.SetValue(record, new Vector3(40, 50, 60));
            _gameplayAfter = new[] { fresh }; _gameplayShots = 0;
            GameplayPositions[fresh.Handle] = new Vector3(40, 50, 60); GameplayOwners[fresh.Handle] = source;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(ReferenceEquals(projectileType.GetProperty("MainProjectile")!.GetValue(record), fresh),
                "The actual vehicle-source path accepts its unique new shot at the requested position when GTA attributes ownership to the driver.");
            projectileType.GetProperty("MainProjectile")!.SetValue(record, null); handles.Remove(fresh.Handle);
            GameplayOwners[fresh.Handle] = null; _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(ReferenceEquals(projectileType.GetProperty("MainProjectile")!.GetValue(record), fresh),
                "A vehicle-source shot with no reported owner still preserves the installed shoot operation through new handle, weapon and position identity.");
            projectileType.GetProperty("MainProjectile")!.SetValue(record, null); handles.Remove(fresh.Handle);
            GameplayPositions[fresh.Handle] = new Vector3(400, 500, 600); _gameplayShots = 0;
            createProjectile.Invoke(record, Array.Empty<object>());
            Assert(projectileType.GetProperty("MainProjectile")!.GetValue(record) == null,
                "A vehicle-source shot cannot adopt a new same-weapon projectile elsewhere in the world.");

            remove.Invoke(null, new object[] { 840035, "OutOfSync" });
            Assert(GameplayDeleted.Contains(old.Handle) && !GameplayExploded.Contains(old.Handle) && !ids.Contains(840035) && !handles.Contains(old.Handle),
                "Actual timeout removal deletes a live remote projectile without inventing an explosion or leaving a discoverable orphan.");
            Projectile exploding = SpawnNative<Projectile>(840040);
            object explosion = ProjectileRecord(840041, exploding);
            PropertyInfo flags = projectileType.GetProperty("Flags")!;
            flags.SetValue(explosion, Enum.Parse(flags.PropertyType, "Exploded"));
            remove.Invoke(null, new object[] { 840041, "Exploded" });
            Assert(GameplayExploded.Contains(exploding.Handle) && !GameplayDeleted.Contains(exploding.Handle), "Confirmed explosion retains the engine's original explosion operation.");
            Projectile shared = SpawnNative<Projectile>(840042);
            object stale = ProjectileRecord(840043, shared), replacement = ProjectileRecord(840044, shared);
            remove.Invoke(null, new object[] { 840043, "OutOfSync" });
            Assert(ReferenceEquals(handles[shared.Handle], replacement) && !GameplayDeleted.Contains(shared.Handle) && !ids.Contains(840043),
                "Removing a stale record preserves the physical projectile and mapping now belonging to another record.");
            _ = stale;
            Projectile local = SpawnNative<Projectile>(840045);
            object localRecord = ProjectileRecord(840046, local);
            projectileType.GetProperty("IsLocal", Instance | BindingFlags.DeclaredOnly)!.SetValue(localRecord, true);
            remove.Invoke(null, new object[] { 840046, "Dead" });
            Assert(!GameplayDeleted.Contains(local.Handle) && !GameplayExploded.Contains(local.Handle) && !ids.Contains(840046),
                "Removing a locally owned projectile preserves the game's original physical lifecycle.");
            object nullShooter = ProjectileRecord(840047, SpawnNative<Projectile>(840048));
            _ = nullShooter;
            cleanup.Invoke(null, new object[] { true, true });
            Assert(ids.Count == 0 && handles.Count == 0 && GameplayDeleted.Contains(840048),
                "The real complete cleanup handles both a missing shooter and its live physical projectile.");
            CheckVehicleWatchLifecycle(Pump, vehicles);
            CheckPassengerCancellation(boundary);
        }
        finally
        {
            guard.Uninstall(); boundary.UnpatchAll(boundary.Id);
            ids.Clear(); handles.Clear(); vehicles.Remove(840021); vehicleHandles.Remove(GameplayVehicle.Handle);
            host.GetMethod("ClearQueuedActions", Static)!.Invoke(null, Array.Empty<object>());
            GameplayDeleted.Clear(); GameplayExploded.Clear(); GameplayPositions.Clear(); GameplayWeapons.Clear(); GameplayOwners.Clear();
            _gameplayConnected = true; _gameplayCreateSucceeds = false; _gameplayModelLoaded = false;
            _gameplayCreates = _gameplayModelRequests = _gameplayShots = 0;
        }
    }

    private static bool GameplayConnection(ref bool __result) { __result = _gameplayConnected; return false; }

    private static void CheckVehicleWatchLifecycle(Action pump, IDictionary vehicles)
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        object instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(script);
        FieldInfo watchedField = script.GetField("_vehicles", Instance)!;
        var watched = (IDictionary)Activator.CreateInstance(watchedField.FieldType)!;
        watchedField.SetValue(instance, watched);
        script.GetField("_active", Instance)!.SetValue(instance, true);
        SyncedVehicle Record() => (SyncedVehicle)Activator.CreateInstance(typeof(SyncedVehicle), Instance, null, new object[] { 840070 }, null)!;
        var old = Record(); var current = Record();
        typeof(SyncedEntity).GetProperty("OwnerID")!.SetValue(old, API.LocalPlayerID);
        typeof(SyncedEntity).GetProperty("OwnerID")!.SetValue(current, API.LocalPlayerID);
        void Event(string name, SyncedVehicle vehicle) => script.GetMethod(name, Instance)!.Invoke(instance, new object?[] { null, vehicle });
        try
        {
            vehicles[old.ID] = old;
            Event("OnVehicleSpawned", old); pump();
            Assert(watched.Contains(old.ID), "The actual queued local spawn begins watching the current vehicle record.");
            vehicles[current.ID] = current;
            Event("OnVehicleSpawned", current); Event("OnVehicleDeleted", old); pump();
            Assert(watched.Contains(current.ID) && ReferenceEquals(watched[current.ID]!.GetType().GetProperty("Synced")!.GetValue(watched[current.ID]), current),
                "A delayed delete for an old record cannot remove its replacement with the same network ID.");
            Event("OnVehicleSpawned", old); pump();
            Assert(ReferenceEquals(watched[current.ID]!.GetType().GetProperty("Synced")!.GetValue(watched[current.ID]), current),
                "A delayed spawn for an old record cannot replace the current repair watch.");
            Event("OnVehicleDeleted", current); pump();
            Assert(!watched.Contains(current.ID), "A current delete still removes its own repair watch.");
            Event("OnVehicleSpawned", current); _gameplayConnected = false; pump();
            Assert(!watched.Contains(current.ID), "A queued spawn cannot recreate a repair watch after disconnect.");
        }
        finally { vehicles.Remove(old.ID); _gameplayConnected = true; }
    }

    private static bool _passengerFrozen, _passengerFocusCleared;
    private static void CheckPassengerCancellation(Harmony boundary)
    {
        void Patch(MethodInfo target, string prefix) => boundary.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        Type pendingType = script.GetNestedType("PendingPassengerTeleport", BindingFlags.NonPublic)!;
        object instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(script);
        Ped ped = SpawnNative<Ped>(840080);
        GameplayPositions[ped.Handle] = new Vector3(1, 2, 3);
        Patch(typeof(Entity).GetProperty("Position")!.GetGetMethod()!, nameof(GameplayReadPositionBoundary));
        MethodInfo cancel = script.GetMethod("CancelPassengerTeleport", Instance)!;
        foreach (MethodBase method in new MethodBase[] { pendingType.GetConstructors(Instance).Single(), cancel })
        {
            boundary.Patch(method, transpiler: new HarmonyMethod(typeof(Program), nameof(PassengerFreezeBoundary)));
        }
        Patch(typeof(Entity).GetProperty("IsDead")!.GetGetMethod()!, nameof(GameplayFalse));
        Patch(typeof(Ped).GetMethod("IsInVehicle", Instance, null, Type.EmptyTypes, null)!, nameof(GameplayFalse));
        MethodInfo send = typeof(API).GetMethod("SendCustomEvent", Static)!;
        Patch(send, nameof(PassengerSendFailure));
        try
        {
            _passengerFrozen = false; _passengerFocusCleared = false;
            object pending = Activator.CreateInstance(pendingType, Instance, null,
                new object[] { 7, 8, 9, new Vector3(1000, 2000, 3000), ped, Stopwatch.GetTimestamp() }, null)!;
            pendingType.GetProperty("MovedForStreaming", Instance)!.SetValue(pending, true);
            script.GetField("_passengerTeleport", Instance)!.SetValue(instance, pending);
            _passengerFrozen = true;
            GameplayPositions[ped.Handle] = new Vector3(1000, 2000, 3003);
            bool failed = false;
            try { cancel.Invoke(instance, new object?[] { null }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException exception && exception.Message == "Disconnected while sending.") { failed = true; }
            Assert(failed && !_passengerFrozen && _passengerFocusCleared && GameplayPositions[ped.Handle] == new Vector3(1, 2, 3) &&
                script.GetField("_passengerTeleport", Instance)!.GetValue(instance) == null,
                "Actual passenger cancellation restores position, freeze and focus even if reporting the result fails during disconnect.");
        }
        finally
        {
            boundary.Unpatch(send, HarmonyPatchType.All, boundary.Id);
        }
    }

    private static bool GameplayFalse(ref bool __result) { __result = false; return false; }
    private static bool GameplayReadPositionBoundary(Entity __instance, ref Vector3 __result) { __result = GameplayPositions[__instance.Handle]; return false; }
    private static bool PassengerReadFrozen(Entity entity) => _passengerFrozen;
    private static void PassengerWriteFrozen(Entity entity, bool frozen) => _passengerFrozen = frozen;
    private static IEnumerable<CodeInstruction> PassengerFreezeBoundary(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(Entity) &&
                (method.Name == "get_IsPositionFrozen" || method.Name == "set_IsPositionFrozen"))
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(method.Name == "get_IsPositionFrozen" ? nameof(PassengerReadFrozen) : nameof(PassengerWriteFrozen), Static)!;
            }
            else if (code.operand is MethodInfo native && native.DeclaringType == typeof(GTA.Native.Function) && native.Name == "Call" && !native.IsGenericMethod)
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(native.GetParameters().Length == 1 ? nameof(PassengerClearFocus) : nameof(PassengerClearFocusWithArguments), Static)!;
            }
            yield return code;
        }
    }
    private static bool PassengerSendFailure() => throw new InvalidOperationException("Disconnected while sending.");
    private static void PassengerClearFocus(GTA.Native.Hash hash) { if (hash == GTA.Native.Hash.CLEAR_FOCUS) _passengerFocusCleared = true; }
    private static void PassengerClearFocusWithArguments(GTA.Native.Hash hash, GTA.Native.InputArgument[] arguments) => PassengerClearFocus(hash);
    private static bool GameplayTimestamp(ref long __result) { __result = _gameplayNow; return false; }
    private static bool GameplayTrue(ref bool __result) { __result = true; return false; }
    private static bool GameplayModelLoaded(ref bool __result) { __result = _gameplayModelLoaded; return false; }
    private static bool GameplayRequestModel() { _gameplayModelRequests++; return false; }
    private static bool GameplayTravel(ref float __result) { __result = 0; return false; }
    private static bool GameplayCreateVehicle(ref Vehicle? __result) { _gameplayCreates++; __result = _gameplayCreateSucceeds ? GameplayVehicle : null; return false; }
    private static bool GameplayExists(Entity __instance, ref bool __result) { __result = !GameplayDeleted.Contains(__instance.Handle); return false; }
    private static bool GameplayDelete(PoolObject __instance) { GameplayDeleted.Add(__instance.Handle); return false; }
    private static bool GameplayExplode(Projectile __0) { GameplayExploded.Add(__0.Handle); return false; }
    private static bool GameplayShoot() { _gameplayShots++; return false; }
    private static bool GameplayProjectiles(ref Projectile[] __result) { __result = _gameplayShots == 0 ? _gameplayBefore : _gameplayAfter; return false; }
    private static bool GameplayPosition(Entity __instance, Vector3 __0) { GameplayPositions[__instance.Handle] = __0; return false; }
    private static IEnumerable<CodeInstruction> IsolateGameplayProjectileBoundary(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method)
            {
                string? replacement = method.DeclaringType == typeof(Projectile) && method.Name == "get_WeaponHash" ? nameof(GameplayReadWeapon) :
                    method.DeclaringType == typeof(Projectile) && method.Name == "get_OwnerEntity" ? nameof(GameplayReadOwner) :
                    method.DeclaringType == typeof(Entity) && method.Name == "get_Position" ? nameof(GameplayReadPosition) :
                    method.DeclaringType == typeof(Game) && method.Name == "get_LastFrameTime" ? nameof(GameplayFrameTime) :
                    typeof(Entity).IsAssignableFrom(method.DeclaringType) && method.Name == "Exists" ? nameof(GameplayReadExists) : null;
                if (replacement != null) { code.opcode = OpCodes.Call; code.operand = typeof(Program).GetMethod(replacement, Static)!; }
            }
            yield return code;
        }
    }
    private static WeaponHash GameplayReadWeapon(Projectile projectile) => GameplayWeapons[projectile.Handle];
    private static Entity? GameplayReadOwner(Projectile projectile) => GameplayOwners[projectile.Handle];
    private static Vector3 GameplayReadPosition(Entity entity) => GameplayPositions[entity.Handle];
    private static float GameplayFrameTime() => 1f / 60;
    private static bool GameplayReadExists(Entity entity) => !GameplayDeleted.Contains(entity.Handle);
}
