using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RageCoop.Core;
using RageCoop.EarlyClient;

internal static partial class EarlyClientLifecycle
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string LoggerOwner = "friendslegacy.ragecoop.earlylogger";

    private static void CheckLoggerFileGuard()
    {
        Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.LoggerFileGuard", true)!;
        MethodBase[] targets = ((IEnumerable<MethodBase>)guard.GetMethod("FindTargets", Static)!.Invoke(null, null)!).ToArray();
        Assert(targets.Length == 2 && targets.Count(target => target.IsConstructor) == 1 &&
            targets.All(target => Harmony.GetPatchInfo(target)?.Transpilers.Count(patch => patch.owner == LoggerOwner) == 1),
            "The early logger guard replaces both real Core File.Delete calls before the client logger starts.");

        string root = Path.Combine(Path.GetTempPath(), "CoopLoggerEarly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "busy.log");
        File.WriteAllText(path, "previous");
        byte[] before = File.ReadAllBytes(path);
        Logger? logger = null;
        try
        {
            using (var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                logger = (Logger)Activator.CreateInstance(typeof(Logger), Instance, null, new object[] { false, true }, null)!;
                logger.LogLevel = 0;
                logger.Name = "early-logger-check";
                logger.LogPath = path;
                Thread thread = (Thread)typeof(Logger).GetField("LoggerThread", Instance)!.GetValue(logger)!;
                thread.IsBackground = true;
                Thread.Sleep(600);
                Assert(thread.IsAlive, "A locked previous log file does not terminate the real Core logger background thread.");
                int suppressed = (int)guard.GetProperty("SuppressedDeleteCount", Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Assert(suppressed > 0, "The locked previous log file actually reached the guarded delete path.");
            }
        }
        finally
        {
            try { logger?.Dispose(); }
            finally
            {
                byte[] after = File.ReadAllBytes(path);
                Directory.Delete(root, recursive: true);
                Assert(after.SequenceEqual(before), "The guarded logger leaves the locked previous log file untouched.");
            }
        }
    }
}
