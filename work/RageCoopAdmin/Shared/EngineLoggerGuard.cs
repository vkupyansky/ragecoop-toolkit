using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RageCoop.Core;

namespace CoopControl.Shared;

// Buffer changes on every append and flush. Preserve the installed formatting
// and output paths, but give all its existing monitor blocks one stable gate.
internal static class EngineLoggerGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginelogger";
    internal const int MaximumBufferCharacters = 1024 * 1024;
    internal const string TruncationMarker = "[WRN] Diagnostic buffer limit reached; older messages removed.\r\n";
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly ConditionalWeakTable<Logger, object> Gates = new ConditionalWeakTable<Logger, object>();
    private static readonly FieldInfo Buffer = typeof(Logger).GetField("Buffer", Methods)!;
    private static readonly MethodInfo Enter = typeof(Monitor).GetMethod(nameof(Monitor.Enter), new[] { typeof(object), typeof(bool).MakeByRefType() })!;
    private static readonly MethodInfo Exit = typeof(Monitor).GetMethod(nameof(Monitor.Exit), new[] { typeof(object) })!;
    private static readonly List<MethodInfo> Patched = new List<MethodInfo>();
    private static Harmony? harmony;

    internal static IReadOnlyList<MethodInfo> FindTargets()
    {
        AssemblyName assembly = typeof(Logger).Assembly.GetName();
        if (assembly.Name != "RageCoop.Core" || assembly.Version != new Version(0, 1, 0, 0) ||
            Buffer == null || Buffer.FieldType != typeof(string) || Buffer.IsStatic)
            throw Unsupported("Core identity " + assembly.FullName);
        MethodInfo[] targets =
        {
            NeedMethod(nameof(Logger.Info), typeof(string)),
            NeedMethod(nameof(Logger.Warning), typeof(string)),
            NeedMethod(nameof(Logger.Error), typeof(string)),
            NeedMethod(nameof(Logger.Error), typeof(string), typeof(Exception)),
            NeedMethod(nameof(Logger.Error), typeof(Exception)),
            NeedMethod(nameof(Logger.Debug), typeof(string)),
            NeedMethod(nameof(Logger.Trace), typeof(string)),
            NeedMethod(nameof(Logger.Flush))
        };
        foreach (MethodInfo target in targets) { FindLockRead(PatchProcessor.GetOriginalInstructions(target), target); }
        return targets;
    }

    internal static void Install()
    {
        if (harmony != null) { return; }
        IReadOnlyList<MethodInfo> targets = FindTargets();
        var installing = new Harmony(PatchId);
        try
        {
            foreach (MethodInfo target in targets)
            {
                Patched.Add(target);
                var trim = new HarmonyMethod(typeof(EngineLoggerGuard), nameof(TrimBuffer));
                installing.Patch(target, prefix: target.Name == nameof(Logger.Flush) ? trim : null,
                    postfix: target.Name == nameof(Logger.Flush) ? null : trim,
                    transpiler: new HarmonyMethod(typeof(EngineLoggerGuard), nameof(UseStableGate)));
            }
            harmony = installing;
        }
        catch
        {
            foreach (MethodInfo target in Patched) { installing.Unpatch(target, HarmonyPatchType.All, PatchId); }
            Patched.Clear();
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (harmony == null) { return; }
        foreach (MethodInfo target in Patched) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        Patched.Clear();
        harmony = null;
    }

    internal static IEnumerable<CodeInstruction> UseStableGate(IEnumerable<CodeInstruction> original, ILGenerator generator, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        int read = FindLockRead(codes, __originalMethod);
        // The original sync-object local is typed string. A separate object
        // local keeps the rewritten IL valid on both .NET Framework and .NET.
        LocalBuilder gate = generator.DeclareLocal(typeof(object));
        codes[read].opcode = OpCodes.Call;
        codes[read].operand = typeof(EngineLoggerGuard).GetMethod(nameof(GetGate), Methods)!;
        codes[read + 1].opcode = OpCodes.Stloc;
        codes[read + 1].operand = gate;
        foreach (CodeInstruction code in codes)
            if (code.opcode == OpCodes.Ldloc_0) { code.opcode = OpCodes.Ldloc; code.operand = gate; }
        return codes;
    }

    private static int FindLockRead(IList<CodeInstruction> codes, MethodBase method)
    {
        int[] enters = Enumerable.Range(0, codes.Count).Where(index => codes[index].Calls(Enter)).ToArray();
        int[] exits = Enumerable.Range(0, codes.Count).Where(index => codes[index].Calls(Exit)).ToArray();
        int[] reads = Enumerable.Range(0, codes.Count).Where(index => codes[index].opcode == OpCodes.Ldfld && Equals(codes[index].operand, Buffer)).ToArray();
        int stores = codes.Count(code => code.opcode == OpCodes.Stfld && Equals(code.operand, Buffer));
        bool flush = method.Name == nameof(Logger.Flush);
        IList<LocalVariableInfo>? locals = method.GetMethodBody()?.LocalVariables;
        int read = reads.Length == 0 ? -1 : reads[0];
        if (locals == null || locals.Count < 2 || locals[0].LocalType != typeof(string) || locals[1].LocalType != typeof(bool) ||
            enters.Length != 1 || exits.Length != 1 || reads.Length != (flush ? 4 : 2) || stores != (flush ? 2 : 1) ||
            read < 1 || read + 6 >= codes.Count || enters[0] != read + 6 ||
            codes[read - 1].opcode != OpCodes.Ldarg_0 || codes[read + 1].opcode != OpCodes.Stloc_0 ||
            codes[read + 2].opcode != OpCodes.Ldc_I4_0 || codes[read + 3].opcode != OpCodes.Stloc_1 ||
            codes[read + 4].opcode != OpCodes.Ldloc_0 || codes[read + 5].opcode != OpCodes.Ldloca_S ||
            LocalIndex(codes[read + 5].operand) != 1 || exits[0] < 1 || codes[exits[0] - 1].opcode != OpCodes.Ldloc_0 ||
            codes.Count(code => code.opcode == OpCodes.Stloc_0) != 1 || codes.Count(code => code.opcode == OpCodes.Ldloc_0) != 2)
            throw Unsupported(method.ToString() ?? method.Name);
        return read;
    }

    private static int LocalIndex(object? local) => local is LocalBuilder builder ? builder.LocalIndex :
        local is LocalVariableInfo info ? info.LocalIndex : local is byte index ? index : local is int number ? number : -1;

    private static object GetGate(Logger logger) => Gates.GetValue(logger, _ => new object());

    private static void TrimBuffer(Logger __instance)
    {
        lock (GetGate(__instance))
        {
            string current = (string)Buffer.GetValue(__instance)!;
            if (current.Length <= MaximumBufferCharacters) { return; }
            // Flush deliberately retains text when its file is unavailable. A
            // permanent IO failure must not turn diagnostics into an unbounded
            // string queue; retain recent complete lines where possible.
            int start = current.Length - (MaximumBufferCharacters - TruncationMarker.Length);
            int newline = current.IndexOf('\n', start);
            if (newline >= 0 && newline + 1 < current.Length) { start = newline + 1; }
            Buffer.SetValue(__instance, TruncationMarker + current.Substring(start));
        }
    }

    private static MethodInfo NeedMethod(string name, params Type[] arguments) =>
        typeof(Logger).GetMethod(name, Methods, null, arguments, null) is MethodInfo method && method.ReturnType == typeof(void) && !method.IsStatic
            ? method : throw Unsupported(name);

    private static InvalidOperationException Unsupported(string detail) =>
        new InvalidOperationException("Unsupported RageCoop logger contract: " + detail + ".");
}
