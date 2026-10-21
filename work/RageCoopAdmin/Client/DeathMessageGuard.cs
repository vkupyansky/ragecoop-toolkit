using System;
using System.Reflection;
using HarmonyLib;

namespace RageCoopAdmin.Client;

internal static class DeathMessageGuard
{
    private const string PatchId = "friendslegacy.ragecoop.killmessage";
    private static Harmony? _harmony;
    private static MethodInfo? _target;

    internal static bool Install()
    {
        if (_harmony != null)
        {
            return true;
        }

        Type? main = Type.GetType("RageCoop.Client.Main, RageCoop.Client", throwOnError: false);
        MethodInfo? target = main == null ? null : AccessTools.DeclaredMethod(main, "KillMessage");
        if (target == null || target.ReturnType != typeof(string) || target.GetParameters().Length != 0)
        {
            return false;
        }

        MethodInfo? finalizer = AccessTools.DeclaredMethod(typeof(DeathMessageGuard), nameof(CatchMissingDeathInfo));
        if (finalizer == null)
        {
            return false;
        }

        var harmony = new Harmony(PatchId);
        harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
        _harmony = harmony;
        _target = target;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony == null || _target == null)
        {
            return;
        }

        _harmony.Unpatch(_target, HarmonyPatchType.Finalizer, PatchId);
        _harmony = null;
        _target = null;
    }

    private static Exception? CatchMissingDeathInfo(Exception? __exception, ref string __result)
    {
        if (__exception is NullReferenceException)
        {
            // RageCoop may remove a player before composing a death message.
            __result = "~h~Игрок~h~ погиб";
            return null;
        }

        return __exception;
    }
}
