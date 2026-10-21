using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoopControl.Shared;
using HarmonyLib;
using RageCoop.Core;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main()
    {
        try
        {
            IReadOnlyList<MethodInfo> targets = EngineLoggerGuard.FindTargets();
            Assert(targets.Count == 8, "all eight real Core logger monitor blocks match the guarded version and IL contract");
            EngineLoggerGuard.Install();
            EngineLoggerGuard.Install();
            Assert(Harmony.GetPatchInfo(typeof(Logger).GetMethod(nameof(Logger.Dispose))!)?.Owners.Contains("friendslegacy.ragecoop.enginelogger") != true,
                "Dispose retains the original join without holding the logger monitor");
            CheckAppendDuringFlush();
            CheckConcurrentFileOutput();
            CheckImmediateNestedErrors();
            CheckDisposeFlush();
            CheckBoundedFailedOutput();
            EngineLoggerGuard.Uninstall();
            EngineLoggerGuard.Uninstall();
            Assert(targets.All(target => Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.enginelogger") != true),
                "uninstall removes every logger guard patch");
            Console.WriteLine("Installed Core logger concurrency and lifecycle checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { EngineLoggerGuard.Uninstall(); }
    }

    private static void CheckAppendDuringFlush()
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        using var boundary = new BlockingException();
        Logger logger = CreateLogger(true);
        logger.UseConsole = true;
        try
        {
            Console.SetOut(output);
            // Error replaces Buffer before calling exception.ToString while
            // still holding its monitor. The original string lock now differs
            // from the object selected by another append or Flush.
            Task error = Task.Run(() => logger.Error("before-flush", boundary));
            if (!boundary.Entered.Wait(3000)) { throw new TimeoutException("The actual Error did not reach its exception boundary."); }
            using var appendStarted = new ManualResetEventSlim();
            using var flushStarted = new ManualResetEventSlim();
            Task flush = Task.Run(() => { flushStarted.Set(); logger.Flush(); });
            Task append = Task.Run(() => { appendStarted.Set(); logger.Info("during-flush"); });
            bool blocked = appendStarted.Wait(3000) && flushStarted.Wait(3000) && !append.Wait(70) && !flush.Wait(70);
            boundary.Release.Set();
            if (!Task.WaitAll(new[] { error, flush, append }, 3000)) { throw new TimeoutException("The actual logger monitor did not release."); }
            logger.Flush();
            StopLogger(logger);
            string captured = output.ToString();
            Console.SetOut(previous);
            Assert(blocked, "actual append and Flush both wait for an Error monitor after Error has replaced Buffer");
            Assert(Count(captured, "before-flush") == 1 && Count(captured, "during-flush") == 1,
                "the controlled append-versus-flush interleaving preserves both messages exactly once");
        }
        finally
        {
            boundary.Release.Set();
            StopLogger(logger);
            Console.SetOut(previous);
        }
    }

    private static void CheckConcurrentFileOutput()
    {
        string directory = NewDirectory();
        Logger logger = CreateLogger(false);
        logger.LogPath = Path.Combine(directory, "concurrent.log");
        const int writers = 8, each = 350;
        try
        {
            using var start = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            Task flush = Task.Run(() => { start.Wait(); while (!finished.IsSet) { logger.Flush(); Thread.Yield(); } });
            Task[] writing = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
            {
                start.Wait();
                for (int index = 0; index < each; index++)
                {
                    string message = "token:" + (writer * each + index);
                    switch (index % 5)
                    {
                        case 0: logger.Info(message); break;
                        case 1: logger.Warning(message); break;
                        case 2: logger.Error(message); break;
                        case 3: logger.Debug(message); break;
                        default: logger.Trace(message); break;
                    }
                }
            })).ToArray();
            start.Set();
            try
            {
                if (!Task.WaitAll(writing, 10000)) { throw new TimeoutException("Concurrent installed logger writes did not finish."); }
            }
            finally { finished.Set(); }
            if (!flush.Wait(3000)) { throw new TimeoutException("The concurrent logger flusher did not finish."); }
            StopLogger(logger);
            string text = File.ReadAllText(logger.LogPath);
            int[] ids = Regex.Matches(text, @"token:(\d+)").Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            Assert(ids.Length == writers * each && ids.Distinct().Count() == writers * each && ids.OrderBy(id => id).SequenceEqual(Enumerable.Range(0, writers * each)),
                "eight concurrent real appenders plus manual and background Flush preserve every file message without loss or duplication");
            Assert(new[] { "[INF]", "[WRN]", "[ERR]", "[DBG]", "[TRC]" }.All(text.Contains),
                "the installed logger keeps its original severity formatting across all string overloads");
        }
        finally { StopLogger(logger); Directory.Delete(directory, recursive: true); }
    }

    private static void CheckImmediateNestedErrors()
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        Logger logger = CreateLogger(true);
        logger.UseConsole = true;
        try
        {
            Console.SetOut(output);
            Task writing = Task.Run(() =>
            {
                logger.Error("error-context", new InvalidOperationException("nested-error-A"));
                logger.Error(new InvalidOperationException("nested-error-B"));
            });
            if (!writing.Wait(3000)) { throw new TimeoutException("Error -> Trace -> immediate Flush lost monitor reentrancy."); }
            string text = output.ToString();
            Console.SetOut(previous);
            Assert(Count(text, "error-context") == 1 && Count(text, "nested-error-A") == 2 && Count(text, "nested-error-B") == 2 && Count(text, "[TRC]") == 2,
                "both real exception overloads preserve nested Trace and FlushImmediately output with a reentrant monitor");
        }
        finally { StopLogger(logger); Console.SetOut(previous); }
    }

    private static void CheckDisposeFlush()
    {
        string directory = NewDirectory();
        Logger logger = CreateLogger(false);
        logger.LogPath = Path.Combine(directory, "shutdown.log");
        try
        {
            logger.Info("dispose-final-message");
            StopLogger(logger);
            Assert(!LoggerThread(logger).IsAlive && Count(File.ReadAllText(logger.LogPath), "dispose-final-message") == 1,
                "the real Dispose joins its background thread and its final Flush completes without a monitor deadlock");
        }
        finally { StopLogger(logger); Directory.Delete(directory, recursive: true); }
    }

    private static Logger CreateLogger(bool immediate)
    {
        var logger = (Logger)Activator.CreateInstance(typeof(Logger), Instance, null, new object[] { immediate, false }, null)!;
        logger.LogLevel = 0;
        logger.Name = "check";
        // A failed deadlock assertion must not leave this isolated check alive.
        if (!immediate) { LoggerThread(logger).IsBackground = true; }
        return logger;
    }

    private static void CheckBoundedFailedOutput()
    {
        string directory = NewDirectory();
        Logger logger = CreateLogger(true);
        logger.LogPath = directory; // A directory is a deterministic unavailable file sink.
        try
        {
            string payload = new('x', 8192);
            for (int index = 0; index < 180; index++) { logger.Info("failed-sink:" + index + ":" + payload); }
            logger.Info("latest-diagnostic");
            string pending = (string)typeof(Logger).GetField("Buffer", Instance)!.GetValue(logger)!;
            Assert(pending.Length <= EngineLoggerGuard.MaximumBufferCharacters && pending.Contains("latest-diagnostic", StringComparison.Ordinal) &&
                pending.StartsWith(EngineLoggerGuard.TruncationMarker, StringComparison.Ordinal),
                "a permanently unavailable log file retains bounded recent diagnostics and an explicit truncation marker");
            logger.LogPath = Path.Combine(directory, "recovered.log");
            logger.Flush();
            string recovered = File.ReadAllText(logger.LogPath);
            Assert(recovered == pending && (string)typeof(Logger).GetField("Buffer", Instance)!.GetValue(logger)! == string.Empty,
                "the real logger writes the retained diagnostics once when its file becomes available again");
        }
        finally { StopLogger(logger); Directory.Delete(directory, recursive: true); }
    }

    private static Thread LoggerThread(Logger logger) => (Thread)typeof(Logger).GetField("LoggerThread", Instance)!.GetValue(logger)!;
    private static void StopLogger(Logger logger)
    {
        if (!Task.Run(logger.Dispose).Wait(3000)) { throw new TimeoutException("The installed logger Dispose did not join within three seconds."); }
    }
    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ragecoop-logger-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static int Count(string text, string token) => Regex.Matches(text, Regex.Escape(token)).Count;
    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        Console.WriteLine("PASS " + label);
    }

    private sealed class BlockingException : Exception, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal BlockingException() : base("isolated error boundary") { }
        public override string ToString()
        {
            Entered.Set();
            if (!Release.Wait(5000)) { throw new TimeoutException("The isolated logger exception barrier was not released."); }
            return "isolated exception trace";
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
