using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoopControl.Shared;
using HarmonyLib;
using RageCoop.Core;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int assertions;

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--original-self-stop") { return OriginalSelfStop(); }
        try
        {
            Console.WriteLine("Core module: " + typeof(Worker).Module.ModuleVersionId);
            CheckOriginalFailures();
            IReadOnlyList<MethodInfo> targets = EngineWorkerGuard.FindTargets();
            Assert(targets.Count == 3, "all three real Worker lifecycle methods and its consumer loop match the installed IL contract");
            CheckInstallOnActiveWorker();
            CheckOrderedExecutionAndJobErrors();
            CheckIdleStop();
            CheckFinishCurrentJob();
            CheckSelfStop(false);
            CheckSelfStop(true);
            CheckConcurrentStopAndDispose();
            CheckProducerStopRace();
            CheckBoundedStop();
            CheckCollectedReferences();
            CheckUninstallWhileActive();
            Assert(targets.All(target => Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.engineworker") != true),
                "uninstall removes every Worker guard patch");
            Console.WriteLine("Installed Core Worker lifecycle checks passed: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { EngineWorkerGuard.Uninstall(); }
    }

    private static void CheckOriginalFailures()
    {
        Worker worker = CreateWorker();
        worker.Stop();
        int stoppedCount = QueueOf(worker).Count;
        worker.QueueJob(() => { });
        Assert(QueueOf(worker).Count == stoppedCount + 1, "original stopped Worker accepts and retains an action that can never execute");
        worker.Dispose();
        Assert(Throws<ObjectDisposedException>(worker.Dispose), "original repeated Dispose releases an already disposed semaphore");
        Assert(QueueOf(worker).Count > 0, "original stopping/disposal leaves abandoned actions in the real queue");

        // Run the original self-join in a child process, so this checking process
        // never needs Thread.Abort or a permanently blocked foreground worker.
        string executable;
        string arguments;
#if NET48
        executable = Assembly.GetExecutingAssembly().Location;
        arguments = "--original-self-stop";
#else
        executable = Process.GetCurrentProcess().MainModule!.FileName;
        arguments = "\"" + Assembly.GetExecutingAssembly().Location + "\" --original-self-stop";
#endif
        using var child = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the isolated original Worker self-join check.");
        if (!child.WaitForExit(10000)) { throw new TimeoutException("The isolated original Worker check did not exit."); }
        string output = child.StandardOutput.ReadToEnd();
        string error = child.StandardError.ReadToEnd();
        Assert(child.ExitCode == 0 && output.Contains("ORIGINAL SELF-JOIN CONFIRMED"), "original Stop deadlocks inside its own actual worker thread: " + error);
    }

    private static int OriginalSelfStop()
    {
        Worker worker = CreateWorker();
        using var entered = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        worker.QueueJob(() => { entered.Set(); worker.Stop(); returned.Set(); });
        if (!entered.Wait(3000) || returned.Wait(200) || !WorkerThread(worker).IsAlive) { return 1; }
        Console.WriteLine("ORIGINAL SELF-JOIN CONFIRMED");
        // A background test worker lets process exit end this known original
        // self-join; no thread abortion or interruption is used.
        return 0;
    }

    private static void CheckInstallOnActiveWorker()
    {
        Worker worker = CreateWorker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        worker.QueueJob(() =>
        {
            entered.Set();
            Wait(release, "pre-install worker release");
            worker.Dispose();
            completed.Set();
        });
        Wait(entered, "pre-install worker current job");
        try
        {
            EngineWorkerGuard.Install();
            EngineWorkerGuard.Install();
            release.Set();
            Wait(completed, "pre-install worker self-disposal");
            if (!WorkerThread(worker).Join(3000)) { throw new TimeoutException("Pre-install Worker did not finish."); }
            Assert(!WorkerThread(worker).IsAlive && QueueOf(worker).IsEmpty,
                "guard fixes self-disposal on a worker whose original consumer was already running before Install");
        }
        finally { release.Set(); DisposeWorker(worker); }
    }

    private static void CheckOrderedExecutionAndJobErrors()
    {
        Worker worker = CreateWorker();
        var order = new ConcurrentQueue<int>();
        using var finished = new ManualResetEventSlim();
        try
        {
            worker.QueueJob(() => order.Enqueue(1));
            worker.QueueJob(() => throw new InvalidOperationException("isolated job failure"));
            worker.QueueJob(() => { order.Enqueue(2); finished.Set(); });
            Wait(finished, "normal Worker queue after a failed action");
            Assert(order.SequenceEqual(new[] { 1, 2 }), "actual consumer preserves ordered actions and continues after an action error");
            Assert(Throws<ArgumentNullException>(() => worker.QueueJob(null!)), "null actions are rejected before changing the queue");
        }
        finally { DisposeWorker(worker); }
    }

    private static void CheckIdleStop()
    {
        Worker worker = CreateWorker();
        DisposeWorker(worker);
        worker.Stop();
        worker.Dispose();
        Assert(!WorkerThread(worker).IsAlive && QueueOf(worker).IsEmpty, "idle Dispose wakes and joins the real semaphore waiter, then repeated Stop/Dispose do nothing");
        Assert(Throws<ObjectDisposedException>(() => worker.QueueJob(() => { })), "QueueJob after Dispose fails without retaining its action");
        Assert(QueueOf(worker).IsEmpty, "rejected post-disposal actions leave the queue empty");
    }

    private static void CheckFinishCurrentJob()
    {
        Worker worker = CreateWorker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int currentFinished = 0, pendingExecuted = 0;
        Task? stopping = null;
        try
        {
            worker.QueueJob(() => { entered.Set(); Wait(release, "current job release"); Interlocked.Increment(ref currentFinished); });
            Wait(entered, "current job start");
            for (int index = 0; index < 100; index++) { worker.QueueJob(() => Interlocked.Increment(ref pendingExecuted)); }
            stopping = Task.Run(worker.Stop);
            WaitForStopping(worker);
            Assert(!stopping.Wait(80) && WorkerThread(worker).IsAlive, "external Stop still waits for a blocked current action; it does not report a fabricated completion");
            Assert(Throws<InvalidOperationException>(() => worker.QueueJob(() => { })), "QueueJob during external Join rejects the action without waiting on that Join");
            release.Set();
            Wait(stopping, "external Stop completion");
            Assert(currentFinished == 1 && pendingExecuted == 0 && QueueOf(worker).IsEmpty, "Stop finishes the current job and releases abandoned queued closures under its documented contract");
            Assert(Throws<InvalidOperationException>(() => worker.QueueJob(() => { })), "QueueJob after Stop rejects an unexecutable action");
        }
        finally
        {
            release.Set();
            if (stopping != null) { Wait(stopping, "cleanup Stop completion"); }
            DisposeWorker(worker);
        }
    }

    private static void CheckSelfStop(bool dispose)
    {
        Worker worker = CreateWorker();
        using var ready = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        int pendingExecuted = 0;
        try
        {
            worker.QueueJob(() =>
            {
                ready.Set();
                Wait(proceed, "self-stop release");
                if (dispose) { worker.Dispose(); worker.Dispose(); } else { worker.Stop(); worker.Stop(); }
                returned.Set();
            });
            Wait(ready, "self-stop job start");
            worker.QueueJob(() => Interlocked.Increment(ref pendingExecuted));
            proceed.Set();
            Wait(returned, dispose ? "self Dispose return" : "self Stop return");
            if (!WorkerThread(worker).Join(3000)) { throw new TimeoutException("The self-stopped consumer did not exit."); }
            Assert(pendingExecuted == 0 && QueueOf(worker).IsEmpty && !worker.IsBusy,
                (dispose ? "Dispose" : "Stop") + " inside the real worker returns, finishes its current action and stops without self-Join or retained actions");
            bool rejected = dispose ? Throws<ObjectDisposedException>(() => worker.QueueJob(() => { })) : Throws<InvalidOperationException>(() => worker.QueueJob(() => { }));
            Assert(rejected, "the self-stopped worker rejects new work using its actual lifecycle state");
        }
        finally { proceed.Set(); DisposeWorker(worker); }
    }

    private static void CheckConcurrentStopAndDispose()
    {
        for (int iteration = 0; iteration < 12; iteration++)
        {
            Worker worker = CreateWorker();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            worker.QueueJob(() => { entered.Set(); Wait(release, "concurrent Dispose release"); worker.Dispose(); });
            Wait(entered, "concurrent Dispose current job");
            Task[] stops = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                if (index % 2 == 0) { worker.Stop(); } else { worker.Dispose(); }
            })).ToArray();
            try
            {
                WaitForStopping(worker);
                release.Set();
                if (!Task.WaitAll(stops, 5000)) { throw new TimeoutException("Concurrent Stop/Dispose did not finish."); }
                worker.Dispose();
                Assert(!WorkerThread(worker).IsAlive && QueueOf(worker).IsEmpty, "concurrent external Stop/Dispose and self-Dispose share one transition and finish without semaphore errors (" + iteration + ")");
            }
            finally { release.Set(); DisposeWorker(worker); }
        }
    }

    private static void CheckProducerStopRace()
    {
        for (int iteration = 0; iteration < 12; iteration++)
        {
            Worker worker = CreateWorker();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var start = new ManualResetEventSlim();
            int executedPending = 0;
            worker.QueueJob(() => { entered.Set(); Wait(release, "producer race release"); });
            Wait(entered, "producer race current job");
            Task[] producers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                Wait(start, "producer race start");
                for (int index = 0; index < 200; index++)
                {
                    try { worker.QueueJob(() => Interlocked.Increment(ref executedPending)); }
                    catch (InvalidOperationException) { }
                }
            })).ToArray();
            Task stopping = Task.Run(() => { Wait(start, "stop race start"); worker.Dispose(); });
            try
            {
                start.Set();
                WaitForStopping(worker);
                if (!Task.WaitAll(producers, 5000)) { throw new TimeoutException("Concurrent producers did not finish while Stop waited."); }
                release.Set();
                Wait(stopping, "producer race Dispose");
                Assert(executedPending == 0 && QueueOf(worker).IsEmpty && !WorkerThread(worker).IsAlive,
                    "concurrent producers cannot add retained or executable actions past the stop boundary (" + iteration + ")");
            }
            finally { start.Set(); release.Set(); DisposeWorker(worker); }
        }
    }

    private static void CheckBoundedStop()
    {
        Worker worker = CreateWorker(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        worker.QueueJob(() => { entered.Set(); Wait(release, "bounded worker release"); });
        Wait(entered, "bounded worker current job");
        worker.QueueJob(() => throw new InvalidOperationException("pending action must not execute"));
        Task stop = Task.Run(worker.Stop);
        try
        {
            WaitForStopping(worker);
            release.Set();
            Wait(stop, "bounded Stop");
            Assert(QueueOf(worker).IsEmpty && !WorkerThread(worker).IsAlive, "Stop with a full bounded semaphore joins without adding an overflowing sentinel");
        }
        finally { release.Set(); DisposeWorker(worker); }
    }

    private static void CheckCollectedReferences()
    {
        Tuple<WeakReference, Worker> references = CreateQueuedReferences();
        Worker worker = references.Item2;
        worker.Stop();
        for (int attempt = 0; attempt < 5 && references.Item1.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert(!references.Item1.IsAlive, "stopped live Worker no longer retains a queued action's captured payload");
        worker.Dispose();
        WeakReference key = CreateDisposedWorkerReference();
        for (int attempt = 0; attempt < 5 && key.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert(!key.IsAlive, "the guard's weak lifecycle state does not retain a disposed Worker");
        GC.KeepAlive(worker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Tuple<WeakReference, Worker> CreateQueuedReferences()
    {
        Worker worker = CreateWorker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        worker.QueueJob(() => { entered.Set(); Wait(release, "captured payload release"); });
        Wait(entered, "captured payload job");
        object payload = new object();
        var weakPayload = new WeakReference(payload);
        worker.QueueJob(() => GC.KeepAlive(payload));
        Task stopping = Task.Run(worker.Stop);
        WaitForStopping(worker);
        release.Set();
        Wait(stopping, "captured payload Stop");
        return Tuple.Create(weakPayload, worker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedWorkerReference()
    {
        Worker worker = CreateWorker();
        worker.Dispose();
        return new WeakReference(worker);
    }

    private static void CheckUninstallWhileActive()
    {
        Worker worker = CreateWorker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var next = new ManualResetEventSlim();
        bool disposed = false;
        worker.QueueJob(() => { entered.Set(); Wait(release, "uninstalled worker current job"); });
        Wait(entered, "uninstall current job start");
        try
        {
            EngineWorkerGuard.Uninstall();
            EngineWorkerGuard.Uninstall();
            worker.QueueJob(next.Set);
            release.Set();
            Wait(next, "original QueueJob after guard removal");
            DisposeWorker(worker);
            disposed = true;
            Assert(!WorkerThread(worker).IsAlive,
                "Uninstall during an active job leaves the host worker running normally until its original external Dispose");
        }
        finally
        {
            release.Set();
            if (!disposed) { DisposeWorker(worker); }
        }
    }

    private static Worker CreateWorker(int maxJobs = int.MaxValue)
    {
        var logger = (Logger)Activator.CreateInstance(typeof(Logger), Instance, null, new object[] { true, false }, null)!;
        logger.LogLevel = 5;
        var worker = (Worker)Activator.CreateInstance(typeof(Worker), Instance, null, new object[] { "isolated-worker-check", logger, maxJobs }, null)!;
        WorkerThread(worker).IsBackground = true;
        return worker;
    }
    private static Thread WorkerThread(Worker worker) => (Thread)typeof(Worker).GetField("_workerThread", Instance)!.GetValue(worker)!;
    private static ConcurrentQueue<Action> QueueOf(Worker worker) => (ConcurrentQueue<Action>)typeof(Worker).GetField("Jobs", Instance)!.GetValue(worker)!;
    private static void WaitForStopping(Worker worker)
    {
        FieldInfo field = typeof(Worker).GetField("_stopping", Instance)!;
        if (!SpinWait.SpinUntil(() => (bool)field.GetValue(worker)!, 3000)) { throw new TimeoutException("Worker did not enter its real stopping state."); }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
    private static void Wait(ManualResetEventSlim signal, string label)
    {
        if (!signal.Wait(5000)) { throw new TimeoutException(label + " did not complete."); }
    }
    private static void Wait(Task task, string label)
    {
        if (!task.Wait(5000)) { throw new TimeoutException(label + " did not complete."); }
    }
    private static void DisposeWorker(Worker worker) => Wait(Task.Run(worker.Dispose), "Worker disposal");
    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        assertions++;
        Console.WriteLine("PASS " + label);
    }
}
