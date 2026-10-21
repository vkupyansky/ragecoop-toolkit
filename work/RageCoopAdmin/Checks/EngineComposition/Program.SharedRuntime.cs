using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Lidgren.Network;

internal static partial class Program
{
    private static Type SharedGuard(string name) => Resource.GetType("CoopControl.Shared.Engine" + name + "Guard", true)!;

    private static void InstallSharedRuntimeGuards()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        foreach (Type type in new[] { SharedGuard("Logger"), SharedGuard("Worker"), SharedGuard("Transport"), Resource.GetType("RageCoopAdmin.Client.EngineRosterGuard", true)! })
        {
            foreach (string name in new[] { "OnStart", "Cleanup" })
            {
                var code = PatchProcessor.GetOriginalInstructions(script.GetMethod(name, Instance)!);
                Assert(code.Count(instruction => instruction.Calls(type.GetMethod("Uninstall", Static)!)) == 1,
                    "Lifecycle removes " + type.Name + " exactly once on cleanup or failed startup.");
                if (name == "OnStart")
                    Assert(code.Count(instruction => instruction.Calls(type.GetMethod("Install", Static)!)) == 1,
                        "Lifecycle installs " + type.Name + " exactly once.");
            }
        }
        foreach (string name in new[] { "Logger", "Worker", "Transport" })
            for (int repeat = 0; repeat < 2; repeat++) SharedGuard(name).GetMethod("Install", Static)!.Invoke(null, null);
        var workerTargets = (System.Collections.Generic.IEnumerable<MethodInfo>)SharedGuard("Worker").GetMethod("FindTargets", Static)!.Invoke(null, null)!;
        Assert(workerTargets.Count() == 3 && workerTargets.All(method =>
            Harmony.GetPatchInfo(method)?.Owners.Contains("friendslegacy.ragecoop.engineworker") == true),
            "The actual resource protects all three Core Worker lifecycle methods alongside its other guards.");
        MethodInfo fragment = (MethodInfo)SharedGuard("Fragment").GetMethod("FindFragmentTarget", Static)!.Invoke(null, null)!;
        Type network = Engine.GetType("RageCoop.Client.Networking", true)!;
        Type peer = network.GetField("Peer", Static)!.FieldType;
        Assert(fragment.DeclaringType!.IsAssignableFrom(peer) && fragment.DeclaringType.Assembly == typeof(NetPeer).Assembly,
            "The compiled resource fragment target is the exact Lidgren assembly used by the installed client peer.");
        Assert(Harmony.GetPatchInfo(fragment)!.Owners.Contains("friendslegacy.ragecoop.transportfragments"),
            "The actual shared fragment hook is active alongside every client engine guard.");
    }

    private static void RemoveSharedRuntimeGuards()
    {
        foreach (string name in new[] { "Transport", "Worker", "Logger" }) SharedGuard(name).GetMethod("Uninstall", Static)!.Invoke(null, null);
        foreach (string owner in new[] { "friendslegacy.ragecoop.transportfragments", "friendslegacy.ragecoop.enginecodec", "friendslegacy.ragecoop.enginelogger", "friendslegacy.ragecoop.engineworker" })
            Assert(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true),
                "Shared runtime cleanup removes only its own " + owner + " hooks.");
    }
}
