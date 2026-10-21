using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GTA;
using HarmonyLib;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string PatchId = "friendslegacy.ragecoop.enginetraffic";
    private static bool _modelValid;
    private static int _modelRequests;

    private static int Main()
    {
        try
        {
            CheckCullingPolicy();
            CheckInstalledInstructions();
            CheckCadence();
            try
            {
                Assert(EngineTrafficGuard.Install() && EngineTrafficGuard.Install(), "The verified installed engine accepts one idempotent traffic patch set.");
                CheckCreateGate();
                CheckPoolRecreation();
                CheckSkippedSend();
            }
            finally { EngineTrafficGuard.Uninstall(); }
            Assert(Harmony.GetPatchInfo(EngineTrafficGuard.FindDoSync()!)?.Owners.Contains(PatchId) != true &&
                Harmony.GetPatchInfo(EngineTrafficGuard.FindCreateVehicle()!)?.Owners.Contains(PatchId) != true,
                "Unloading removes the owned engine patches.");
            Console.WriteLine("Installed engine traffic/lifecycle checks passed; live GTA traffic was not simulated.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckCullingPolicy()
    {
        Assert(!EngineTrafficPolicy.CanCull(false, false, false, false, 100000f), "Traffic below the soft limit is retained.");
        Assert(!EngineTrafficPolicy.CanCull(true, true, false, false, 100000f), "Visible traffic ahead survives even above the soft limit.");
        Assert(!EngineTrafficPolicy.CanCull(true, false, false, false, 10000f), "Nearby traffic survives when the camera turns away.");
        Assert(!EngineTrafficPolicy.CanCull(true, false, true, false, 100000f), "Mission/persistent traffic survives.");
        Assert(!EngineTrafficPolicy.CanCull(true, false, false, true, 100000f), "Player-occupied traffic survives at any distance.");
        Assert(EngineTrafficPolicy.CanCull(true, false, false, false, 100000f), "The original population filter can still remove distant unseen ambient traffic above the cap.");
        Assert(!EngineTrafficPolicy.CanCull(true, false, false, false, float.NaN) &&
            !EngineTrafficPolicy.CanCull(true, false, false, false, float.PositiveInfinity), "Invalid distance never authorizes a deletion.");
    }

    private static void CheckInstalledInstructions()
    {
        MethodInfo sync = EngineTrafficGuard.FindDoSync() ?? throw new InvalidOperationException("Missing installed DoSync.");
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(sync);
        Assert(EngineTrafficGuard.HasCullGates(original, sync), "Both installed soft-limit comparisons and their typed candidate locals match.");
        CodeInstruction[] rewritten = EngineTrafficGuard.ProtectTraffic(original, sync).ToArray();
        Assert(rewritten.Length == original.Count + 4 && rewritten.Count(code => code.operand is MethodInfo method &&
            method.DeclaringType == typeof(EngineTrafficGuard) && (method.Name == "CanCullPed" || method.Name == "CanCullVehicle")) == 2,
            "Exactly two protection predicates are added to DoSync.");
        Assert(rewritten.Count(code => code.operand is MethodInfo method && method.Name == "get_PopulationType") ==
            original.Count(code => code.operand is MethodInfo method && method.Name == "get_PopulationType"),
            "Original ambient population rules remain in the engine.");

        MethodInfo create = EngineTrafficGuard.FindCreateVehicle() ?? throw new InvalidOperationException("Missing installed CreateVehicle.");
        List<CodeInstruction> createOriginal = PatchProcessor.GetOriginalInstructions(create);
        Assert(EngineTrafficGuard.TryFindNearbyBlock(createOriginal, out int start, out int end), "The installed destructive two-meter replacement block matches exactly.");
        CodeInstruction[] createPatched = EngineTrafficGuard.RemoveNearbyReplacement(createOriginal).ToArray();
        Assert(createPatched.Length == createOriginal.Count - (end - start) &&
            !createPatched.Any(code => code.operand is MethodInfo method && (method.Name == "GetNearbyVehicles" || method.Name == "RemoveVehicle")),
            "Creating a remote car cannot query/delete another nearby car or remove its own incoming pool record.");
        Assert(createPatched.Count(code => code.operand is MethodInfo method && method.Name == "Delete") == 1,
            "Only replacement of this record's own prior vehicle remains.");
        bool rejected = false;
        try { EngineTrafficGuard.RemoveNearbyReplacement(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "An incompatible creation method is rejected before applying a guessed edit.");
    }

    private static void CheckCadence()
    {
        foreach (int fps in new[] { 30, 60, 144 })
        {
            var schedule = new FullSnapshotSchedule();
            long lastOrdinary = -30;
            var fullTimes = new List<long>();
            for (int frame = 0; frame < fps * 4; frame++)
            {
                long now = frame * 1000L / fps;
                if (now - lastOrdinary < 30) { continue; }
                lastOrdinary = now;
                if (!schedule.IsDue(now, 500, false)) { continue; }
                schedule.Sent(now);
                fullTimes.Add(now);
            }
            Assert(fullTimes.Count >= 7 && fullTimes.Count <= 8 &&
                fullTimes.Zip(fullTimes.Skip(1), (first, second) => second - first).All(gap => gap >= 500 && gap <= 567),
                $"Full snapshots remain bounded and cannot starve at {fps} FPS despite skipped ordinary send frames.");
        }
        var requested = new FullSnapshotSchedule();
        requested.Sent(1000);
        Assert(!requested.IsDue(1100, 500, false) && requested.IsDue(1100, 500, true), "SendFullNextFrame can explicitly request an earlier complete snapshot.");
    }

    private static void CheckCreateGate()
    {
        SyncedVehicle incoming = VehicleRecord(870001, 71);
        Dictionary<int, SyncedVehicle> ids = PoolDictionary<SyncedVehicle>("VehiclesByID");
        const string modelPatchId = "friendslegacy.check.enginemodel";
        var modelPatch = new Harmony(modelPatchId);
        MethodInfo[] modelGetters = new[] { "IsInCdImage", "IsVehicle", "IsLoaded" }.Select(name =>
            typeof(Model).GetProperty(name, Instance)!.GetGetMethod()!).ToArray();
        MethodInfo request = typeof(Model).GetMethod("Request", Instance, null, Type.EmptyTypes, null)!;
        foreach (MethodInfo getter in modelGetters)
        {
            modelPatch.Patch(getter, prefix: new HarmonyMethod(typeof(Program), getter.Name == "get_IsLoaded" ? nameof(ModelLoaded) : nameof(ModelValid)));
        }
        modelPatch.Patch(request, prefix: new HarmonyMethod(typeof(Program), nameof(ModelRequested)));
        ids.Add(incoming.ID, incoming);
        try
        {
            Assert(!(bool)EngineTrafficGuard.FindCreateVehicle()!.Invoke(incoming, null)! && ReferenceEquals(ids[incoming.ID], incoming),
                "The patched real CreateVehicle returns safely before native calls until full model data arrives, keeping the incoming record.");
            typeof(SyncedEntity).GetProperty("LastFullSynced", Instance)!.SetValue(incoming, 1UL);
            typeof(SyncedEntity).GetProperty("Model", Instance)!.SetValue(incoming, new Model(1));
            Assert(!(bool)EngineTrafficGuard.FindCreateVehicle()!.Invoke(incoming, null)! && _modelRequests == 0,
                "The patched real CreateVehicle rejects an invalid full model before any deletion or request.");
            _modelValid = true;
            Assert(!(bool)EngineTrafficGuard.FindCreateVehicle()!.Invoke(incoming, null)! && _modelRequests == 1 && ReferenceEquals(ids[incoming.ID], incoming),
                "An unloaded valid model is requested without deleting the incoming record or native vehicles.");
        }
        finally
        {
            ids.Remove(incoming.ID);
            foreach (MethodInfo getter in modelGetters) { modelPatch.Unpatch(getter, HarmonyPatchType.All, modelPatchId); }
            modelPatch.Unpatch(request, HarmonyPatchType.All, modelPatchId);
        }
    }

    private static bool ModelValid(ref bool __result) { __result = _modelValid; return false; }
    private static bool ModelLoaded(ref bool __result) { __result = false; return false; }
    private static bool ModelRequested() { _modelRequests++; return false; }

    private static void CheckPoolRecreation()
    {
        var record = VehicleRecord(870002, 71);
        var unrelated = VehicleRecord(870003, 72);
        Dictionary<int, SyncedVehicle> handles = PoolDictionary<SyncedVehicle>("VehiclesByHandle");
        Dictionary<int, SyncedVehicle> ids = PoolDictionary<SyncedVehicle>("VehiclesByID");
        const int oldHandle = 901001, newHandle = 901002, unrelatedHandle = 901003;
        handles.Add(oldHandle, record);
        handles.Add(unrelatedHandle, unrelated);
        typeof(SyncedVehicle).GetProperty("MainVehicle", Instance)!.SetValue(record, NativeEntity<Vehicle>(newHandle));
        try
        {
            EngineTrafficGuard.FindAdd(typeof(SyncedVehicle))!.Invoke(null, new object[] { record });
            Assert(!handles.ContainsKey(oldHandle) && ReferenceEquals(handles[newHandle], record) && ReferenceEquals(handles[unrelatedHandle], unrelated),
                "Actual EntityPool.Add remaps a recreated car and preserves unrelated handle mappings.");
            handles.Add(oldHandle, unrelated);
            EngineTrafficGuard.FindAdd(typeof(SyncedVehicle))!.Invoke(null, new object[] { record });
            Assert(ReferenceEquals(handles[oldHandle], unrelated), "Reusing the former handle for unrelated traffic remains valid.");
        }
        finally
        {
            handles.Remove(oldHandle);
            handles.Remove(newHandle);
            handles.Remove(unrelatedHandle);
            ids.Remove(record.ID);
        }

        var ped = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(ped, 870004);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(ped, 71);
        Dictionary<int, SyncedPed> pedHandles = PoolDictionary<SyncedPed>("PedsByHandle");
        Dictionary<int, SyncedPed> pedIds = PoolDictionary<SyncedPed>("PedsByID");
        pedHandles.Add(oldHandle, ped);
        typeof(SyncedPed).GetProperty("MainPed", Instance)!.SetValue(ped, NativeEntity<Ped>(newHandle));
        try
        {
            EngineTrafficGuard.FindAdd(typeof(SyncedPed))!.Invoke(null, new object[] { ped });
            Assert(!pedHandles.ContainsKey(oldHandle) && ReferenceEquals(pedHandles[newHandle], ped), "Actual EntityPool.Add also remaps a recreated ped.");
        }
        finally
        {
            pedHandles.Remove(oldHandle);
            pedHandles.Remove(newHandle);
            pedIds.Remove(ped.ID);
        }
    }

    private static void CheckSkippedSend()
    {
        SyncedVehicle vehicle = VehicleRecord(870005, 71);
        Type network = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Networking", true)!;
        FieldInfo interval = network.GetField("SyncInterval", Static)!;
        int previous = (int)interval.GetValue(null)!;
        typeof(SyncedEntity).GetProperty("LastSentStopWatch", Instance)!.SetValue(vehicle, Stopwatch.StartNew());
        vehicle.SendFullNextFrame = true;
        try
        {
            interval.SetValue(null, int.MaxValue);
            EngineTrafficGuard.FindSend(typeof(SyncedVehicle))!.Invoke(null, new object[] { vehicle, true });
            Assert(vehicle.SendFullNextFrame, "A skipped real SendVehicle preserves an explicit full-snapshot request and returns before native access.");
        }
        finally { interval.SetValue(null, previous); }
    }

    private static SyncedVehicle VehicleRecord(int id, int owner)
    {
        var vehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(vehicle, id);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(vehicle, owner);
        return vehicle;
    }

    private static Dictionary<int, T> PoolDictionary<T>(string name) =>
        (Dictionary<int, T>)typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", true)!.GetField(name, Static)!.GetValue(null)!;

    private static T NativeEntity<T>(int handle) where T : Entity =>
        (T)typeof(T).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
        Console.WriteLine("PASS " + message);
    }
}
