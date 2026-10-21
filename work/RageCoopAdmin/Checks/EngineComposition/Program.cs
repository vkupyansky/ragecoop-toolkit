using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using GTA;
using GTA.Math;
using HarmonyLib;
using Lidgren.Network;
using LemonUI;
using LemonUI.Menus;
using RageCoop.Client;
using RageCoop.Core;

internal static partial class Program
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly Resource = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RageCoopAdmin.Client.dll"));
    private static readonly Assembly Engine = typeof(SyncedEntity).Assembly;
    private static readonly Type Network = Engine.GetType("RageCoop.Client.Networking", true)!;
    private static readonly Type Pool = Engine.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly Guard[] Guards = { new Guard("Ingress"), new Guard("Packet"), new Guard("Network"), new Guard("Traffic"), new Guard("Damage"), new Guard("Motion"), new Guard("Send"), new Guard("Gameplay") };
    private static readonly List<ExpectedPatch> Expected = new List<ExpectedPatch>();

    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args.Length == 1 && args[0] == "--public-engine") return CheckPublicEngine();
            if (args.Length == 1 && args[0] == "--public-release") return CheckReleaseProfile(publicRelease: true);
            if (args.Length == 1 && args[0] == "--private-profile") return CheckReleaseProfile(publicRelease: false);
            Console.Error.WriteLine("Usage: EngineCompositionCheck.exe [--public-release|--public-engine|--private-profile]");
            return 2;
        }
        var boundary = new Harmony("friendslegacy.check.enginecompositionboundary");
        MethodInfo movie = typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!;
        try
        {
            // PlayerList's type initializer requests a GTA HUD movie. Only this
            // game boundary is suppressed; resource guards and engine stay real.
            boundary.Patch(movie, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
            InitializeHost();
            CheckSpeedometerAssets();
            CheckSpeedometerTextureCache();
            CheckSpeedometerRendering();
            CheckLocalVehicleInput();
            CheckBoostReconnect();
            CheckVehicleToolsComposition();
            CheckAppearancePreviewComposition();
            CheckInterfaceInputComposition();
            CheckMenuStorageComposition();
            CheckVehicleSpawnComposition();
            CheckVehicleCardComposition();
            CheckGameplayComposition();
            CheckMenuNavigationComposition();
            CheckIdleInputPolling();
            CheckConcurrentMenuStorage();
            CheckSpeedometerInitialization();
            CheckResourceIdentity();
            CheckHostCleanupComposition();
            CheckResourceLifecycleComposition();
            CheckRosterComposition();
            CheckEngineGuardComposition();
            Assert(Harmony.GetPatchInfo(movie)?.Owners.Contains(boundary.Id) == true,
                "Resource unload preserves patches owned by another component.");
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(Resource.Location))
            {
                Console.WriteLine("Client resource SHA256: " + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
            }
            Console.WriteLine("Actual built client resource composition passed: host lifecycle, pause ownership, Xbox menu input, held O dispatch, vehicle menu, appearance wiring and engine guards coexist, reinstall and unload; GTA natives, physical devices and rendering were not exercised.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            RemoveSharedRuntimeGuards();
            foreach (Guard guard in Guards.Reverse()) { guard.Uninstall(); }
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
            {
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
            }
        }
    }

    private static void CheckEngineGuardComposition()
    {
        DefineExpectedPatches();
        CheckResourceLifecycleCalls();
        CheckGameplayRollbackIsolation();
        InstallSharedRuntimeGuards();
        Assert(Resource.GetReferencedAssemblies().Any(name => name.Name == typeof(Harmony).Assembly.GetName().Name &&
            name.Version == typeof(Harmony).Assembly.GetName().Version), "The resource and composition host use the same Harmony identity.");
        foreach (Guard guard in Guards) { guard.Install(); }
        CheckPatches();
        foreach (Guard guard in Guards) { guard.Install(); }
        CheckPatches();
        CheckSharedSendMethods();
        CheckSkippedSends();
        CheckPacketDecodeWithGuards();

        foreach (Guard guard in Guards)
        {
            guard.Uninstall();
            guard.Uninstall();
            CheckPatches(guard);
            if (guard.Name == "Traffic")
            {
                Assert(CurrentCalls(Send(typeof(SyncedPed)), "EngineSendGuard", "IsLocalOrMissing") == 1,
                    "Removing traffic cadence retains the send transpiler on the same real method.");
            }
            if (guard.Name == "Send")
            {
                Assert(CurrentCalls(Send(typeof(SyncedPed)), "EngineSendGuard", "IsLocalOrMissing") == 0,
                    "Removing the send guard restores its original IL while retaining traffic prefixes and postfixes.");
            }
            guard.Install();
            CheckPatches();
        }
        CheckSharedSendMethods();
        foreach (Guard guard in Guards.Reverse()) { guard.Uninstall(); }
        RemoveSharedRuntimeGuards();
        CheckPatches(Guards);
    }

    private static void CheckVehicleToolsComposition()
    {
        var boundary = new Harmony("friendslegacy.check.vehicletoolscompositionboundary");
        try { CheckVehicleToolsCompositionCore(boundary); }
        finally
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
            {
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
            }
        }
    }

    private static void CheckVehicleToolsCompositionCore(Harmony boundary)
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        MethodInfo Method(string name) => script.GetMethod(name, Instance)!;
        List<CodeInstruction> Instructions(string name) => PatchProcessor.GetOriginalInstructions(Method(name));
        Assert(Instructions("BuildMenus").Count(code => code.Calls(Method("BuildVehicleToolsMenu"))) == 1 &&
            Instructions("BuildMenus").Count(code => code.Calls(Method("BuildFavoritesMenu"))) == 1,
            "The assembled vehicle menu still includes saved vehicles and favorites.");
        Assert(Instructions("TickVehicleTools").Count(code => code.Calls(Method("CaptureOwnerAppearance"))) == 1 &&
            Instructions("CaptureOwnerAppearance").Count(code => code.Calls(Method("TryCaptureVehicleAppearance"))) == 1 &&
            Instructions("CaptureOwnerAppearance").Count(code => code.Calls(Method("StageVehicleAppearance"))) == 1,
            "The assembled resource still captures driver appearance changes and queues them independently of menu editing.");
        Type native = Resource.GetType("RageCoopAdmin.Client.NativeVehicleAppearance", true)!;
        Assert(Instructions("TryCaptureVehicleAppearance").Count(code => code.Calls(native.GetMethod("Capture", Static)!)) == 1 &&
            Instructions("TryApplyVehicleAppearanceCore").Count(code => code.Calls(native.GetMethod("Apply", Static)!)) == 1,
            "The actual client resource retains full appearance capture and application for Los Santos Customs and saved vehicles.");

        // LemonUI's layout calculation crosses into GTA. Menu construction,
        // submenu links, preset actions and the resource builder remain real.
        boundary.Patch(typeof(NativeMenu).GetMethod("UpdateItems", Instance)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(NativeMenu).GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        foreach (Type element in new[] { typeof(LemonUI.Elements.ScaledText), typeof(LemonUI.Elements.ScaledTexture), typeof(LemonUI.Elements.ScaledRectangle) })
        {
            boundary.Patch(element.GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        }
        boundary.Patch(typeof(LemonUI.Elements.ScaledTexture).GetMethod("Request", Instance)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Elements.ScaledText).GetProperty("Width")!.GetGetMethod()!,
            prefix: new HarmonyMethod(typeof(Program), nameof(IsolateTextWidth)));
        Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
        boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(System.Drawing.PointF) }, null)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(KeepLayoutPoint)));
        boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButton).GetConstructor(new[] { typeof(string), typeof(GTA.Control) })!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButtons).GetMethod("Add", Instance)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Scaleform.BaseScaleform).GetConstructor(Instance, null, new[] { typeof(string) }, null)!,
            prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        // The script constructor also creates the GTA speedometer text element.
        // Only preset data needed by this menu is initialized in the test host.
        object instance = FormatterServices.GetUninitializedObject(script);
        FieldInfo savedVehicles = script.GetField("_presets", Instance)!;
        savedVehicles.SetValue(instance, Activator.CreateInstance(savedVehicles.FieldType));
        var vehicleMenu = new NativeMenu("Проверка", "Транспорт");
        script.GetField("_vehicleMenu", Instance)!.SetValue(instance, vehicleMenu);
        // ObjectPool's constructor reads GTA's graphics settings; its managed
        // collection and Add path are sufficient for menu registration here.
        var pool = (ObjectPool)FormatterServices.GetUninitializedObject(typeof(ObjectPool));
        FieldInfo poolItems = typeof(ObjectPool).GetFields(Instance).Single(field =>
            field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>));
        poolItems.SetValue(pool, Activator.CreateInstance(poolItems.FieldType));
        script.GetField("_pool", Instance)!.SetValue(instance, pool);
        Method("BuildVehicleToolsMenu").Invoke(instance, Array.Empty<object>());
        Assert(vehicleMenu.Items.Count == 1 && vehicleMenu.Items[0].Title == "Гараж",
            "The actual vehicle tools builder exposes the saved vehicle garage as its only submenu.");
        var presets = (NativeMenu)script.GetField("_presetsMenu", Instance)!.GetValue(instance)!;
        Assert(((System.Collections.IList)poolItems.GetValue(pool)!).Contains(presets) &&
            presets.Parent == vehicleMenu && presets.Items.Count == 2 &&
            presets.Items[0].Title == "Сохранить текущую машину" && presets.Items[0].Enabled && !presets.Items[1].Enabled,
            "The actual empty garage retains its save action and disabled empty-state row.");
        Assert(new[] { "ChangeAppearance", "AddPaintRow", "OpenVehicleParts", "ChangePlate" }.All(name => script.GetMethod(name, Instance) == null) &&
            Resource.GetType("CoopControl.Shared.VehicleAppearanceFactory") == null,
            "The built client contains no separate appearance editor handlers or factory reset implementation.");
    }

    private static bool SkipMenuLayout() => false;

    private static bool KeepLayoutPoint(System.Drawing.PointF __0, ref System.Drawing.PointF __result)
    { __result = __0; return false; }

    private static void InitializeHost()
    {
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        host.GetField("LocalPlayerID", Static)!.SetValue(null, 1);
    }

    private static void DefineExpectedPatches()
    {
        Guard ingress = Guards.Single(guard => guard.Name == "Ingress");
        Add(ingress, (MethodInfo)ingress.Call("FindMessageTarget")!, 1, 0, 0, 0);
        Add(ingress, (MethodInfo)ingress.Call("FindDownloadTarget")!, 1, 0, 0, 0);
        Guard packet = Guards.Single(guard => guard.Name == "Packet"), network = Guards.Single(guard => guard.Name == "Network");
        Guard traffic = Guards.Single(guard => guard.Name == "Traffic"), damage = Guards.Single(guard => guard.Name == "Damage");
        Guard motion = Guards.Single(guard => guard.Name == "Motion"), send = Guards.Single(guard => guard.Name == "Send");
        Add(packet, (MethodInfo)packet.Call("FindTarget")!, 0, 0, 1, 0);
        Add(packet, (MethodInfo)packet.Call("FindEnvelopeTarget")!, 0, 0, 1, 0);
        foreach (string name in new[] { "PedSync", "VehicleSync", "ProjectileSync" }) { Add(network, Network.GetMethod(name, Static)!, 1, 0, 0, 1); }
        foreach (string name in new[] { "Cleanup", "RemoveAllFromPlayer" }) { Add(network, Pool.GetMethod(name, Static)!, 1, 0, 0, 1); }
        Add(network, typeof(SyncedEntity).GetProperty("OwnerID")!.GetSetMethod(true)!, 1, 1, 1, 0);
        Add(network, typeof(SyncedEntity).GetProperty("IsOutOfSync")!.GetGetMethod()!, 1, 0, 0, 0);
        Add(traffic, (MethodInfo)traffic.Call("FindDoSync")!, 0, 0, 1, 0);
        Add(traffic, (MethodInfo)traffic.Call("FindCreateVehicle")!, 1, 0, 1, 0);
        foreach (Type entity in new[] { typeof(SyncedPed), typeof(SyncedVehicle) })
        {
            Add(traffic, (MethodInfo)traffic.Call("FindAdd", entity)!, 1, 0, 0, 0);
            Add(traffic, Send(entity), 1, 1, 0, 0);
            Add(send, Send(entity), 0, 0, 1, 0);
        }
        Add(damage, (MethodInfo)damage.Call("FindTarget")!, 1, 1, 1, 0);
        Add(damage, (MethodInfo)damage.Call("FindCaptureTarget")!, 0, 0, 1, 0);
        Add(damage, (MethodInfo)damage.Call("FindUpdateTarget")!, 0, 1, 1, 0);
        Add(damage, (MethodInfo)damage.Call("FindOwnerTarget")!, 1, 0, 0, 0);
        Add(motion, (MethodInfo)motion.Call("FindVehicleTarget")!, 0, 0, 1, 0);
        Add(motion, (MethodInfo)motion.Call("FindPedTarget")!, 0, 0, 1, 0);
        Add(motion, (MethodInfo)motion.Call("FindSmoothTarget")!, 0, 0, 1, 0);
        Guard gameplay = Guards.Single(guard => guard.Name == "Gameplay");
        foreach (string target in new[] { "FindVehicleTarget", "FindProjectileTarget", "FindCleanupTarget", "FindRemoveTarget" })
            Add(gameplay, (MethodInfo)gameplay.Call(target)!, 1, 0, 0, 0);
    }

    private static void Add(Guard guard, MethodInfo method, int prefix, int postfix, int transpiler, int finalizer) =>
        Expected.Add(new ExpectedPatch { Guard = guard, Target = method, Counts = new[] { prefix, postfix, transpiler, finalizer } });

    private static void CheckPatches(params Guard[] excluded)
    {
        foreach (ExpectedPatch expected in Expected)
        {
            Patches? patches = Harmony.GetPatchInfo(expected.Target);
            IEnumerable<Patch>[] groups = { patches?.Prefixes ?? Enumerable.Empty<Patch>(), patches?.Postfixes ?? Enumerable.Empty<Patch>(),
                patches?.Transpilers ?? Enumerable.Empty<Patch>(), patches?.Finalizers ?? Enumerable.Empty<Patch>() };
            for (int kind = 0; kind < groups.Length; kind++)
            {
                Patch[] owned = groups[kind].Where(patch => patch.owner == expected.Guard.Id).ToArray();
                Assert(owned.Length == (excluded.Contains(expected.Guard) ? 0 : expected.Counts[kind]),
                    expected.Guard.Name + " patch count changed on " + expected.Target.Name + " for kind " + kind + ".");
                Assert(owned.All(patch => patch.PatchMethod.DeclaringType == expected.Guard.Type),
                    "Every engine patch comes from the guard in the actual built resource assembly.");
            }
        }
        foreach (Guard guard in Guards)
        {
            MethodBase[] actual = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(guard.Id) == true).ToArray();
            MethodBase[] expected = excluded.Contains(guard) ? Array.Empty<MethodBase>() : Expected.Where(item => item.Guard == guard).Select(item => (MethodBase)item.Target).ToArray();
            Assert(actual.Length == expected.Length && actual.All(expected.Contains), guard.Name + " owns exactly its documented engine targets.");
        }
    }

    private static void CheckResourceLifecycleCalls()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        List<CodeInstruction> start = PatchProcessor.GetOriginalInstructions(script.GetMethod("OnStart", Instance)!);
        List<CodeInstruction> cleanup = PatchProcessor.GetOriginalInstructions(script.GetMethod("Cleanup", Instance)!);
        MethodInfo gameplayCleanup = script.GetMethod("UninstallGameplayGuard", Static)!;
        foreach (Guard guard in Guards)
        {
            Assert(start.Count(code => code.Calls(guard.Method("Install"))) == 1,
                "The actual resource installs " + guard.Name + " exactly once at startup.");
            if (guard.Name == "Gameplay")
            {
                Assert(start.Count(code => code.Calls(gameplayCleanup)) == 1 &&
                    cleanup.Count(code => code.Calls(gameplayCleanup)) == 1 &&
                    PatchProcessor.GetOriginalInstructions(gameplayCleanup).Count(code => code.Calls(guard.Method("Uninstall"))) == 1,
                    "The actual resource contains gameplay cleanup failures during rollback and cleanup.");
            }
            else
            {
                Assert(start.Count(code => code.Calls(guard.Method("Uninstall"))) == 1 &&
                    cleanup.Count(code => code.Calls(guard.Method("Uninstall"))) == 1,
                    "The actual resource wires " + guard.Name + " into failed-start rollback and cleanup.");
            }
        }
    }

    private static void CheckGameplayRollbackIsolation()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        MethodInfo cleanup = script.GetMethod("UninstallGameplayGuard", Static)!;
        Type guard = Resource.GetType("RageCoopAdmin.Client.EngineGameplayGuard", true)!;
        MethodInfo uninstall = guard.GetMethod("Uninstall", Static)!;
        var boundary = new Harmony("friendslegacy.check.gameplayrollback");
        try
        {
            boundary.Patch(uninstall, prefix: new HarmonyMethod(typeof(Program), nameof(FailGameplayUninstall)));
            Exception? failure = null;
            try { cleanup.Invoke(null, null); }
            catch (TargetInvocationException error) { failure = error.InnerException ?? error; }
            Assert(failure == null, "A failed gameplay-guard cleanup is contained so later owners can be removed.");
        }
        finally
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
        }
        Assert(Harmony.GetPatchInfo(uninstall)?.Owners.Contains(boundary.Id) != true,
            "The injected gameplay cleanup failure is removed from the real guard.");
    }

    private static bool FailGameplayUninstall() => throw new InvalidOperationException("Injected gameplay cleanup failure.");

    private static void CheckSharedSendMethods()
    {
        foreach (Type entity in new[] { typeof(SyncedPed), typeof(SyncedVehicle) })
        {
            Patches patches = Harmony.GetPatchInfo(Send(entity))!;
            Assert(patches.Prefixes.Any(patch => patch.owner.EndsWith(".enginetraffic", StringComparison.Ordinal)) &&
                patches.Postfixes.Any(patch => patch.owner.EndsWith(".enginetraffic", StringComparison.Ordinal)) &&
                patches.Transpilers.Any(patch => patch.owner.EndsWith(".enginesend", StringComparison.Ordinal)),
                "Cadence and packet construction coexist on the actual " + Send(entity).Name + ".");
        }
        Assert(CurrentCalls(Send(typeof(SyncedPed)), "EngineSendGuard", "IsLocalOrMissing") == 1,
            "Combined SendPed IL contains exactly one guarded vehicle owner check.");
        MethodInfo vehicle = Send(typeof(SyncedVehicle));
        MethodInfo station = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", true)!.GetProperty("RadioStation")!.GetSetMethod()!;
        Assert(PatchProcessor.GetCurrentInstructions(vehicle).Count(code => code.Calls(station)) == 2,
            "Combined SendVehicle IL contains the radio reset and its original radio assignment.");
    }

    private static void CheckSkippedSends()
    {
        FieldInfo interval = Network.GetField("SyncInterval", Static)!;
        object previous = interval.GetValue(null)!;
        try
        {
            interval.SetValue(null, int.MaxValue);
            foreach (Type entity in new[] { typeof(SyncedPed), typeof(SyncedVehicle) })
            {
                var record = (SyncedEntity)FormatterServices.GetUninitializedObject(entity);
                typeof(SyncedEntity).GetProperty("LastSentStopWatch", Instance)!.SetValue(record, Stopwatch.StartNew());
                record.SendFullNextFrame = true;
                Send(entity).Invoke(null, new object[] { record, true });
                Assert(record.SendFullNextFrame, "Combined " + Send(entity).Name + " preserves an explicit full request on a cadence skip before native access.");
            }
        }
        finally { interval.SetValue(null, previous); }
    }

    private static void CheckPacketDecodeWithGuards()
    {
        Type packet = typeof(Logger).Assembly.GetType("RageCoop.Core.Packets+VehicleSync", true)!;
        Type damage = packet.GetProperty("DamageModel")!.PropertyType;
        object reused = Network.GetNestedType("ReceivedPackets", BindingFlags.NonPublic | BindingFlags.Public)!
            .GetField("VehicelPacket", Static)!.GetValue(null)!;
        const int id = 987601;
        // This composition check stops at the real packet boundary. Applying a
        // full snapshot to the world is a separate host/game behavior check.
        foreach (var state in new[] { (Full: true, Broken: true, Expected: (byte)5),
            (Full: false, Broken: false, Expected: (byte)5), (Full: true, Broken: false, Expected: (byte)0) })
        {
            object outgoingPacket = Activator.CreateInstance(packet)!;
            void Set(string name, object value) => packet.GetProperty(name)!.SetValue(outgoingPacket, value);
            Set("ID", id);
            Set("OwnerID", 11);
            Set("Flags", Enum.ToObject(packet.GetProperty("Flags")!.PropertyType, state.Full ? 8192 : 0));
            Set("Position", new Vector3(11f, 12f, 13f));
            Set("Colors", new byte[] { 12, 34 });
            Set("Mods", new Dictionary<int, int>());
            Set("LicensePlate", "COMPOSE");
            object model = Activator.CreateInstance(damage)!;
            damage.GetProperty("BrokenDoors")!.SetValue(model, state.Broken ? (byte)5 : (byte)0);
            Set("DamageModel", model);
            var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
            packet.GetMethod("Pack")!.Invoke(outgoingPacket, new object[] { outgoing });
            var incoming = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
            incoming.Data = outgoing.Data;
            incoming.LengthBits = outgoing.LengthBits;
            incoming.ReadByte();
            packet.GetMethod("Deserialize")!.Invoke(reused, new object[] { incoming });
            Assert(incoming.Position == incoming.LengthBits, "The actual packet parser consumes the full serialized payload.");
            object actual = packet.GetProperty("DamageModel")!.GetValue(reused)!;
            Assert((byte)damage.GetProperty("BrokenDoors")!.GetValue(actual)! == state.Expected &&
                (Vector3)packet.GetProperty("Position")!.GetValue(reused)! == new Vector3(11f, 12f, 13f),
                "The actual parser preserves partial damage and clears intact full damage while every engine guard is installed.");
        }
    }

    private static MethodInfo Send(Type entity) => Network.GetMethod(entity == typeof(SyncedPed) ? "SendPed" : "SendVehicle", Static)!;
    private static int CurrentCalls(MethodInfo method, string type, string name) => PatchProcessor.GetCurrentInstructions(method)
        .Count(code => code.operand is MethodInfo call && call.DeclaringType?.Assembly == Resource && call.DeclaringType.Name == type && call.Name == name);
    private static bool SkipMovie() => false;
    private static void Assert(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Guard
    {
        internal readonly string Name;
        internal readonly string Id;
        internal readonly Type Type;
        internal Guard(string name)
        {
            Name = name;
            Id = "friendslegacy.ragecoop.engine" + name.ToLowerInvariant();
            Type = Resource.GetType("RageCoopAdmin.Client.Engine" + name + "Guard", true)!;
        }
        internal MethodInfo Method(string name) => Type.GetMethod(name, Static)!;
        internal object? Call(string name, params object[] arguments) => Method(name).Invoke(null, arguments);
        internal void Install()
        {
            object[] arguments = Method("Install").GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
            Assert(Call("Install", arguments) is not false, Name + " rejected the installed engine contract.");
        }
        internal void Uninstall() => Call("Uninstall");
    }

    private sealed class ExpectedPatch
    {
        internal Guard Guard = null!;
        internal MethodInfo Target = null!;
        internal int[] Counts = Array.Empty<int>();
    }
}
