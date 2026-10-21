using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using GTA;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using Player = RageCoop.Client.Player;

namespace RageCoopAdmin.Client;

// Keep the installed event/packet contracts. Creation may fail at the GTA
// boundary; it must neither block the script nor adopt another live projectile.
internal static class EngineGameplayGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginegameplay";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly Engine = typeof(SyncedEntity).Assembly;
    private static readonly Type Pool = Engine.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly Type ProjectileType = Engine.GetType("RageCoop.Client.SyncedProjectile", true)!;
    private static readonly Type Network = Engine.GetType("RageCoop.Client.Networking", true)!;
    private static readonly Dictionary<int, PendingVehicle> PendingVehicles = new Dictionary<int, PendingVehicle>();
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static readonly PropertyInfo ProjectileObject = Property(ProjectileType, "MainProjectile", typeof(Projectile));
    private static readonly PropertyInfo ProjectileShooter = Property(ProjectileType, "Shooter", typeof(SyncedEntity));
    private static readonly PropertyInfo ProjectileLocal = Property(ProjectileType, "IsLocal", typeof(bool));
    private static readonly PropertyInfo ProjectileExploded = Property(ProjectileType, "Exploded", typeof(bool));
    private static readonly PropertyInfo ProjectileWeapon = Property(ProjectileType, "WeaponHash", typeof(WeaponHash));
    private static readonly PropertyInfo EntityPosition = Property(typeof(SyncedEntity), "Position", typeof(Vector3));
    private static readonly PropertyInfo EntityVelocity = Property(typeof(SyncedEntity), "Velocity", typeof(Vector3));
    private static readonly PropertyInfo EntityRotation = Property(typeof(SyncedEntity), "Rotation", typeof(Vector3));
    private static readonly PropertyInfo EntityOwner = Property(typeof(SyncedEntity), "Owner", typeof(Player));
    private static readonly MethodInfo AddVehicle = Pool.GetMethod("Add", Static, null, new[] { typeof(SyncedVehicle) }, null)!;
    private static readonly MethodInfo AddProjectile = Pool.GetMethod("Add", Static, null, new[] { ProjectileType }, null)!;
    private static readonly MethodInfo SetupVehicle = typeof(SyncedVehicle).GetMethod("SetUpFixedData", Instance)!;
    private static readonly FieldInfo Connection = Network.GetField("ServerConnection", Static)!;
    private static readonly object VehicleLock = Pool.GetField("VehiclesLock", Static)!.GetValue(null)!;
    private static Harmony? _harmony;
    private static volatile object _epoch = new object();

    internal static MethodInfo FindVehicleTarget() => Engine.GetType("RageCoop.Client.Scripting.BaseScript", true)!
        .GetMethod("CreateVehicle", Instance, null, new[] { typeof(CustomEventReceivedArgs) }, null)!;
    internal static MethodInfo FindProjectileTarget() => ProjectileType.GetMethod("CreateProjectile", Instance, null, Type.EmptyTypes, null)!;
    internal static MethodInfo FindCleanupTarget() => Pool.GetMethod("Cleanup", Static, null, new[] { typeof(bool), typeof(bool) }, null)!;
    internal static MethodInfo FindRemoveTarget() => Pool.GetMethod("RemoveProjectile", Static, null, new[] { typeof(int), typeof(string) }, null)!;

    internal static void Install()
    {
        if (_harmony != null) { return; }
        MethodInfo spawn = FindVehicleTarget(), projectile = FindProjectileTarget(), cleanup = FindCleanupTarget(), remove = FindRemoveTarget();
        if (!HasSupportedContract(spawn, projectile, cleanup, remove)) { throw Unsupported("creation and cleanup IL"); }
        var harmony = new Harmony(PatchId);
        try
        {
            Patch(harmony, spawn, nameof(BeforeVehicle));
            Patch(harmony, projectile, nameof(BeforeProjectile));
            Patch(harmony, cleanup, nameof(BeforeCleanup));
            Patch(harmony, remove, nameof(BeforeRemove));
        }
        catch
        {
            foreach (MethodInfo target in Targets) { harmony.Unpatch(target, HarmonyPatchType.Prefix, PatchId); }
            Targets.Clear();
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        _epoch = new object();
        lock (PendingVehicles) { PendingVehicles.Clear(); }
        if (_harmony != null)
        {
            foreach (MethodInfo target in Targets) { _harmony.Unpatch(target, HarmonyPatchType.Prefix, PatchId); }
        }
        Targets.Clear();
        _harmony = null;
    }

    private static void Patch(Harmony harmony, MethodInfo target, string prefix)
    {
        Targets.Add(target);
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(EngineGameplayGuard), prefix));
    }

    internal static bool HasSupportedContract(MethodInfo spawn, MethodInfo projectile, MethodInfo cleanup, MethodInfo remove)
    {
        if (spawn != FindVehicleTarget() || projectile != FindProjectileTarget() || cleanup != FindCleanupTarget() || remove != FindRemoveTarget() ||
            new[] { spawn, projectile, cleanup, remove }.Any(method => method.ReturnType != typeof(void)) ||
            AddVehicle?.ReturnType != typeof(void) || AddProjectile?.ReturnType != typeof(void) || SetupVehicle?.ReturnType != typeof(void) ||
            Connection == null || Connection.FieldType.FullName != "Lidgren.Network.NetConnection") { return false; }
        // These prefixes replace complete methods, so matching only one call
        // would silently discard new behavior in another engine build.
        if (!HasOriginalBody(spawn, "ec450b370152315ea93ab2da590059f9a7e84830a7ff3a62b4f0fcf843738418") ||
            !HasOriginalBody(projectile, "789600b65303626bce99acac51a61b7d23bf1d30e91e8887a3c4f5ac490f7c38") ||
            !HasOriginalBody(cleanup, "a940f385405cdac756837396a7b2f84a94b9e3764e52fd275b6d22aab23e5871") ||
            !HasOriginalBody(remove, "6aa0d12df17833ad013dbf8171dc782f376c2071e5a24962a17883cb2662b672")) { return false; }
        var spawnCodes = PatchProcessor.GetOriginalInstructions(spawn);
        var projectileCodes = PatchProcessor.GetOriginalInstructions(projectile);
        var cleanupCodes = PatchProcessor.GetOriginalInstructions(cleanup);
        var removeCodes = PatchProcessor.GetOriginalInstructions(remove);
        return spawnCodes.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(World) && method.Name == "CreateVehicle") == 2 &&
            spawnCodes.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(System.Threading.Thread) && method.Name == "Sleep") == 1 &&
            spawnCodes.Count(code => code.Calls(AddVehicle)) == 1 && projectileCodes.Count(code => code.Calls(AddProjectile)) == 1 &&
            projectileCodes.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(World) && method.Name == "GetAllProjectiles") == 1 &&
            cleanupCodes.Count(code => code.Calls(ProjectileShooter.GetGetMethod(true)!)) == 1 &&
            removeCodes.Count(code => code.Calls(ProjectileExploded.GetGetMethod(true)!)) == 1 &&
            removeCodes.Count(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Projectile) && method.Name == "Explode") == 1;
    }

    private static bool BeforeVehicle(CustomEventReceivedArgs __0)
    {
        object[]? args = __0.Args;
        if (args == null || args.Length != 4 || args[0] is not int id || id == 0 || args[1] is not Model model ||
            args[2] is not Vector3 position || args[3] is not float heading || !Finite(position) || !Finite(heading) ||
            !API.IsOnServer || !model.IsInCdImage || !model.IsVehicle) { return false; }
        if (RuntimeEntityLookup.GetVehicleByID(id) != null) { return false; }
        var pending = new PendingVehicle(id, model, position, heading, Connection.GetValue(null), _epoch, API.LocalPlayerID, Timestamp());
        lock (PendingVehicles)
        {
            if (PendingVehicles.ContainsKey(id)) { return false; }
            PendingVehicles.Add(id, pending);
        }
        API.QueueAction(() => AdvanceVehicle(pending));
        return false;
    }

    private static bool AdvanceVehicle(PendingVehicle pending)
    {
        bool complete = true;
        try
        {
            if (!IsCurrentSession(pending) ||
                Timestamp() - pending.Started >= Stopwatch.Frequency * 10 || RuntimeEntityLookup.GetVehicleByID(pending.Id) != null) { return true; }
            if (!pending.Model.IsLoaded)
            {
                pending.Model.Request();
                complete = false;
                return false;
            }
            Vehicle? vehicle = CreateGameVehicle(pending.Model, pending.Position, pending.Heading);
            if (vehicle == null || !vehicle.Exists()) { complete = false; return false; }
            if (!IsCurrentSession(pending)) { vehicle.Delete(); return true; }
            var synced = (SyncedVehicle)Activator.CreateInstance(typeof(SyncedVehicle), Instance, null, new object[] { pending.Id }, null)!;
            typeof(SyncedVehicle).GetProperty(nameof(SyncedVehicle.MainVehicle))!.SetValue(synced, vehicle);
            typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))!.SetValue(synced, pending.OwnerId);
            InitializeVehicle(synced);
            bool registered = false;
            lock (VehicleLock)
            {
                if (IsCurrentSession(pending) && RuntimeEntityLookup.GetVehicleByID(pending.Id) == null)
                {
                    AddVehicle.Invoke(null, new object[] { synced });
                    registered = true;
                }
            }
            if (!registered) { vehicle.Delete(); }
            return true;
        }
        finally
        {
            if (complete)
            {
                lock (PendingVehicles)
                {
                    if (PendingVehicles.TryGetValue(pending.Id, out PendingVehicle current) && ReferenceEquals(current, pending)) { PendingVehicles.Remove(pending.Id); }
                }
                pending.Model.MarkAsNoLongerNeeded();
            }
        }
    }

    private static bool IsCurrentSession(PendingVehicle pending) => ReferenceEquals(pending.Epoch, _epoch) && API.IsOnServer &&
        pending.OwnerId == API.LocalPlayerID && ReferenceEquals(pending.Connection, Connection.GetValue(null));

    private static Vehicle? CreateGameVehicle(Model model, Vector3 position, float heading)
    {
        // Vehicle.Create also requests the model with a yielding timeout. The
        // caller already checked loading; this native path never waits a frame.
        if (World.VehicleCount >= World.VehicleCapacity) { return null; }
        return Entity.FromHandle(Function.Call<int>(Hash.CREATE_VEHICLE, model.Hash,
            position.X, position.Y, position.Z, heading, false, false)) as Vehicle;
    }

    private static void InitializeVehicle(SyncedVehicle vehicle)
    {
        vehicle.MainVehicle.CanPretendOccupants = false;
        SetupVehicle.Invoke(vehicle, Array.Empty<object>());
    }

    private static bool BeforeProjectile(object __instance)
    {
        if (ProjectileShooter.GetValue(__instance) is not SyncedEntity shooter || !API.IsOnServer ||
            EntityOwner.GetValue(shooter) is not Player owner) { return false; }
        Entity? source = shooter is SyncedPed ped ? ped.MainPed : (shooter as SyncedVehicle)?.MainVehicle;
        if (source == null || !source.Exists()) { return false; }
        WeaponHash hash = (WeaponHash)ProjectileWeapon.GetValue(__instance)!;
        var asset = new WeaponAsset(hash);
        if (!asset.IsLoaded) { asset.Request(); return false; }
        var synced = (SyncedEntity)__instance;
        Vector3 position = (Vector3)EntityPosition.GetValue(synced)! +
            (owner.PacketTravelTime + 0.001f * synced.LastSyncedStopWatch.ElapsedMilliseconds) * (Vector3)EntityVelocity.GetValue(shooter)!;
        Vector3 velocity = (Vector3)EntityVelocity.GetValue(synced)!;
        HashSet<int> previous = new HashSet<int>(GetProjectiles().Select(projectile => projectile.Handle));
        ShootProjectile(hash, source, position, velocity);
        Projectile? created = null;
        IDictionary handles = Projectiles("ProjectilesByHandle");
        foreach (Projectile candidate in GetProjectiles())
        {
            if (previous.Contains(candidate.Handle) || handles.Contains(candidate.Handle) || !MatchesProjectile(candidate, hash, source, position, velocity)) { continue; }
            if (created != null) { return false; }
            created = candidate;
        }
        if (created == null) { return false; }
        created.Position = position;
        created.Rotation = (Vector3)EntityRotation.GetValue(synced)!;
        created.Velocity = velocity;
        foreach (int handle in handles.Keys.Cast<int>().Where(handle => ReferenceEquals(handles[handle], synced)).ToArray()) { handles.Remove(handle); }
        ProjectileObject.SetValue(synced, created);
        AddProjectile.Invoke(null, new object[] { synced });
        return false;
    }

    private static Projectile[] GetProjectiles() => World.GetAllProjectiles();
    private static bool MatchesProjectile(Projectile projectile, WeaponHash hash, Entity source, Vector3 position, Vector3 velocity)
    {
        if (!projectile.Exists() || projectile.WeaponHash != hash) { return false; }
        Entity? owner = projectile.OwnerEntity;
        if (owner?.Handle == source.Handle) { return true; }
        if (source is Ped) { return false; }
        // SHVDN explicitly supports both vehicle and ped projectile owners. The
        // installed engine passes a vehicle handle to the shoot native; it does
        // not establish whether GTA retains it, reports a ped or no owner. The
        // unique new handle must still be at the requested creation position,
        // allowing its first physics step and the muzzle/collision adjustment.
        float frame = Game.LastFrameTime;
        if (!Finite(frame) || frame < 0) { return false; }
        float radius = 2f + velocity.Length() * frame;
        Vector3 actual = projectile.Position;
        return Finite(actual) && actual.DistanceToSquared(position) <= radius * radius;
    }
    private static void ExplodeProjectile(Projectile projectile) => projectile.Explode();

    private static void ShootProjectile(WeaponHash hash, Entity source, Vector3 position, Vector3 velocity)
    {
        Vector3 end = position + velocity;
        Function.Call((Hash)0xE3A7742E0B7A2F8B, position.X, position.Y, position.Z, end.X, end.Y, end.Z,
            0, 1, hash, source.Handle, 1, 0, -1);
    }

    private static void BeforeCleanup()
    {
        // A projectile packet can precede its shooter on another channel. Such
        // an entry is valid while connected, but cannot stop disconnect cleanup.
        IDictionary ids = Projectiles("ProjectilesByID");
        foreach (int id in ids.Keys.Cast<int>().ToArray())
        {
            object? projectile = ids[id];
            if (projectile != null && ProjectileShooter.GetValue(projectile) == null)
            {
                FindRemoveTarget().Invoke(null, new object[] { id, "Cleanup" });
            }
        }
    }

    private static bool BeforeRemove(int __0, string __1)
    {
        IDictionary ids = Projectiles("ProjectilesByID"), handles = Projectiles("ProjectilesByHandle");
        object? record = ids[__0];
        if (record == null) { return false; }
        if (ProjectileObject.GetValue(record) is Projectile projectile && ReferenceEquals(handles[projectile.Handle], record))
        {
            handles.Remove(projectile.Handle);
            if ((bool)ProjectileExploded.GetValue(record)!) { ExplodeProjectile(projectile); }
            else if (!(bool)ProjectileLocal.GetValue(record)! && projectile.Exists()) { projectile.Delete(); }
        }
        if (ReferenceEquals(ids[__0], record)) { ids.Remove(__0); }
        return false;
    }

    private static IDictionary Projectiles(string name) => (IDictionary)Pool.GetField(name, Static)!.GetValue(null)!;
    private static bool HasOriginalBody(MethodInfo method, string expected)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) { return false; }
        using (SHA256 hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
        }
    }
    private static long Timestamp() => Stopwatch.GetTimestamp();
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    private static PropertyInfo Property(Type type, string name, Type expected)
    {
        PropertyInfo? property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
        return property?.PropertyType == expected ? property : throw Unsupported(type.Name + "." + name);
    }
    private static InvalidOperationException Unsupported(string contract) => new InvalidOperationException("Unsupported RageCoop gameplay contract: " + contract + ".");

    private sealed class PendingVehicle
    {
        internal readonly int Id, OwnerId;
        internal readonly Model Model;
        internal readonly Vector3 Position;
        internal readonly float Heading;
        internal readonly object? Connection;
        internal readonly object Epoch;
        internal readonly long Started;
        internal PendingVehicle(int id, Model model, Vector3 position, float heading, object? connection, object epoch, int ownerId, long started)
        { Id = id; Model = model; Position = position; Heading = heading; Connection = connection; Epoch = epoch; OwnerId = ownerId; Started = started; }
    }
}
