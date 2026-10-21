using System;
using System.Reflection;
using System.Runtime.Serialization;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        try
        {
            RejectMalformedEvents();
            ApplyAfterStreamLoadOnce();
            IgnoreOldRevisions();
            ExpirePendingRepairs();
            BoundQueueAndPreferPending();
            RetryAfterCallbackFailure();
            ClearBaseClientRepairCache();
            ExpireOwnerRequestsBeforeLateRepair();
            Console.WriteLine("Repair synchronization checks passed; GTA vehicle repair was not simulated.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RejectMalformedEvents()
    {
        var queue = new RepairSyncQueue(1_000);
        Assert(!queue.TryReceive(null, 0) &&
            !queue.TryReceive(new object[] { 1, 1L }, 0) &&
            !queue.TryReceive(new object[] { 0, 1L, 2 }, 0) &&
            !queue.TryReceive(new object[] { 1, 0L, 2 }, 0) &&
            !queue.TryReceive(new object[] { 1, 1, 2 }, 0) &&
            !queue.TryReceive(new object[] { 1, 1L, 0 }, 0) &&
            queue.Count == 0,
            "malformed and unversioned repair events are rejected");
    }

    private static void ApplyAfterStreamLoadOnce()
    {
        var queue = new RepairSyncQueue(1_000);
        Assert(queue.TryReceive(new object[] { -41, 11L, 555 }, 0), "signed RageCoop IDs are accepted");
        int attempts = 0;
        bool loaded = false;
        int loadedModel = 999;
        bool Apply(int id, int model)
        {
            attempts++;
            return id == -41 && model == loadedModel && loaded;
        }

        Assert(queue.Process(0, Apply) == 0 && attempts == 1 && queue.PendingCount == 1,
            "repair waits while the exact vehicle is absent");
        loaded = true;
        Assert(queue.Process(249, Apply) == 0 && attempts == 1,
            "pending lookup is throttled between frames");
        Assert(queue.Process(250, Apply) == 0 && attempts == 2 && queue.PendingCount == 1,
            "a reused ID with the wrong model is not repaired");
        loadedModel = 555;
        Assert(queue.Process(500, Apply) == 1 && attempts == 3 && queue.PendingCount == 0,
            "repair applies when the matching vehicle loads");
        Assert(queue.Process(750, Apply) == 0 && attempts == 3 &&
            !queue.TryReceive(new object[] { -41, 11L, 555 }, 751),
            "the same repair revision never applies twice");
    }

    private static void IgnoreOldRevisions()
    {
        var queue = new RepairSyncQueue(1_000);
        Assert(queue.TryReceive(new object[] { 5, 30L, 900 }, 0) &&
            queue.TryReceive(new object[] { 5, 31L, 901 }, 1) &&
            !queue.TryReceive(new object[] { 5, 30L, 900 }, 2),
            "a newer repair supersedes an older pending repair");
        int appliedModel = 0;
        Assert(queue.Process(3, (_, model) => { appliedModel = model; return true; }) == 1 &&
            appliedModel == 901,
            "only the latest model and revision are applied");
        Assert(queue.TryReceive(new object[] { 5, 32L, 901 }, 4) &&
            queue.Process(4, (_, _) => true) == 1,
            "a later authorized repair of the same vehicle can run");
    }

    private static void ExpirePendingRepairs()
    {
        var queue = new RepairSyncQueue(1_000);
        queue.TryReceive(new object[] { 9, 7L, 444 }, 0);
        int attempts = 0;
        Assert(queue.Process(15_000, (_, _) => { attempts++; return true; }) == 0 &&
            attempts == 0 && queue.PendingCount == 0 &&
            !queue.TryReceive(new object[] { 9, 7L, 444 }, 15_001),
            "an expired repair cannot affect a vehicle that appears much later");
        queue.Clear();
        Assert(queue.TryReceive(new object[] { 9, 7L, 444 }, 15_002),
            "disconnect or script cleanup clears revision state");
    }

    private static void BoundQueueAndPreferPending()
    {
        var queue = new RepairSyncQueue(1_000);
        for (int id = 1; id <= 128; id++)
        {
            Assert(queue.TryReceive(new object[] { id, (long)id, 444 }, id), null);
        }

        Assert(queue.Process(128, (id, _) => id == 1) == 1,
            "one completed entry is available for eviction");
        Assert(queue.TryReceive(new object[] { 129, 129L, 444 }, 129) && queue.Count == 128 &&
            queue.PendingCount == 128 &&
            !queue.TryReceive(new object[] { 2, 2L, 444 }, 129),
            "the bounded queue evicts a completed entry before a pending repair");
        Assert(queue.TryReceive(new object[] { 1, 1L, 444 }, 130),
            "an evicted revision can be received again only after leaving the bounded window");
    }

    private static void RetryAfterCallbackFailure()
    {
        var queue = new RepairSyncQueue(1_000);
        queue.TryReceive(new object[] { 70, 1L, 808 }, 0);
        bool threw = false;
        try { queue.Process(0, (_, _) => throw new InvalidOperationException("simulated disappearing vehicle")); }
        catch (InvalidOperationException) { threw = true; }
        Assert(threw && queue.PendingCount == 1 &&
            queue.Process(250, (_, _) => true) == 1,
            "a transient game failure leaves the repair pending for a bounded retry");
    }

    private static void Assert(bool condition, string? message)
    {
        if (!condition) { throw new InvalidOperationException(message ?? "Queue assertion failed."); }
        if (message != null) { Console.WriteLine("PASS " + message); }
    }

    private static void ClearBaseClientRepairCache()
    {
        Assert(VehicleRepairCache.IsAvailable, "the installed RageCoop exposes the exact repair cache contract");
        var vehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        const BindingFlags binding = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo flags = typeof(SyncedVehicle).GetProperty("Flags", binding)!;
        PropertyInfo engine = typeof(SyncedVehicle).GetProperty("EngineHealth", binding)!;
        PropertyInfo damage = typeof(SyncedVehicle).GetProperty("DamageModel", binding)!;
        PropertyInfo previousEngine = typeof(SyncedVehicle).GetProperty("LastEngineHealth", binding)!;
        ulong dead = Flag(flags, "IsDead");
        ulong fire = Flag(flags, "IsOnFire");
        ulong horn = Flag(flags, "IsHornActive");
        ulong repairedFlag = Flag(flags, "Repaired");
        flags.SetValue(vehicle, Enum.ToObject(flags.PropertyType, dead | fire | horn));
        engine.SetValue(vehicle, -50f);
        previousEngine.SetValue(vehicle, -50f);
        object broken = Activator.CreateInstance(damage.PropertyType)!;
        damage.PropertyType.GetProperty("BrokenDoors")!.SetValue(broken, (byte)255);
        damage.PropertyType.GetProperty("BrokenWindows")!.SetValue(broken, (byte)255);
        damage.PropertyType.GetProperty("BurstedTires")!.SetValue(broken, (short)127);
        damage.SetValue(vehicle, broken);

        VehicleRepairCache.Reset(vehicle, publish: true);
        object repaired = damage.GetValue(vehicle)!;
        ulong repairedFlags = Convert.ToUInt64(flags.GetValue(vehicle));
        Assert((byte)damage.PropertyType.GetProperty("BrokenDoors")!.GetValue(repaired)! == 0 &&
            (byte)damage.PropertyType.GetProperty("BrokenWindows")!.GetValue(repaired)! == 0 &&
            (short)damage.PropertyType.GetProperty("BurstedTires")!.GetValue(repaired)! == 0 &&
            (float)engine.GetValue(vehicle)! == 1000f &&
            (repairedFlags & (dead | fire)) == 0 &&
            (repairedFlags & repairedFlag) != 0 &&
            (repairedFlags & horn) != 0,
            "repair clears stale cached damage without removing unrelated vehicle flags");
        Assert(vehicle.SendNextFrame && vehicle.SendFullNextFrame && (float)previousEngine.GetValue(vehicle)! == -50f,
            "owner repair schedules a complete base snapshot and preserves repaired-bit detection");
        previousEngine.SetValue(vehicle, 1000f);
        VehicleRepairCache.Reset(vehicle, publish: true);
        Assert((float)engine.GetValue(vehicle)! > (float)previousEngine.GetValue(vehicle)!,
            "cosmetic repair with a healthy engine also causes the real outgoing repaired indication");
        vehicle.SendNextFrame = false;
        vehicle.SendFullNextFrame = false;
        VehicleRepairCache.Reset(vehicle, publish: false);
        Assert(!vehicle.SendNextFrame && !vehicle.SendFullNextFrame,
            "a passenger corrects its cache without publishing state as a false owner");
    }

    private static ulong Flag(PropertyInfo flags, string name) =>
        Convert.ToUInt64(Enum.Parse(flags.PropertyType, name));

    private static void ExpireOwnerRequestsBeforeLateRepair()
    {
        var queue = new RepairSyncQueue(1000, 5);
        queue.TryReceive(new object[] { 1, 9L, 20 }, 0);
        Assert(queue.GetRevision(1) == 9L && queue.Process(5000, (_, _) => true) == 0,
            "an expired owner request cannot repair a car after the server stopped waiting");
    }
}
