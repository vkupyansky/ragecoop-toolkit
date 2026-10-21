using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

internal static partial class Program
{
    private static void CheckHostCleanupComposition()
    {
        string isolated = Path.Combine(Path.GetTempPath(), "CoopHostCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        AppDomain? domain = null;
        try
        {
            // Keep the server's SHVDN stand-in outside this domain's probing
            // path. The real ASI is inspected separately and is never executed.
            domain = AppDomain.CreateDomain("HostCleanupComposition", null,
                new AppDomainSetup { ApplicationBase = isolated });
            var boundary = (HostCleanupCompositionBoundary)domain.CreateInstanceFromAndUnwrap(
                Assembly.GetExecutingAssembly().Location, typeof(HostCleanupCompositionBoundary).FullName);
            Console.WriteLine(boundary.Run(AppDomain.CurrentDomain.BaseDirectory));
        }
        finally
        {
            if (domain != null) AppDomain.Unload(domain);
            Directory.Delete(isolated);
        }
    }
}

// The managed wrapper/domain is the isolated host boundary. Harmony and the
// resource guard are loaded from the actual built DLLs used by this check.
public sealed class HostCleanupCompositionBoundary : MarshalByRefObject
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string GuardId = "friendslegacy.ragecoop.hostcleanup";
    private static object? _logger;
    private static readonly List<string> Warnings = new List<string>();
    private static int _foreignPrefixes;

    public string Run(string binaries)
    {
        int rejectedBinds = 0;
        ResolveEventHandler resolver = (_, args) =>
        {
            var name = new AssemblyName(args.Name);
            if (name.Name == "ScriptHookVDotNet")
            {
                rejectedBinds++;
                throw new FileLoadException("A new SHVDN bind must not load the core ASI.", args.Name);
            }
            Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.FullName == args.Name);
            if (loaded != null) return loaded;
            string path = Path.Combine(binaries, name.Name + ".dll");
            return File.Exists(path) ? Assembly.Load(File.ReadAllBytes(path)) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try { return RunCore(binaries, () => rejectedBinds); }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
    }

    private static string RunCore(string binaries, Func<int> rejectedBinds)
    {
        // Reflection.Emit has no codebase beside the server's stub DLL. This
        // recreates the old unversioned binding path against a rejecting host.
        ModuleBuilder probeModule = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("FriendsLegacy.HostLookupProbe"), AssemblyBuilderAccess.Run).DefineDynamicModule("Probe");
        TypeBuilder probe = probeModule.DefineType("LegacyHostLookup", TypeAttributes.Public);
        MethodBuilder lookup = probe.DefineMethod("Lookup", MethodAttributes.Public | MethodAttributes.Static, typeof(Type), Type.EmptyTypes);
        ILGenerator lookupIl = lookup.GetILGenerator();
        lookupIl.Emit(OpCodes.Ldstr, "SHVDN.ScriptDomain, ScriptHookVDotNet");
        lookupIl.Emit(OpCodes.Ldc_I4_0);
        lookupIl.Emit(OpCodes.Call, typeof(Type).GetMethod("GetType", new[] { typeof(string), typeof(bool) })!);
        lookupIl.Emit(OpCodes.Ret);
        bool reproduced = false;
        try { probe.CreateType()!.GetMethod("Lookup")!.Invoke(null, null); }
        catch (TargetInvocationException ex) when (ex.InnerException is FileLoadException) { reproduced = true; }
        Require(reproduced && rejectedBinds() > 0, "The former unversioned lookup reproduces FileLoadException at the resolver boundary.");
        int initialBinds = rejectedBinds();

        Assembly resource = Assembly.Load(File.ReadAllBytes(Path.Combine(binaries, "RageCoopAdmin.Client.dll")));
        Type guard = resource.GetType("RageCoopAdmin.Client.HostAbortCleanupGuard", true)!;
        MethodInfo install = guard.GetMethod("Install", Static)!, uninstall = guard.GetMethod("Uninstall", Static)!;
        bool Install(Action cleanup) => (bool)install.Invoke(null, new object[] { cleanup })!;
        void Uninstall() => uninstall.Invoke(null, null);
        int cleanups = 0;
        Require(!Install(() => cleanups++) && rejectedBinds() == initialBinds,
            "With no loaded core, the resource reports the unavailable host without starting assembly resolution.");

        ModuleBuilder module = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("ScriptHookVDotNet") { Version = new Version(3, 7, 0, 188) },
            AssemblyBuilderAccess.Run).DefineDynamicModule("ManagedHostBoundary");
        Type main = module.DefineType("RageCoop.Client.Main", TypeAttributes.Public).CreateType()!;
        TypeBuilder wrapperBuilder = module.DefineType("SHVDN.Script", TypeAttributes.Public | TypeAttributes.Sealed);
        FieldBuilder instance = wrapperBuilder.DefineField("Instance", typeof(object), FieldAttributes.Public);
        PropertyBuilder scriptInstance = wrapperBuilder.DefineProperty("ScriptInstance", PropertyAttributes.None, typeof(object), Type.EmptyTypes);
        MethodBuilder instanceGetter = wrapperBuilder.DefineMethod("get_ScriptInstance", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(object), Type.EmptyTypes);
        ILGenerator instanceIl = instanceGetter.GetILGenerator();
        instanceIl.Emit(OpCodes.Ldarg_0); instanceIl.Emit(OpCodes.Ldfld, instance); instanceIl.Emit(OpCodes.Ret);
        scriptInstance.SetGetMethod(instanceGetter);
        FieldBuilder aborted = wrapperBuilder.DefineField("Aborted", typeof(Action), FieldAttributes.Public);
        FieldBuilder abortCalls = wrapperBuilder.DefineField("AbortCalls", typeof(int), FieldAttributes.Public);
        MethodBuilder abortBuilder = wrapperBuilder.DefineMethod("Abort", MethodAttributes.Public, typeof(void), Type.EmptyTypes);
        ILGenerator abortIl = abortBuilder.GetILGenerator();
        abortIl.Emit(OpCodes.Ldarg_0); abortIl.Emit(OpCodes.Dup); abortIl.Emit(OpCodes.Ldfld, abortCalls);
        abortIl.Emit(OpCodes.Ldc_I4_1); abortIl.Emit(OpCodes.Add); abortIl.Emit(OpCodes.Stfld, abortCalls);
        abortIl.Emit(OpCodes.Ldarg_0); abortIl.Emit(OpCodes.Ldfld, aborted);
        abortIl.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke")!); abortIl.Emit(OpCodes.Ret);
        Type wrapper = wrapperBuilder.CreateType()!;
        TypeBuilder domainBuilder = module.DefineType("SHVDN.ScriptDomain", TypeAttributes.Public | TypeAttributes.Sealed);
        FieldBuilder executing = domainBuilder.DefineField("Current", typeof(object), FieldAttributes.Public | FieldAttributes.Static);
        PropertyBuilder executingProperty = domainBuilder.DefineProperty("ExecutingScript", PropertyAttributes.None, typeof(object), Type.EmptyTypes);
        MethodBuilder executingGetter = domainBuilder.DefineMethod("get_ExecutingScript", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(object), Type.EmptyTypes);
        ILGenerator executingIl = executingGetter.GetILGenerator();
        executingIl.Emit(OpCodes.Ldsfld, executing); executingIl.Emit(OpCodes.Ret);
        executingProperty.SetGetMethod(executingGetter);
        Type hostDomain = domainBuilder.CreateType()!;
        FieldInfo current = hostDomain.GetField("Current")!;
        MethodInfo abort = wrapper.GetMethod("Abort")!;
        object Host(object script, Action onAbort)
        {
            object host = Activator.CreateInstance(wrapper)!;
            wrapper.GetField("Instance")!.SetValue(host, script);
            wrapper.GetField("Aborted")!.SetValue(host, onAbort);
            return host;
        }
        void Abort(object host) => abort.Invoke(host, null);

        var foreign = new Harmony("friendslegacy.check.hostcleanupboundary");
        try
        {
            current.SetValue(null, new object());
            Require(!Install(() => cleanups++), "A loaded core with a non-SHVDN wrapper is rejected.");
            current.SetValue(null, Host(new object(), () => { }));
            Require(!Install(() => cleanups++), "A SHVDN wrapper belonging to another script is rejected.");

            bool earlierFailed = false, appendedRan = false;
            Action earlier = () => throw new InvalidOperationException("Earlier host Aborted handler failed.");
            Action appended = () => appendedRan = true;
            Action handlers = earlier + appended;
            object host = Host(Activator.CreateInstance(main)!, () =>
            {
                Require(cleanups == 1, "Cleanup precedes the host's Aborted handlers.");
                try { handlers(); }
                catch (InvalidOperationException) { earlierFailed = true; }
            });
            object other = Host(Activator.CreateInstance(main)!, () => { });
            current.SetValue(null, host);
            foreign.Patch(abort, prefix: new HarmonyMethod(typeof(HostCleanupCompositionBoundary), nameof(ForeignPrefix)));
            Require(Install(() => cleanups++) && Install(() => cleanups += 100), "Repeated installation retains the captured cleanup without duplicating its prefix.");
            Require(Harmony.GetPatchInfo(abort)!.Prefixes.Count(patch => patch.owner == GuardId) == 1,
                "The actual resource installs exactly one Abort prefix.");
            Abort(other);
            Require(cleanups == 0, "Another wrapper of the same host script cannot trigger the captured cleanup.");
            Abort(host);
            Require(cleanups == 1 && earlierFailed && !appendedRan && (int)wrapper.GetField("AbortCalls")!.GetValue(host)! == 1,
                "Cleanup runs even when the earlier Aborted handler prevents a later event subscriber from running.");

            Uninstall(); Uninstall();
            Require(Harmony.GetPatchInfo(abort)?.Prefixes.Any(patch => patch.owner == GuardId) != true &&
                Harmony.GetPatchInfo(abort)?.Owners.Contains(foreign.Id) == true,
                "Repeated uninstall removes only the resource's prefix and retains another owner's patch.");
            int oldCalls = 0, newCalls = 0;
            wrapper.GetField("Aborted")!.SetValue(host, new Action(() => oldCalls++));
            Abort(host);
            Require(cleanups == 1 && oldCalls == 1, "An unloaded resource no longer receives host aborts.");

            // Isolate only logging; the resource's real exception handling and
            // the synthetic Abort body must both execute for this failure case.
            Assembly engine = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "RageCoop.Client") ??
                Assembly.Load(File.ReadAllBytes(Path.Combine(binaries, "RageCoop.Client.dll")));
            Type api = engine.GetType("RageCoop.Client.Scripting.API", true)!;
            MethodInfo loggerGetter = api.GetProperty("Logger", Static)!.GetGetMethod()!;
            Type loggerType = loggerGetter.ReturnType;
            _logger = FormatterServices.GetUninitializedObject(loggerType);
            foreign.Patch(loggerGetter, transpiler: new HarmonyMethod(typeof(HostCleanupCompositionBoundary), nameof(LoggerGetter)));
            foreign.Patch(loggerType.GetMethod("Warning", new[] { typeof(string) })!,
                prefix: new HarmonyMethod(typeof(HostCleanupCompositionBoundary), nameof(Warning)));
            object replacement = Host(Activator.CreateInstance(main)!, () => newCalls++);
            current.SetValue(null, replacement);
            Require(Install(() => { cleanups++; throw new InvalidOperationException("Cleanup failure"); }),
                "The actual guard can capture the next wrapper after reload.");
            Abort(host);
            Require(cleanups == 1 && oldCalls == 2, "Reload does not reconnect the old wrapper to the new cleanup.");
            Abort(replacement);
            Require(cleanups == 2 && newCalls == 1 && Warnings.Count == 1 && Warnings[0].Contains("InvalidOperationException"),
                "A throwing cleanup is reported once and cannot block the host's Abort body.");
            Require(rejectedBinds() == initialBinds && _foreignPrefixes == 5,
                "All installation, abort and reload paths avoid new core binding and preserve the unrelated prefix.");
            return "Host cleanup composition passed: old lookup FileLoadException reproduced; loaded-core binding, wrong host, Abort ordering, exception, reload and scoped unpatch verified without loading the native ASI.";
        }
        finally
        {
            Uninstall();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(foreign.Id) == true).ToArray())
                foreign.Unpatch(method, HarmonyPatchType.All, foreign.Id);
            current.SetValue(null, null);
            _logger = null;
        }
    }

    private static object Logger() => _logger!;

    private static IEnumerable<CodeInstruction> LoggerGetter(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        yield return new CodeInstruction(OpCodes.Call, typeof(HostCleanupCompositionBoundary).GetMethod(nameof(Logger), Static)!);
        yield return new CodeInstruction(OpCodes.Castclass, ((MethodInfo)__originalMethod).ReturnType);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static bool Warning(string __0) { Warnings.Add(__0); return false; }
    private static void ForeignPrefix() => _foreignPrefixes++;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
