using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RageCoop.Core;

namespace RageCoop.EarlyClient;

// Core deletes the previous log file from the Logger constructor and again
// from its background thread. A busy or protected file must not terminate the
// GTA process before RageCoop can report the problem, so both original deletes
// are replaced with the same tolerant operation.
internal static class LoggerFileGuard
{
    private const string Owner = "friendslegacy.ragecoop.earlylogger";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type DisplayClass = typeof(Logger).GetNestedType("<>c__DisplayClass12_0", BindingFlags.NonPublic)
        ?? throw Unsupported("display class");
    private static readonly ConstructorInfo Constructor = typeof(Logger).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool), typeof(bool) }, null)
        ?? throw Unsupported("constructor");
    private static readonly MethodInfo ThreadBody = DisplayClass.GetMethod("<.ctor>b__0",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
        ?? throw Unsupported("background thread");
    private static readonly MethodInfo Exists = typeof(File).GetMethod(nameof(File.Exists), new[] { typeof(string) })!;
    private static readonly MethodInfo Delete = typeof(File).GetMethod(nameof(File.Delete), new[] { typeof(string) })!;
    private static readonly MethodInfo SafeDeleteMethod = typeof(LoggerFileGuard).GetMethod(nameof(SafeDelete), All)!;
    private static int _suppressedDeleteCount;
    private static Harmony? _harmony;

    internal static int SuppressedDeleteCount => Volatile.Read(ref _suppressedDeleteCount);

    internal static IReadOnlyList<MethodBase> FindTargets()
    {
        var targets = new MethodBase[] { Constructor, ThreadBody };
        foreach (MethodBase target in targets)
        {
            IReadOnlyList<CodeInstruction> codes = PatchProcessor.GetOriginalInstructions(target);
            if (codes.Count(code => code.Calls(Exists)) != 1 || codes.Count(code => code.Calls(Delete)) != 1)
                throw Unsupported(target.ToString() ?? target.Name);
        }
        return targets;
    }

    internal static void Install()
    {
        if (_harmony != null) return;
        IReadOnlyList<MethodBase> targets = FindTargets();
        var harmony = new Harmony(Owner);
        try
        {
            foreach (MethodBase target in targets)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(LoggerFileGuard), nameof(SafeDeleteCalls)));
            _harmony = harmony;
        }
        catch
        {
            foreach (MethodBase target in targets) harmony.Unpatch(target, HarmonyPatchType.All, Owner);
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (_harmony == null) return;
        foreach (MethodBase target in FindTargets()) _harmony.Unpatch(target, HarmonyPatchType.All, Owner);
        _harmony = null;
    }

    internal static IEnumerable<CodeInstruction> SafeDeleteCalls(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction code in original)
        {
            if (code.Calls(Delete))
            {
                code.opcode = OpCodes.Call;
                code.operand = SafeDeleteMethod;
            }
            yield return code;
        }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { Interlocked.Increment(ref _suppressedDeleteCount); }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _suppressedDeleteCount); }
    }

    private static InvalidOperationException Unsupported(string detail) =>
        new InvalidOperationException("Unsupported RageCoop logger file contract: " + detail + ".");
}
