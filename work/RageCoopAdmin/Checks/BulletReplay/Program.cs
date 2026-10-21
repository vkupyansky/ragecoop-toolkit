using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using CoopControl.Shared;
using GTA.Math;
using HarmonyLib;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        try
        {
            CheckVehicleHitPolicy();
            CheckOwnerLookup();
            CheckInstalledRangeGate();
            CheckAppearanceEdits();
            CheckAppearanceCache();
            Console.WriteLine("Bullet replay range checks passed; no live GTA damage was simulated.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckAppearanceEdits()
    {
        var queue = new AppearanceEditQueue();
        int first = queue.Stage(41, "old part");
        int second = queue.Stage(41, "new part");
        Assert(first != second && queue.IsStaleEcho(41, first) && queue.IsStaleEcho(41, 0) && !queue.IsStaleEcho(41, second),
            "an older appearance echo cannot undo a newer local selection");
        Assert(queue.TryTake(1000, out PendingAppearanceEdit edit) && edit.Xml == "new part" && edit.Nonce == second,
            "quick selections coalesce into the latest complete appearance");
        int third = queue.Stage(41, "last part");
        Assert(!queue.TryTake(1449, out _) && queue.TryTake(1450, out edit) && edit.Nonce == third,
            "local edits do not wait but network snapshots observe the rate limit");
        queue.Acknowledge(41, second);
        Assert(queue.IsStaleEcho(41, second), "a stale acknowledgment does not remove the pending current appearance");
        Assert(queue.TryTake(2950, out edit) && edit.Nonce == third && edit.Attempts == 2,
            "a snapshot rejected by a short server cooldown is retried with the same nonce");
        queue.Acknowledge(41, third);
        Assert(!queue.TryTake(5000, out _), "the actual acknowledgment stops retries");
        queue.Stage(42, "bounded retry");
        Assert(queue.TryTake(5000, out _) && queue.TryTake(6500, out _) && queue.TryTake(8000, out _) && queue.TryTake(9500, out _) &&
            queue.TryTake(11000, out edit) && edit.Expired && !queue.TryTake(13000, out _),
            "an unacknowledged appearance ends with an explicit failure after bounded retries");
        queue.Stage(43, "removed car");
        queue.Forget(43);
        Assert(!queue.TryTake(15000, out _), "deleting a car cancels its queued appearance");
        queue.Stage(44, "disconnected");
        queue.Clear();
        Assert(!queue.TryTake(15000, out _), "disconnect clears pending appearances");
    }

    private static void CheckAppearanceCache()
    {
        Assert(VehicleAppearanceRuntimeGuard.IsSupported, "the installed vehicle update and cosmetic cache match the guarded contract");
        var vehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        var appearance = new VehicleAppearance { ModelHash = -1216765807, Primary = 27, Secondary = 111, Plate = "FRIENDS", Livery = 3 };
        appearance.Mods[0] = 4;
        appearance.Mods[23] = 12;
        var snapshot = new AppearanceSyncSnapshot(appearance);
        VehicleAppearanceRuntimeGuard.SynchronizeCache(vehicle, snapshot);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        object? Property(string name) => typeof(SyncedVehicle).GetProperty(name, flags)!.GetValue(vehicle);
        object? Field(string name) => typeof(SyncedVehicle).GetField(name, flags)!.GetValue(vehicle);
        Assert(ReferenceEquals(Property("Colors"), Field("_lastVehicleColors")) &&
            ReferenceEquals(Property("Mods"), Field("_lastVehicleMods")) &&
            ((Dictionary<int, int>)Property("Mods")!)[23] == 12 &&
            (string)Property("LicensePlate")! == "FRIENDS" &&
            (int)Property("Livery")! == 3 && (int)Field("_lastLivery")! == 3,
            "the base full sync sees the same canonical mods, colors, plate and livery as the applied complete appearance");
        typeof(SyncedVehicle).GetProperty("Mods", flags)!.SetValue(vehicle, new Dictionary<int, int> { { 0, -1 }, { 23, -1 } });
        VehicleAppearanceRuntimeGuard.SynchronizeCache(vehicle, snapshot);
        Assert(ReferenceEquals(Property("Mods"), Field("_lastVehicleMods")) &&
            ((Dictionary<int, int>)Property("Mods")!)[0] == 4,
            "an old ordinary packet cannot replace the server-confirmed tuning cache");
        Assert(!VehicleAppearanceRuntimeGuard.NeedsRestore(vehicle, snapshot),
            "a matching canonical cache does not reapply all native tuning on each frame");
        typeof(SyncedVehicle).GetField("_lastVehicleMods", flags)!.SetValue(vehicle,
            new Dictionary<int, int> { { 0, -1 }, { 23, -1 } });
        Assert(VehicleAppearanceRuntimeGuard.NeedsRestore(vehicle, snapshot),
            "an old packet applied between the update prefix and postfix requires complete appearance restoration");
        VehicleAppearanceRuntimeGuard.SynchronizeCache(vehicle, snapshot);
        Assert(!VehicleAppearanceRuntimeGuard.NeedsRestore(vehicle, snapshot),
            "restoring the canonical cache stops unnecessary repeated appearance work");
        Assert(VehicleAppearanceRuntimeGuard.Install(_ => null, (_, _) => { }) &&
            VehicleAppearanceRuntimeGuard.Install(_ => null, (_, _) => { }),
            "the appearance guard installs against the actual vehicle update once");
        VehicleAppearanceRuntimeGuard.Uninstall();
        Assert(Harmony.GetPatchInfo(VehicleAppearanceRuntimeGuard.FindTarget()!)?.Owners.Contains("friendslegacy.ragecoop.appearance") != true,
            "appearance guard cleanup removes only its own patches and delegates");
    }

    private static void CheckVehicleHitPolicy()
    {
        Vector3 minimum = new Vector3(-1f, -2f, -0.5f);
        Vector3 maximum = new Vector3(1f, 2f, 1.5f);
        Assert(!VehicleBulletRange.NeedsVehicleCheck(50f * 50f) && VehicleBulletRange.NeedsVehicleCheck(60f * 60f),
            "the original 50 m filter remains the boundary for additional owner checks");
        Assert(VehicleBulletReplayGuard.GetRangeLimit(50f * 50f, Vector3.Zero) == 2500f,
            "ordinary nearby shots keep the original limit without reading the game world");
        Assert(VehicleBulletRange.IsVehicleHit(Vector3.Zero, minimum, maximum) &&
            VehicleBulletRange.IsVehicleHit(new Vector3(2f, 0f, 0f), minimum, maximum),
            "impact checks include the vehicle and a small interpolation margin");
        Assert(!VehicleBulletRange.IsVehicleHit(new Vector3(20f, 0f, 0f), minimum, maximum) &&
            !VehicleBulletRange.IsVehicleHit(new Vector3(float.NaN, 0f, 0f), minimum, maximum) &&
            !VehicleBulletRange.IsVehicleHit(Vector3.Zero, maximum, minimum),
            "distant points and invalid hit geometry cannot extend replay range");
    }

    private static void CheckOwnerLookup()
    {
        Assert(RuntimeEntityLookup.CanListVehicles, "the installed entity pool supports a locked vehicle snapshot");
        Type pool = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", throwOnError: true)!;
        Type main = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Main", throwOnError: true)!;
        IDictionary vehicles = (IDictionary)pool.GetField("VehiclesByID", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        FieldInfo localId = main.GetField("LocalPlayerID", BindingFlags.NonPublic | BindingFlags.Static)!;
        FieldInfo owner = typeof(SyncedEntity).GetField("_ownerID", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? previousLocalId = localId.GetValue(null);
        var local = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        var remote = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(local, -1234);
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(remote, 1234);
        owner.SetValue(local, 11);
        owner.SetValue(remote, 22);
        try
        {
            localId.SetValue(null, 11);
            vehicles.Add(-1234, local);
            vehicles.Add(1234, remote);
            Assert(RuntimeEntityLookup.GetLocalVehicles().SequenceEqual(new[] { local }),
                "only the current owner can extend range for a vehicle, including signed IDs");
            owner.SetValue(local, 22);
            Assert(RuntimeEntityLookup.GetLocalVehicles().Length == 0,
                "a transferred vehicle stops qualifying immediately");
        }
        finally
        {
            vehicles.Remove(-1234);
            vehicles.Remove(1234);
            localId.SetValue(null, previousLocalId);
        }
    }

    private static void CheckInstalledRangeGate()
    {
        MethodInfo target = VehicleBulletReplayGuard.FindTarget() ?? throw new InvalidOperationException("Installed bullet handler is missing.");
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
        int index = VehicleBulletReplayGuard.FindRangeGate(original);
        Assert(index >= 0, "the installed handler contains the verified 50 m drop condition");
        List<CodeInstruction> rewritten = VehicleBulletReplayGuard.ExtendVehicleRange(original).ToList();
        Assert(rewritten.Count == original.Count + 2 && rewritten[index].opcode == OpCodes.Dup &&
            rewritten[index + 1].opcode == OpCodes.Ldarg_1 && rewritten[index + 2].opcode == OpCodes.Call &&
            rewritten[index + 2].operand is MethodInfo hook && hook.Name == nameof(VehicleBulletReplayGuard.GetRangeLimit),
            "only the range constant is replaced with the owner vehicle decision");
        for (int offset = 0; offset < original.Count; offset++)
        {
            if (offset == index) { continue; }
            CodeInstruction actual = rewritten[offset < index ? offset : offset + 2];
            Assert(actual.opcode == original[offset].opcode && Equals(actual.operand, original[offset].operand),
                null);
        }
        Console.WriteLine("PASS every original instruction outside the range constant, including GTA shooting, remains unchanged");
        bool unsupportedRejected = false;
        try { VehicleBulletReplayGuard.ExtendVehicleRange(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
        catch (InvalidOperationException) { unsupportedRejected = true; }
        Assert(unsupportedRejected, "an incompatible handler cannot receive a guessed patch");
        try
        {
            Assert(VehicleBulletReplayGuard.Install() && VehicleBulletReplayGuard.Install(),
                "the guard installs against the actual assembly and repeated installation is harmless");
        }
        finally { VehicleBulletReplayGuard.Uninstall(); }
        Assert(Harmony.GetPatchInfo(target)?.Transpilers.All(patch => patch.owner != "friendslegacy.ragecoop.vehiclebulletrange") != false,
            "cleanup removes the owned transpiler");
    }

    private static void Assert(bool condition, string? message)
    {
        if (!condition) { throw new InvalidOperationException(message ?? "An unrelated original instruction changed."); }
        if (message != null) { Console.WriteLine("PASS " + message); }
    }
}
