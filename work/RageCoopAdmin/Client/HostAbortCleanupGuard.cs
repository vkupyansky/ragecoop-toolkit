using System;
using System.Reflection;
using HarmonyLib;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

// RageCoop queues resource cleanup even when its own SHVDN script is aborting.
// Capture the actual host wrapper during Initialize, before that queue can stop.
internal static class HostAbortCleanupGuard
{
    private const string PatchId = "friendslegacy.ragecoop.hostcleanup";
    private static Harmony? _harmony;
    private static MethodInfo? _abort;
    private static object? _host;
    private static Action? _cleanup;

    internal static bool Install(Action cleanup)
    {
        if (_harmony != null) return true;
        // The core is already loaded from ScriptHookVDotNet.asi. A new
        // unversioned bind enters SHVDN's API resolver instead of that core.
        PropertyInfo? executingScript = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "ScriptHookVDotNet") continue;
            executingScript = assembly.GetType("SHVDN.ScriptDomain", throwOnError: false)?
                .GetProperty("ExecutingScript", BindingFlags.Public | BindingFlags.Static);
            if (executingScript != null) break;
        }
        object? host = executingScript?.GetValue(null);
        if (host == null || host.GetType().FullName != "SHVDN.Script") return false;
        object? instance = host.GetType().GetProperty("ScriptInstance", BindingFlags.Public | BindingFlags.Instance)?.GetValue(host);
        if (instance?.GetType().FullName != "RageCoop.Client.Main") return false;
        MethodInfo? abort = host.GetType().GetMethod("Abort", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        MethodInfo? prefix = AccessTools.DeclaredMethod(typeof(HostAbortCleanupGuard), nameof(BeforeHostAbort));
        if (abort == null || abort.ReturnType != typeof(void) || prefix == null) return false;

        var harmony = new Harmony(PatchId);
        _host = host;
        _cleanup = cleanup;
        try
        {
            harmony.Patch(abort, prefix: new HarmonyMethod(prefix));
        }
        catch
        {
            _host = null;
            _cleanup = null;
            throw;
        }
        _abort = abort;
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony != null && _abort != null)
            _harmony.Unpatch(_abort, HarmonyPatchType.Prefix, PatchId);
        _harmony = null;
        _abort = null;
        _host = null;
        _cleanup = null;
    }

    private static void BeforeHostAbort(object __instance)
    {
        if (!ReferenceEquals(__instance, _host)) return;
        try
        {
            _cleanup?.Invoke();
        }
        catch (Exception ex)
        {
            // Cleanup failures must not prevent SHVDN from aborting the host.
            API.Logger.Warning("Coop Control: host cleanup failed: " + ex.GetType().Name);
        }
    }
}
