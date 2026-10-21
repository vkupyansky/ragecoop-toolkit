using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        try
        {
            CheckInstalledAudioContract();
            CheckPatchLifecycle();
            Console.WriteLine("Vehicle radio protocol and patch lifecycle checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckInstalledAudioContract()
    {
        MethodInfo update = VehicleAudioSync.FindUpdateMethod() ?? throw new InvalidOperationException("Vehicle update is missing.");
        FieldInfo station = VehicleAudioSync.FindStationField() ?? throw new InvalidOperationException("Received radio station is missing.");
        Type networking = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Networking", throwOnError: true)!;
        MethodInfo[] receivers = networking.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name == "VehicleSync").ToArray();
        Assert(receivers.Any(receiver => PatchProcessor.GetOriginalInstructions(receiver).Any(code => Equals(code.operand, station))),
            "The installed vehicle packet receiver populates the radio station field used by this patch.");
        Assert(!PatchProcessor.GetOriginalInstructions(update).Any(code => Equals(code.operand, station)),
            "The installed vehicle updater needs this patch because it does not consume the received station.");
        Assert(PatchProcessor.GetOriginalInstructions(update).Any(code => code.operand is MethodInfo method && method.Name == "get_HornActive"),
            "The installed updater already replays horn state; the resource must not add a second horn loop.");
    }

    private static void CheckPatchLifecycle()
    {
        MethodInfo update = VehicleAudioSync.FindUpdateMethod()!;
        try
        {
            Assert(VehicleAudioSync.Install() && VehicleAudioSync.Install(), "Radio patch installs idempotently.");
            Assert(Harmony.GetPatchInfo(update).Postfixes.Count(patch => patch.owner == "friendslegacy.ragecoop.vehicleradio") == 1,
                "Repeated initialization does not stack audio callbacks.");
        }
        finally
        {
            VehicleAudioSync.Uninstall();
            VehicleAudioSync.Uninstall();
        }

        Assert(Harmony.GetPatchInfo(update)?.Postfixes.All(patch => patch.owner != "friendslegacy.ragecoop.vehicleradio") != false,
            "Resource cleanup removes its callback from the underlying client.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
