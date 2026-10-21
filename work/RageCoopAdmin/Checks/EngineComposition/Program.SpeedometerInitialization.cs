using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static partial class Program
{
    private static void CheckSpeedometerInitialization()
    {
        Type initialization = Resource.GetType("RageCoopAdmin.Client.SpeedometerInitialization", true)!;
        Type cache = Resource.GetType("RageCoopAdmin.Client.SpeedometerTextureCache", true)!;
        int gameThread = Thread.CurrentThread.ManagedThreadId;
        string root = Path.Combine(Path.GetTempPath(), "CoopHudInitialization-" + Guid.NewGuid().ToString("N"));
        object Start(string path, Func<string, int> create) => Activator.CreateInstance(initialization, Instance, null, new object[] { path, create }, null)!;
        string DirectoryFor(string path) => Path.Combine(path, "RageCoop", "CoopControl", "Hud", "fss-1.3-v1");
        void Pump(object value) => initialization.GetMethod("Pump", Instance)!.Invoke(value, Array.Empty<object>());
        bool Completed(object value) => (int)initialization.GetField("_completed", Instance)!.GetValue(value)! != 0;
        bool Requested(object value) => initialization.GetField("_request", Instance)!.GetValue(value) != null;
        void Finish(object value, Func<int>? count = null)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool bounded = true;
            while (!Completed(value) && elapsed.Elapsed.TotalSeconds < 15)
            {
                int before = count?.Invoke() ?? 0;
                Pump(value);
                if (count != null && count() - before > 1) bounded = false;
                Thread.Sleep(1);
            }
            Assert(Completed(value), "The asynchronous HUD initialization finishes within the isolated test deadline.");
            Assert(bounded, "One initialization Tick can allocate at most one SDK texture.");
        }
        Dictionary<string, int> Textures(object value)
        {
            object?[] args = { null };
            Assert((bool)initialization.GetMethod("TryGetTextures", Instance)!.Invoke(value, args)!, "Completed HUD preparation exposes its committed texture IDs without waiting.");
            return (Dictionary<string, int>)args[0]!;
        }
        void WaitFor(Func<bool> predicate)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!predicate() && elapsed.Elapsed.TotalSeconds < 10) Thread.Sleep(1);
            Assert(predicate(), "The background file phase reaches its expected boundary.");
        }
        try
        {
            int created = 0;
            object first = Start(root, _ =>
            {
                Assert(Thread.CurrentThread.ManagedThreadId == gameThread, "Only the game thread invokes the HUD SDK.");
                return created++;
            });
            WaitFor(() => Requested(first));
            Assert(created == 0 && !Completed(first), "Background file preparation waits for an explicit game Tick before any native allocation.");
            Finish(first, () => created);
            Assert(Textures(first).Count == 23 && created == 23, "All 23 native IDs are committed by incremental initialization.");
            ((IDisposable)first).Dispose();
            object again = Start(root, _ => throw new InvalidOperationException("Unexpected duplicate SDK allocation"));
            Finish(again);
            Assert(Textures(again).Count == 23, "The background path reuses the process cache after initialization.");
            ((IDisposable)again).Dispose();

            string cancelled = root + "-cancelled";
            int cancelledCalls = 0;
            object beforeSdk = Start(cancelled, _ => cancelledCalls++);
            WaitFor(() => Requested(beforeSdk));
            ((IDisposable)beforeSdk).Dispose();
            WaitFor(() => Completed(beforeSdk));
            Pump(beforeSdk);
            string record = Path.Combine(DirectoryFor(cancelled), "texture-ids.xml");
            Assert(cancelledCalls == 0 && XDocument.Load(record).Root!.Attribute("pending") == null,
                "Stop before SDK creation revokes the request and clears only a provably unallocated pending identity.");
            object resumed = Start(cancelled, _ => cancelledCalls++);
            Finish(resumed);
            Assert(Textures(resumed).Count == 23 && cancelledCalls == 23, "A safely cancelled resource can resume without restarting GTA or duplicating textures.");
            ((IDisposable)resumed).Dispose();

            string stopped = root + "-started";
            int startedCalls = 0;
            object? afterSdk = null;
            afterSdk = Start(stopped, _ => { ((IDisposable)afterSdk!).Dispose(); return startedCalls++; });
            WaitFor(() => Requested(afterSdk));
            Pump(afterSdk);
            WaitFor(() => Completed(afterSdk));
            XElement committed = XDocument.Load(Path.Combine(DirectoryFor(stopped), "texture-ids.xml")).Root!;
            Assert(startedCalls == 1 && committed.Attribute("pending") == null && committed.Elements().Count() == 1,
                "Stop during a started SDK call still commits its returned ID before cancelling later allocations.");

            string failed = root + "-failed";
            int failures = 0;
            object faulted = Start(failed, _ => { failures++; throw new TaskCanceledException("Ambiguous SDK failure, not a cancelled queued request"); });
            Finish(faulted);
            bool rejected = false;
            try { Textures(faulted); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
            Assert(rejected && failures == 1 && XDocument.Load(Path.Combine(DirectoryFor(failed), "texture-ids.xml")).Root!.Attribute("pending") != null,
                "An SDK fault whose exception happens to be TaskCanceledException retains the ambiguous-allocation marker.");
            ((IDisposable)faulted).Dispose();

            string busy = root + "-busy";
            Directory.CreateDirectory(DirectoryFor(busy));
            var held = (Mutex)cache.GetMethod("AcquireLock", Static)!.Invoke(null, new object[] { DirectoryFor(busy) })!;
            object waiting;
            try
            {
                waiting = Start(busy, _ => throw new InvalidOperationException("SDK while cache mutex is busy"));
                Thread.Sleep(30);
                Pump(waiting);
                Assert(!Completed(waiting), "A competing cache owner delays only the worker, while the game Tick returns.");
                Stopwatch cleanup = Stopwatch.StartNew();
                ((IDisposable)waiting).Dispose();
                Assert(cleanup.Elapsed.TotalSeconds < 1, "Cleanup returns without waiting for a worker blocked on the cache mutex.");
            }
            finally { held.ReleaseMutex(); held.Dispose(); }
            WaitFor(() => Completed(waiting));
        }
        finally
        {
            foreach (string path in new[] { root, root + "-cancelled", root + "-started", root + "-busy", root + "-failed" })
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
