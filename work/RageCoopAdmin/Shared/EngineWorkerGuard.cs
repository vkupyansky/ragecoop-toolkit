using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RageCoop.Core;

namespace CoopControl.Shared;

// Worker.Stop promises to finish the current job, rather than drain the queue.
// Serialize producers with the stop transition; never hold this gate across Join.
internal static class EngineWorkerGuard
{
    private const string PatchId = "friendslegacy.ragecoop.engineworker";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConditionalWeakTable<Worker, WorkerState> States = new ConditionalWeakTable<Worker, WorkerState>();
    private static readonly FieldInfo Semaphore = NeedField("_semaphoreSlim", typeof(SemaphoreSlim), true);
    private static readonly FieldInfo ThreadField = NeedField("_workerThread", typeof(Thread), true);
    private static readonly FieldInfo Stopping = NeedField("_stopping", typeof(bool), false);
    private static readonly FieldInfo Jobs = NeedField("Jobs", typeof(ConcurrentQueue<Action>), true);
    private static readonly MethodInfo Queue = NeedMethod(nameof(Worker.QueueJob), typeof(Action));
    private static readonly MethodInfo Stop = NeedMethod(nameof(Worker.Stop));
    private static readonly MethodInfo Dispose = NeedMethod(nameof(Worker.Dispose));
    private static Harmony? harmony;

    internal static IReadOnlyList<MethodInfo> FindTargets()
    {
        AssemblyName assembly = typeof(Worker).Assembly.GetName();
        if (assembly.Name != "RageCoop.Core" || assembly.Version != new Version(0, 1, 0, 0) ||
            typeof(Worker).Module.ModuleVersionId != new Guid("6fc4fc60-4fd0-49a8-880f-1dfa630f8b58"))
            throw Unsupported("Core identity " + assembly.FullName);
        // These exact small methods and the single-consumer loop determine why
        // self-disposal is safe: after the current action, only _stopping is read.
        RequireInstructions(Queue, 9, new[] { "Enqueue", "Release" });
        RequireInstructions(Stop, 22, new[] { "QueueJob", "get_IsAlive", "Join" });
        RequireInstructions(Dispose, 6, new[] { "Stop", "Dispose" });
        Type? closure = typeof(Worker).GetNestedType("<>c__DisplayClass11_0", BindingFlags.NonPublic);
        MethodInfo? loop = closure?.GetMethod("<.ctor>b__0", Instance);
        if (loop == null || loop.GetMethodBody()?.ExceptionHandlingClauses.Count != 1)
            throw Unsupported("worker thread entry point");
        RequireInstructions(loop, 44, new[] { "set_IsBusy", "Wait", "TryDequeue", "set_IsBusy", "Invoke", "Error", "Error", "set_IsBusy" });
        List<CodeInstruction> loopCode = PatchProcessor.GetOriginalInstructions(loop);
        int invoke = loopCode.FindIndex(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Action) && method.Name == "Invoke");
        if (loopCode.Skip(invoke + 1).Any(code => code.opcode == OpCodes.Ldfld && Equals(code.operand, Semaphore)) ||
            loopCode.Count(code => code.opcode == OpCodes.Ldfld && Equals(code.operand, Stopping)) != 1 ||
            loopCode.Count(code => code.opcode == OpCodes.Ldfld && Equals(code.operand, Jobs)) != 1 ||
            !PatchProcessor.GetOriginalInstructions(Stop).Any(code => code.opcode == OpCodes.Stfld && Equals(code.operand, Stopping)))
            throw Unsupported("worker stop/consumer ordering");
        return new[] { Queue, Stop, Dispose };
    }

    internal static void Install()
    {
        if (harmony != null) { return; }
        IReadOnlyList<MethodInfo> targets = FindTargets();
        var installing = new Harmony(PatchId);
        try
        {
            installing.Patch(Queue, prefix: new HarmonyMethod(typeof(EngineWorkerGuard), nameof(BeforeQueue)));
            installing.Patch(Stop, prefix: new HarmonyMethod(typeof(EngineWorkerGuard), nameof(BeforeStop)));
            installing.Patch(Dispose, prefix: new HarmonyMethod(typeof(EngineWorkerGuard), nameof(BeforeDispose)));
            harmony = installing;
        }
        catch
        {
            foreach (MethodInfo target in targets) { installing.Unpatch(target, HarmonyPatchType.All, PatchId); }
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (harmony == null) { return; }
        foreach (MethodInfo target in new[] { Queue, Stop, Dispose }) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        harmony = null;
    }

    private static bool BeforeQueue(Worker __instance, Action work)
    {
        if (work == null) { throw new ArgumentNullException(nameof(work)); }
        WorkerState state = State(__instance);
        lock (state.Gate)
        {
            if (state.Disposed) { throw new ObjectDisposedException(nameof(Worker)); }
            if ((bool)Stopping.GetValue(__instance)!) { throw new InvalidOperationException("Cannot queue a job after the worker has stopped."); }
            // Preserve the installed active-worker queue contract. In particular,
            // maxJobs remains the semaphore limit; this is not a dropping queue.
            QueueOf(__instance).Enqueue(work);
            SemaphoreOf(__instance).Release();
        }
        return false;
    }

    private static bool BeforeStop(Worker __instance)
    {
        StopWorker(__instance, dispose: false);
        return false;
    }

    private static bool BeforeDispose(Worker __instance)
    {
        StopWorker(__instance, dispose: true);
        return false;
    }

    private static void StopWorker(Worker worker, bool dispose)
    {
        WorkerState state = State(worker);
        Thread thread = (Thread)ThreadField.GetValue(worker)!;
        bool self = ReferenceEquals(thread, Thread.CurrentThread);
        lock (state.Gate)
        {
            if (!(bool)Stopping.GetValue(worker)!)
            {
                Stopping.SetValue(worker, true);
                // A pending job already supplies the wake-up. Adding the original
                // extra sentinel there can exceed a bounded semaphore on Stop.
                if (thread.IsAlive && QueueOf(worker).IsEmpty)
                {
                    QueueOf(worker).Enqueue(NoOperation);
                    SemaphoreOf(worker).Release();
                }
            }
        }
        if (!self && thread.IsAlive) { thread.Join(); }
        lock (state.Gate)
        {
            // Here the consumer has exited, or this is that same consumer inside
            // its current action. It cannot dequeue another action after return.
            while (QueueOf(worker).TryDequeue(out _)) { }
            if (dispose && !state.Disposed)
            {
                SemaphoreOf(worker).Dispose();
                state.Disposed = true;
            }
        }
    }

    private static void NoOperation() { }
    private static WorkerState State(Worker worker) => States.GetValue(worker, _ => new WorkerState());
    private static ConcurrentQueue<Action> QueueOf(Worker worker) => (ConcurrentQueue<Action>)Jobs.GetValue(worker)!;
    private static SemaphoreSlim SemaphoreOf(Worker worker) => (SemaphoreSlim)Semaphore.GetValue(worker)!;

    private static void RequireInstructions(MethodInfo method, int count, string[] calls)
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method);
        string[] actual = code.Where(instruction => instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
            .Select(instruction => ((MethodBase)instruction.operand).Name).ToArray();
        if (code.Count != count || !actual.SequenceEqual(calls)) { throw Unsupported(method.ToString() ?? method.Name); }
    }

    private static FieldInfo NeedField(string name, Type type, bool readOnly) =>
        typeof(Worker).GetField(name, Instance) is FieldInfo field && field.FieldType == type && !field.IsStatic && field.IsInitOnly == readOnly
            ? field : throw Unsupported(name);
    private static MethodInfo NeedMethod(string name, params Type[] arguments) =>
        typeof(Worker).GetMethod(name, Instance, null, arguments, null) is MethodInfo method && method.ReturnType == typeof(void) && !method.IsStatic
            ? method : throw Unsupported(name);
    private static InvalidOperationException Unsupported(string detail) => new InvalidOperationException("Unsupported RageCoop worker contract: " + detail + ".");

    private sealed class WorkerState
    {
        internal readonly object Gate = new object();
        internal bool Disposed;
    }
}
