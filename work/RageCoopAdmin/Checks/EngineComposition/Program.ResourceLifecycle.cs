using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using RageCoop.Core;

internal static partial class Program
{
    private static readonly List<(Hash Hash, int Id)> LifecycleSounds = new List<(Hash, int)>();
    private static int _lifecycleNextSound;
    private static bool _lifecycleFailQueue;
    private static int _lifecycleForeignCalls;

    private static void CheckResourceLifecycleComposition()
    {
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        Type resourcesType = Engine.GetType("RageCoop.Client.Scripting.Resources", true)!;
        Type util = Engine.GetType("RageCoop.Client.Util", true)!;
        FieldInfo table = typeof(API).GetField("CustomEventHandlers", Static)!;
        var handlers = (Dictionary<int, List<Action<CustomEventReceivedArgs>>>)table.GetValue(null)!;
        var original = handlers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        FieldInfo repairRegistered = Resource.GetType("RageCoopAdmin.Client.RepairSync", true)!.GetField("_registered", Static)!;
        object previousRepairRegistered = repairRegistered.GetValue(null)!;
        FieldInfo hostLogger = host.GetField("Logger", Static)!;
        object? previousLogger = hostLogger.GetValue(null);
        var logger = (Logger)FormatterServices.GetUninitializedObject(typeof(Logger));
        typeof(Logger).GetField("Buffer", Instance)!.SetValue(logger, ""); logger.LogLevel = 5;
        hostLogger.SetValue(null, logger);
        var boundary = new Harmony("friendslegacy.check.resourcelifecycle");
        Action<CustomEventReceivedArgs> foreign = LifecycleForeign;
        const int commonHash = 20774114;
        try
        {
            boundary.Patch(typeof(GTA.Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
            Assert(((ICollection)typeof(SoundIdPool).GetField("soundIdQueue", Static)!.GetValue(null)!).Count == 0,
                "The real remote sound pool starts empty in the isolated host.");
            foreach (string name in new[] { "GetSoundId", "Clear" })
                boundary.Patch(typeof(SoundIdPool).GetMethod(name, Static)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateLifecycleSounds)));
            boundary.Patch(util.GetMethod("Reload", Static)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipLifecycleReload)));
            boundary.Patch(typeof(API).GetMethod("QueueAction", Static, null, new[] { typeof(Action) }, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(BeforeLifecycleQueue)));
            API.RegisterCustomEventHandler(commonHash, foreign);
            _lifecycleForeignCalls = 0; _lifecycleNextSound = 4000; LifecycleSounds.Clear();

            WeakReference first = RunLifecycleSession(script, resourcesType, host, logger, handlers, abort: false);
            WeakReference second = RunLifecycleSession(script, resourcesType, host, logger, handlers, abort: true);
            for (int retry = 0; retry < 4 && (first.IsAlive || second.IsAlive); retry++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert(!first.IsAlive && !second.IsAlive,
                "Two real Start/Stop sessions in one AppDomain release both resource instances after queued actions drain.");

            WeakReference failed = RunFailedLifecycleStart(script, host, logger, handlers);
            for (int retry = 0; retry < 4 && failed.IsAlive; retry++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert(!failed.IsAlive, "The host's normal Stop/queued Cleanup also releases a script whose OnStart failed after event registration.");
            Assert(handlers.TryGetValue(commonHash, out List<Action<CustomEventReceivedArgs>>? remaining) && remaining.Contains(foreign),
                "Repeated stop and failed startup preserve a foreign handler sharing this resource's event hash.");
            DispatchLifecycleEvent(commonHash);
            Assert(_lifecycleForeignCalls == 1, "The preserved foreign handler still executes through the restored original dispatcher.");
            Assert(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains("friendslegacy.ragecoop.resourceevents") == true),
                "The final subscription owner removes only its dispatcher prefix.");
            Console.WriteLine("Actual resource lifecycle passed: same-domain Start/Stop, failed-start event rollback, concurrent custom-event dispatch, instance collection and native sound ID release; GTA audio and a native SHVDN host were isolated.");
        }
        finally
        {
            _lifecycleFailQueue = false;
            host.GetMethod("ClearQueuedActions", Static)!.Invoke(null, null);
            lock (handlers)
            {
                // This test restores the isolated host's pre-test handlers.
                handlers.Clear();
                foreach (var pair in original) handlers.Add(pair.Key, pair.Value.ToList());
            }
            hostLogger.SetValue(null, previousLogger);
            repairRegistered.SetValue(null, previousRepairRegistered);
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunLifecycleSession(Type script, Type resourcesType, Type host, Logger logger,
        Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers, bool abort)
    {
        object instance = Activator.CreateInstance(script)!;
        var resource = new ClientResource();
        typeof(ClientResource).GetProperty("Logger")!.SetValue(resource, logger);
        typeof(ClientResource).GetProperty("Scripts")!.SetValue(resource, new List<ClientScript> { (ClientScript)instance });
        typeof(ClientScript).GetProperty("CurrentResource")!.SetValue(instance, resource);
        object resources = Activator.CreateInstance(resourcesType, nonPublic: true)!;
        ((IList)resourcesType.GetField("LoadedResources", Instance)!.GetValue(resources)!).Add(resource);
        int before = handlers.Sum(pair => pair.Value.Count);
        ((ClientScript)instance).OnStart();
        int after = handlers.Sum(pair => pair.Value.Count);
        Assert(after - before is 12 or 14, "The real OnStart registers exactly twelve instance handlers plus at most two persistent static repair handlers.");
        int gets = abort ? 6 : 92;
        int start = LifecycleSounds.Count;
        for (int index = 0; index < gets; index++) SoundIdPool.GetSoundId();
        Assert(LifecycleSounds.Skip(start).Count(call => call.Hash == Hash.GET_SOUND_ID) == Math.Min(90, gets),
            "The real sound pool allocates at most ninety native IDs and reuses its oldest IDs.");
        if (abort)
        {
            CheckDispatchStopsItself(instance, script, handlers);
            script.GetMethod("CleanupBeforeHostAbort", Instance)!.Invoke(instance, null);
        }
        else CheckDispatchDuringLifecycleStop(instance, script, handlers);
        resourcesType.GetMethod("Unload", Instance)!.Invoke(resources, null);
        host.GetMethod("DoQueuedActions", Static)!.Invoke(null, null);
        Assert((bool)script.GetField("_cleaned", Instance)!.GetValue(instance)!, "Actual queued or host-abort cleanup completes before the next resource session.");
        ((ClientScript)instance).OnStop();
        host.GetMethod("DoQueuedActions", Static)!.Invoke(null, null);
        var allocated = LifecycleSounds.Skip(start).Where(call => call.Hash == Hash.GET_SOUND_ID).Select(call => call.Id).ToArray();
        var released = LifecycleSounds.Skip(start).Where(call => call.Hash == Hash.RELEASE_SOUND_ID).Select(call => call.Id).ToArray();
        Assert(released.OrderBy(id => id).SequenceEqual(allocated.OrderBy(id => id)) && allocated.Distinct().Count() == allocated.Length &&
            LifecycleSounds.Skip(start).Count(call => call.Hash == Hash.STOP_SOUND) == gets,
            "Actual resource unload or host-abort cleanup stops every pooled sound and releases each owned native ID exactly once.");
        Assert(((ICollection)typeof(SoundIdPool).GetField("soundIdQueue", Static)!.GetValue(null)!).Count == 0,
            "Native sound cleanup leaves the real pool empty for reconnect in the same AppDomain.");
        int staticHandlers = handlers.Sum(pair => pair.Value.Count) - before;
        Assert(staticHandlers is 0 or 2 && !handlers.Values.SelectMany(list => list).Any(handler => ReferenceEquals(handler.Target, instance)),
            "Stopped sessions retain only pre-existing foreign/base handlers and the two intentional static repair handlers.");
        return new WeakReference(instance);
    }

    private static void CheckDispatchDuringLifecycleStop(object instance, Type script,
        Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers)
    {
        const int hash = 20774999;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int ownCalls = 0;
        Action<CustomEventReceivedArgs> blocker = _ => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("dispatch boundary"); };
        API.RegisterCustomEventHandler(hash, blocker);
        object subscriptions = script.GetField("_eventSubscriptions", Instance)!.GetValue(instance)!;
        subscriptions.GetType().GetMethod("Register", Instance)!.Invoke(subscriptions,
            new object[] { hash, new Action<CustomEventReceivedArgs>(_ => ownCalls++) });
        Task dispatch = Task.Run(() => DispatchLifecycleEvent(hash));
        Assert(entered.Wait(3000), "The actual dispatcher captures a foreign and resource callback before Stop.");
        try { ((ClientScript)instance).OnStop(); }
        finally { release.Set(); }
        Assert(dispatch.Wait(3000) && ownCalls == 0 && handlers[hash].SequenceEqual(new[] { blocker }),
            "Concurrent real dispatch survives removal; its captured stopped resource callback is cancelled and foreign handlers remain.");
        lock (handlers) handlers.Remove(hash);
    }

    private static void CheckDispatchStopsItself(object instance, Type script,
        Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers)
    {
        const int hash = 20774998;
        int foreignCalls = 0, ownCalls = 0;
        Action<CustomEventReceivedArgs> foreign = _ => foreignCalls++;
        API.RegisterCustomEventHandler(hash, foreign);
        object subscriptions = script.GetField("_eventSubscriptions", Instance)!.GetValue(instance)!;
        subscriptions.GetType().GetMethod("Register", Instance)!.Invoke(subscriptions,
            new object[] { hash, new Action<CustomEventReceivedArgs>(_ => { ownCalls++; ((ClientScript)instance).OnStop(); }) });
        API.RegisterCustomEventHandler(hash, foreign);
        DispatchLifecycleEvent(hash);
        Assert(ownCalls == 1 && foreignCalls == 2 && handlers[hash].SequenceEqual(new[] { foreign, foreign }),
            "An executing resource handler can stop itself without deadlock or changing the foreign callbacks in the actual dispatch snapshot.");
        lock (handlers) handlers.Remove(hash);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunFailedLifecycleStart(Type script, Type host, Logger logger,
        Dictionary<int, List<Action<CustomEventReceivedArgs>>> handlers)
    {
        object instance = Activator.CreateInstance(script)!;
        var resource = new ClientResource(); typeof(ClientResource).GetProperty("Logger")!.SetValue(resource, logger);
        typeof(ClientScript).GetProperty("CurrentResource")!.SetValue(instance, resource);
        int before = handlers.Sum(pair => pair.Value.Count);
        _lifecycleFailQueue = true;
        try
        {
            try { ((ClientScript)instance).OnStart(); throw new InvalidOperationException("startup failure was hidden"); }
            catch (InvalidOperationException ex) when (ex.Message == "isolated resource queue failure") { }
        }
        finally { _lifecycleFailQueue = false; }
        Assert(handlers.Sum(pair => pair.Value.Count) == before,
            "Failure after the real instance registrations rolls all twelve subscriptions back before OnStart returns.");
        // Installed StartAll retains a failed script in its resource. Its host
        // guards follow the existing OnStop lifecycle; event rollback is immediate.
        ((ClientScript)instance).OnStop(); host.GetMethod("DoQueuedActions", Static)!.Invoke(null, null);
        Assert(Resource.GetType("RageCoopAdmin.Client.PassengerSeatGuard", true)!.GetField("_resourceInputBlocked", Static)!.GetValue(null) == null,
            "Actual failed-start Stop removes the passenger guard's bound resource callback.");
        return new WeakReference(instance);
    }

    private static void LifecycleForeign(CustomEventReceivedArgs _) => _lifecycleForeignCalls++;
    private static void DispatchLifecycleEvent(int hash)
    {
        Type packetType = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+CustomEvent", true)!;
        object packet = Activator.CreateInstance(packetType, Instance, null, new object?[] { null, false }, null)!;
        packetType.GetProperty("Hash")!.SetValue(packet, hash); packetType.GetProperty("Args")!.SetValue(packet, Array.Empty<object>());
        typeof(API.Events).GetMethod("InvokeCustomEventReceived", Static)!.Invoke(null, new[] { packet });
    }
    private static bool SkipLifecycleReload() => false;
    private static void BeforeLifecycleQueue() { if (_lifecycleFailQueue) throw new InvalidOperationException("isolated resource queue failure"); }
    private static IEnumerable<CodeInstruction> IsolateLifecycleSounds(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction instruction in original)
        {
            var code = new CodeInstruction(instruction);
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(Function) && method.Name == "Call")
                code.operand = typeof(Program).GetMethod(method.IsGenericMethod ? nameof(LifecycleSoundGet) : nameof(LifecycleSoundWrite), Static, null,
                    method.GetParameters().Select(parameter => parameter.ParameterType).ToArray(), null) ??
                    throw new InvalidOperationException("Unsupported sound native boundary: " + method);
            yield return code;
        }
    }
    private static int LifecycleSoundGet(Hash hash, InputArgument[] args)
    {
        Assert(hash == Hash.GET_SOUND_ID && args.Length == 0, "The installed allocator invokes GET_SOUND_ID at the native boundary.");
        int id = ++_lifecycleNextSound; LifecycleSounds.Add((hash, id)); return id;
    }
    private static int LifecycleSoundGet(Hash hash) => LifecycleSoundGet(hash, Array.Empty<InputArgument>());
    private static void LifecycleSoundWrite(Hash hash, InputArgument argument) => LifecycleSoundWrite(hash, new[] { argument });
    private static void LifecycleSoundWrite(Hash hash, InputArgument[] args)
    {
        Assert(hash is Hash.STOP_SOUND or Hash.RELEASE_SOUND_ID && args.Length == 1, "The installed pool uses only STOP_SOUND and RELEASE_SOUND_ID for its owned IDs.");
        LifecycleSounds.Add((hash, InputArgumentValue(args[0])));
    }
}
