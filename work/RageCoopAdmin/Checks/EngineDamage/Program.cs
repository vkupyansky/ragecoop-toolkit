using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GTA;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Host = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.Main", true)!;
    private static readonly Type Pool = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly Type Players = typeof(SyncedVehicle).Assembly.GetType("RageCoop.Client.PlayerList", true)!;
    private static readonly List<Action> Delayed = new List<Action>();
    private static int _lastRepairHandle;
    private const string NativePatchId = "friendslegacy.check.enginedamage";
    private static readonly List<MethodInfo> NativeTargets = new List<MethodInfo>();
    private static int _brokenDoors, _brokenWindows, _openedDoors, _vehicleRepairs, _windowRepairs;
    private static int _burstedTyres, _tyrePunctures, _tyreBursts, _tyreFixes;
    private static VehicleWheel[] _wheels = Array.Empty<VehicleWheel>();
    private static float _engine, _body, _tank, _dirt;
    private static bool _running, _dead, _leftLight, _rightLight;

    private static int Main()
    {
        var natives = new Harmony(NativePatchId);
        try
        {
            CheckInstalledContract();
            InstallNativeBoundary(natives);
            CheckOriginalDeferredRepair();
            CheckOriginalTyreTruncation();
            Assert(EngineDamageGuard.Install() && EngineDamageGuard.Install(), "The damage patch installs idempotently against the actual engine method.");
            CheckTyreRoundTrips();
            CheckPendingRepairs();
            CheckDoorRestoration();
            CheckWindowRestoration();
            CheckWindowDoorIndices();
            CheckInitialMismatchAndDeath();
            Console.WriteLine("Actual engine damage application checks passed; GTA native effects were simulated at the game boundary.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            EngineDamageGuard.Uninstall();
            foreach (MethodInfo target in NativeTargets) { natives.Unpatch(target, HarmonyPatchType.All, NativePatchId); }
        }
    }

    private static void CheckInstalledContract()
    {
        MethodInfo target = EngineDamageGuard.FindTarget() ?? throw new InvalidOperationException("Installed damage method missing.");
        Assert(EngineDamageGuard.HasSupportedContract(target), "The installed method has one-way door/window damage and the exact supported model contract.");
        CheckTyreContract(target);
        bool UsesNative(Type type, string name, Hash hash) => PatchProcessor.GetOriginalInstructions(type.GetMethod(name, Instance)!)
            .Any(code => code.operand is long value && unchecked((ulong)value) == (ulong)hash);
        Assert(UsesNative(typeof(VehicleWindow), "Repair", Hash.FIX_VEHICLE_WINDOW) && UsesNative(typeof(Vehicle), "Repair", Hash.SET_VEHICLE_FIXED),
            "Installed SHVDN repairs individual windows with FIX_VEHICLE_WINDOW and reconstructs doors with SET_VEHICLE_FIXED.");
        MethodInfo update = EngineDamageGuard.FindUpdateTarget()!;
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(update);
        Assert(EngineDamageGuard.TryFindDeferredRepair(original, out _), "The actual delayed-repair call and generated callback match the supported contract.");
        CodeInstruction[] replaced = EngineDamageGuard.ReplaceDeferredRepair(original).ToArray();
        Assert(!replaced.Any(code => code.operand is MethodInfo method && method.DeclaringType == Host && method.Name == "Delay") &&
            replaced.Count(code => code.operand is MethodInfo method && method.Name == "TrackPendingRepair") == 1,
            "The repair transition retains its original branch and allocates no deferred task or callback.");
        bool rejected = false;
        try { EngineDamageGuard.ReplaceDeferredRepair(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unsupported repair IL is rejected.");
        Assert(EngineDamageGuard.RestoredBits(7, 6) == 1 && EngineDamageGuard.RestoredBits(0, 255) == 0 && EngineDamageGuard.RestoredBits(7, 7) == 0,
            "Only authoritative broken-to-clear bits request restoration.");
    }

    private static void CheckTyreContract(MethodInfo apply)
    {
        MethodInfo capture = EngineDamageGuard.FindCaptureTarget() ?? throw new InvalidOperationException("Installed damage capture method missing.");
        Assert(EngineDamageGuard.HasSupportedTyreContract(capture, apply),
            "Both actual tyre shifts use the exact SHVDN bone IDs and the same unchanged Int16 model field.");
        foreach (MethodInfo method in new[] { capture, apply })
        {
            bool writing = method == capture;
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method);
            Assert(EngineDamageGuard.TryFindTyreShift(original, writing, out int at),
                method.Name + " has exactly one supported bone-ID shift followed by Int16 conversion.");
            List<CodeInstruction> changed = EngineDamageGuard.ReplaceTyreShift(original, method).ToList();
            Assert(changed[at].opcode == OpCodes.Call && changed[at].operand is MethodInfo mapping && mapping.Name == "TyreBitIndex" &&
                changed.Where((code, index) => index != at).Select(code => (code.opcode, code.operand))
                    .SequenceEqual(original.Where((code, index) => index != at).Select(code => (code.opcode, code.operand))),
                method.Name + " changes only its bone-ID callsite and retains the existing damage loops.");
            void Reject(List<CodeInstruction> unsupported, string reason)
            {
                Assert(!EngineDamageGuard.TryFindTyreShift(unsupported, writing, out _), method.Name + " rejects " + reason + ".");
                bool rejected = false;
                try { EngineDamageGuard.ReplaceTyreShift(unsupported, method).ToArray(); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, method.Name + " refuses to rewrite " + reason + ".");
            }
            List<CodeInstruction> modified = original.Select(code => new CodeInstruction(code)).ToList();
            modified[at + 4].opcode = OpCodes.Conv_I4;
            Reject(modified, "a changed tyre field conversion");
            modified = original.Select(code => new CodeInstruction(code)).ToList();
            modified.Insert(at, new CodeInstruction(original[at]));
            Reject(modified, "an additional bone-ID read");
            modified = original.Select(code => new CodeInstruction(code)).ToList();
            modified[at + 5].opcode = OpCodes.Xor;
            Reject(modified, "a changed capture/application mask operation");
            Reject(new List<CodeInstruction> { new CodeInstruction(OpCodes.Ret) }, "unrelated IL");
        }
        Assert(!EngineDamageGuard.HasSupportedTyreContract(apply, capture), "Swapped capture/application signatures are unsupported.");
    }

    private static void CheckOriginalTyreTruncation()
    {
        ResetNative();
        Vehicle vehicle = NativeVehicle(930000);
        PrepareWheels(vehicle);
        for (int bone = 11; bone <= 20; bone++)
        {
            _burstedTyres = 1 << bone;
            short mask = TyreMask(Capture(vehicle));
            Assert(mask == unchecked((short)(1 << bone)), "Original capture reproduces the Int16 tyre mask for bone " + bone + ".");
            ApplyTyres(vehicle, unchecked((short)(1 << bone)));
            Assert(_burstedTyres == (bone <= 15 ? 1 << bone : 0),
                "Original application " + (bone <= 15 ? "retains" : "incorrectly fixes") + " bone " + bone + ".");
        }
    }

    private static void CheckTyreRoundTrips()
    {
        for (int bone = 11; bone <= 20; bone++)
        {
            ResetNative();
            Vehicle vehicle = NativeVehicle(930010 + bone);
            PrepareWheels(vehicle);
            _burstedTyres = 1 << bone;
            object snapshot = Capture(vehicle);
            int index = bone <= 15 ? bone : bone - 16;
            Assert(TyreMask(snapshot) == unchecked((short)(1 << index)),
                "Actual capture preserves only the independent tyre bit for bone " + bone + ".");
            _burstedTyres = 0;
            EngineDamageGuard.FindTarget()!.Invoke(null, new[] { vehicle, snapshot, (object)true });
            Assert(_burstedTyres == 1 << bone && _tyrePunctures == 1 && _tyreBursts == 1 && _tyreFixes == 0,
                "Actual application bursts only bone " + bone + " after an owner-to-peer snapshot.");
            EngineDamageGuard.FindTarget()!.Invoke(null, new[] { vehicle, snapshot, (object)true });
            Assert(_tyrePunctures == 1 && _tyreBursts == 1 && _tyreFixes == 0,
                "A repeated damaged snapshot leaves bone " + bone + " unchanged.");
            ApplyTyres(vehicle, 0);
            Assert(_burstedTyres == 0 && _tyreFixes == 1 && _vehicleRepairs == 0,
                "A clean snapshot fixes bone " + bone + " without a whole-car repair.");
        }

        ResetNative();
        Vehicle stock = NativeVehicle(930100);
        PrepareWheels(stock);
        _burstedTyres = 0x7800;
        object oldStockSnapshot = Capture(stock);
        Assert(TyreMask(oldStockSnapshot) == 0x7800, "All four stock wheels retain their original combined wire mask 0x7800.");
        _burstedTyres = 0;
        EngineDamageGuard.FindTarget()!.Invoke(null, new[] { stock, oldStockSnapshot, (object)true });
        Assert(_burstedTyres == 0x7800 && _tyreBursts == 4 && _tyreFixes == 0,
            "The unchanged legacy four-wheel snapshot applies only to those four wheels.");

        ResetNative();
        Vehicle multiAxle = NativeVehicle(930101);
        PrepareWheels(multiAxle);
        _burstedTyres = 0x1ff800;
        object fullSnapshot = Capture(multiAxle);
        Assert(TyreMask(fullSnapshot) == unchecked((short)0xf81f), "All ten tyres fit in distinct preserved/previously unused Int16 bits.");
        _burstedTyres = 0;
        EngineDamageGuard.FindTarget()!.Invoke(null, new[] { multiAxle, fullSnapshot, (object)true });
        Assert(_burstedTyres == 0x1ff800 && _tyreBursts == 10, "A combined ten-wheel snapshot retains every tyre independently.");
        ApplyTyres(multiAxle, unchecked((short)0x8001));
        Assert(_burstedTyres == ((1 << 15) | (1 << 16)) && _tyreFixes == 8,
            "A partial repair preserves signed bit 15 and new bit 0 while fixing exactly eight other tyres.");
        ApplyTyres(multiAxle, 0);
        Assert(_burstedTyres == 0 && _tyreFixes == 10 && _vehicleRepairs == 0,
            "The final clean snapshot restores all ten tyres through the original per-wheel fix path.");
    }

    private static void PrepareWheels(Vehicle vehicle)
    {
        // Exercise the installed SHVDN collection/indexer and BoneId getter;
        // substitute only collection discovery and the native tyre operations.
        VehicleWheelCollection collection = vehicle.Wheels;
        _wheels = Enumerable.Range(11, 10).Select(bone => collection[(VehicleWheelBoneId)bone]).ToArray();
        Assert(_wheels.All(wheel => ReferenceEquals(wheel.Vehicle, vehicle)) && _wheels.Select(wheel => (int)wheel.BoneId).SequenceEqual(Enumerable.Range(11, 10)),
            "The real SHVDN wheel collection supplies all ten owner-bound wheel objects.");
    }

    private static object Capture(Vehicle vehicle) => EngineDamageGuard.FindCaptureTarget()!.Invoke(null, new object[] { vehicle })!;
    private static short TyreMask(object snapshot) => (short)snapshot.GetType().GetProperty("BurstedTires")!.GetValue(snapshot)!;
    private static void ApplyTyres(Vehicle vehicle, short mask)
    {
        MethodInfo target = EngineDamageGuard.FindTarget()!;
        Type modelType = target.GetParameters()[1].ParameterType;
        object model = Activator.CreateInstance(modelType)!;
        modelType.GetProperty("BurstedTires")!.SetValue(model, mask);
        target.Invoke(null, new object[] { vehicle, model, true });
    }

    private static void CheckOriginalDeferredRepair()
    {
        SyncedVehicle record = RepairRecord();
        for (int frame = 0; frame < 30; frame++) { Update(record); }
        Assert(Delayed.Count == 30, "The real original Update schedules a new one-second task for every still-dead remote update.");
        SetOwner(record, 1);
        Delayed[0]();
        Assert(_vehicleRepairs == 1 && _lastRepairHandle == record.MainVehicle.Handle,
            "The actual original deferred lambda repairs a car after it has become locally owned.");
        record = RepairRecord();
        Update(record);
        record.GetType().GetProperty("MainVehicle", Instance)!.SetValue(record, NativeVehicle(920002));
        Delayed[0]();
        Assert(_vehicleRepairs == 1 && _lastRepairHandle == 920002,
            "The actual original deferred lambda repairs a replacement native car that did not schedule it.");
        Delayed.Clear();
    }

    private static void CheckPendingRepairs()
    {
        SyncedVehicle record = RepairRecord();
        Update(record);
        object first = Pending(record)!;
        for (int frame = 0; frame < 30; frame++) { Update(record); }
        Assert(ReferenceEquals(first, Pending(record)) && Delayed.Count == 0 && _vehicleRepairs == 0,
            "Thirty actual remote updates retain one original one-second grace period and queue no tasks.");
        MakeDue(record);
        record.LastUpdated = 1;
        Update(record);
        Assert(_vehicleRepairs == 1 && Pending(record) == null && Delayed.Count == 0,
            "The same remote car repairs once after its grace period even when no newer snapshot needs applying.");
        Update(record);
        Assert(_vehicleRepairs == 1, "Later updates do not repeat a completed repair.");

        foreach (int nextOwner in new[] { 1, 22 })
        {
            record = RepairRecord();
            Update(record);
            MakeDue(record);
            SetOwner(record, nextOwner);
            Assert(Pending(record) == null, "The real owner setter cancels pending repair before either local or remote ownership changes.");
            if (nextOwner == 22)
            {
                SetOwner(record, 11);
                Update(record);
                Assert(_vehicleRepairs == 0 && Pending(record) != null,
                    "Returning to the previous remote owner starts a fresh grace period.");
            }
            else
            {
                Update(record);
                Assert(_vehicleRepairs == 0 && Pending(record) == null, "A locally owned car cannot schedule or receive the old remote repair.");
            }
        }
        foreach (bool sameHandle in new[] { false, true })
        {
            record = RepairRecord();
            Update(record);
            MakeDue(record);
            typeof(SyncedVehicle).GetProperty("MainVehicle", Instance)!.SetValue(record, NativeVehicle(sameHandle ? 920001 : 920002));
            Update(record);
            Assert(_vehicleRepairs == 0 && Pending(record) != null,
                "Replacing the native vehicle, including a reused handle, requires its own full grace period.");
        }
        record = RepairRecord();
        Update(record);
        MakeDue(record);
        var replacement = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        Vehicles[record.ID] = replacement;
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) == null, "Reusing a network ID cannot authorize the removed record's repair.");

        record = RepairRecord();
        Update(record);
        MakeDue(record);
        PlayerMap.Remove(11);
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) == null, "A disconnected owner's record cannot complete a delayed repair.");

        record = RepairRecord();
        Update(record);
        MakeDue(record);
        Host.GetField("LocalPlayerID", Static)!.SetValue(null, 0);
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) == null, "A disconnected local session cannot complete a repair.");

        record = RepairRecord();
        Update(record);
        MakeDue(record);
        _dead = false;
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) == null, "A car already restored elsewhere cancels the pending repair.");

        record = RepairRecord();
        Update(record);
        MakeDue(record);
        PropertyInfo flags = typeof(SyncedVehicle).GetProperty("Flags", Instance)!;
        flags.SetValue(record, Enum.Parse(flags.PropertyType, "IsDead"));
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) == null, "An authoritative destruction cancels pending resurrection.");

        record = RepairRecord();
        Update(record);
        MakeDue(record);
        EngineDamageGuard.Uninstall();
        Assert(new[] { EngineDamageGuard.FindTarget()!, EngineDamageGuard.FindCaptureTarget()!, EngineDamageGuard.FindUpdateTarget()!, EngineDamageGuard.FindOwnerTarget()! }
            .All(target => Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.enginedamage") != true),
            "Unload removes all four damage targets and drops the pending session state.");
        Assert(EngineDamageGuard.Install(), "A later resource session can install cleanly.");
        Update(record);
        Assert(_vehicleRepairs == 0 && Pending(record) != null && Delayed.Count == 0,
            "A reloaded resource starts a fresh grace period with no surviving deferred callback.");
    }

    private static object? Pending(SyncedVehicle record)
    {
        object table = typeof(EngineDamageGuard).GetField("_pendingRepairs", Static)!.GetValue(null)!;
        object?[] arguments = { record, null };
        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)! ? arguments[1] : null;
    }
    private static void MakeDue(SyncedVehicle record)
    {
        object pending = Pending(record) ?? throw new InvalidOperationException("No pending grace period.");
        pending.GetType().GetField("Started", Instance)!.SetValue(pending, Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency);
    }

    private static Dictionary<int, SyncedVehicle> Vehicles => (Dictionary<int, SyncedVehicle>)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!;
    private static Dictionary<int, RageCoop.Client.Player> PlayerMap => (Dictionary<int, RageCoop.Client.Player>)Players.GetField("Players", Static)!.GetValue(null)!;
    private static SyncedVehicle RepairRecord()
    {
        ResetNative();
        Delayed.Clear();
        Vehicles.Clear();
        Host.GetField("LocalPlayerID", Static)!.SetValue(null, 1);
        PlayerMap.Clear();
        foreach (int id in new[] { 1, 11, 22 })
        {
            var player = (RageCoop.Client.Player)FormatterServices.GetUninitializedObject(typeof(RageCoop.Client.Player));
            typeof(RageCoop.Client.Player).GetProperty("ID", Instance)!.SetValue(player, id);
            PlayerMap.Add(id, player);
        }
        var record = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        typeof(SyncedEntity).GetProperty("ID", Instance)!.SetValue(record, 2001);
        typeof(SyncedEntity).GetProperty("Model", Instance)!.SetValue(record, new Model(123));
        typeof(SyncedVehicle).GetProperty("MainVehicle", Instance)!.SetValue(record, NativeVehicle(920001));
        SetOwner(record, 11);
        Vehicles.Add(record.ID, record);
        _dead = true;
        return record;
    }
    private static void SetOwner(SyncedVehicle record, int id) => typeof(SyncedEntity).GetProperty("OwnerID", Instance)!.SetValue(record, id);
    private static void Update(SyncedVehicle record) => typeof(SyncedVehicle).GetMethod("Update", Instance)!.Invoke(record, null);

    private static void CheckDoorRestoration()
    {
        ResetNative();
        Vehicle vehicle = NativeVehicle(910001);
        Apply(vehicle, doors: 1, windows: 4);
        Assert(_brokenDoors == 1 && _brokenWindows == 4 && _vehicleRepairs == 0,
            "The real original engine still applies an initial damaged snapshot.");
        Apply(vehicle, doors: 0, windows: 8, opened: 2, leftLight: true);
        Assert(_brokenDoors == 0 && _brokenWindows == 8 && _openedDoors == 2 && _leftLight && !_rightLight && _vehicleRepairs == 1,
            "Clearing a missing door reconstructs it once, then the real original engine reapplies remaining windows, open doors and headlights.");
        Assert(_engine == 1000f && _body == 675f && _tank == 430f && _dirt == 12f && !_running,
            "Cosmetic repair at engine health 1000 preserves body/tank health, dirt and engine running state.");

        _brokenDoors |= 2;
        _brokenWindows |= 1;
        _body = 400f;
        for (int duplicate = 0; duplicate < 3; duplicate++) { Apply(vehicle, 0, 8, 2, true); }
        Assert((_brokenDoors & 2) != 0 && (_brokenWindows & 1) != 0 && _body == 400f && _vehicleRepairs == 1,
            "Unchanged authoritative snapshots do not repeatedly repair later local collision damage.");

        Apply(vehicle, doors: 1, windows: 4);
        Apply(vehicle, doors: 0, windows: 0);
        Assert(_brokenDoors == 0 && _brokenWindows == 0 && _body == 400f && _vehicleRepairs == 2,
            "A late old damaged snapshot followed by a clean full snapshot recovers without needing the one-shot Repaired flag.");
    }

    private static void CheckWindowRestoration()
    {
        ResetNative();
        Vehicle vehicle = NativeVehicle(910002);
        Apply(vehicle, doors: 0, windows: 4);
        Apply(vehicle, doors: 0, windows: 0);
        Assert(_brokenWindows == 0 && _windowRepairs == 1 && _vehicleRepairs == 0 && _body == 675f,
            "A cleared window bit repairs only that window without a full vehicle repair.");
        _brokenWindows = 4;
        Apply(vehicle, doors: 0, windows: 0);
        Assert(_brokenWindows == 4 && _windowRepairs == 1,
            "Repeating a clean snapshot does not make windows immune to subsequent damage.");
    }

    private static void CheckInitialMismatchAndDeath()
    {
        ResetNative();
        _brokenDoors = 1;
        _brokenWindows = 4;
        Apply(NativeVehicle(910003), doors: 0, windows: 0);
        Assert(_brokenDoors == 0 && _brokenWindows == 0 && _vehicleRepairs == 1,
            "The first authoritative snapshot can also correct an already damaged local representation.");
        ResetNative();
        _brokenDoors = 1;
        _dead = true;
        Apply(NativeVehicle(910004), doors: 0, windows: 0);
        Assert(_vehicleRepairs == 0 && _brokenDoors == 1 && _dead,
            "A cosmetic damage transition cannot resurrect a destroyed vehicle.");
    }

    private static void CheckWindowDoorIndices()
    {
        for (int index = 0; index < 8; index++)
        {
            ResetNative();
            Vehicle vehicle = NativeVehicle(910010 + index);
            byte bit = (byte)(1 << index);
            Apply(vehicle, doors: bit, windows: bit);
            if (index >= 4)
            {
                Assert((_brokenWindows & bit) != 0,
                    "Broken non-door window " + index + " is applied even while the same numbered body panel is broken.");
            }
            _brokenWindows |= bit;
            Apply(vehicle, doors: bit, windows: 0);
            Assert(_windowRepairs == (index < 4 ? 0 : 1) && _vehicleRepairs == 0 && _brokenDoors == bit &&
                _brokenWindows == (index < 4 ? bit : 0),
                "Window " + index + " restoration depends on a matching broken door only for door windows 0–3.");
        }
    }

    private static void Apply(Vehicle vehicle, byte doors, byte windows, byte opened = 0, bool leftLight = false)
    {
        MethodInfo target = EngineDamageGuard.FindTarget()!;
        Type modelType = target.GetParameters()[1].ParameterType;
        object model = Activator.CreateInstance(modelType)!;
        modelType.GetProperty("BrokenDoors")!.SetValue(model, doors);
        modelType.GetProperty("BrokenWindows")!.SetValue(model, windows);
        modelType.GetProperty("OpenedDoors")!.SetValue(model, opened);
        modelType.GetProperty("LeftHeadLightBroken")!.SetValue(model, (byte)(leftLight ? 1 : 0));
        target.Invoke(null, new object[] { vehicle, model, true });
    }

    private static void InstallNativeBoundary(Harmony harmony)
    {
        void Method(Type type, string method, string prefix, params Type[] arguments)
        {
            MethodInfo target = type.GetMethod(method, Instance, null, arguments, null) ?? throw new InvalidOperationException("Missing native boundary " + type.Name + "." + method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
            NativeTargets.Add(target);
        }
        foreach (string name in new[] { "EngineHealth", "BodyHealth", "PetrolTankHealth", "DirtLevel" })
        {
            Method(typeof(Vehicle), "get_" + name, nameof(ReadFloat));
            Method(typeof(Vehicle), "set_" + name, nameof(WriteFloat), typeof(float));
        }
        Method(typeof(Vehicle), "get_IsEngineRunning", nameof(ReadRunning));
        Method(typeof(Vehicle), "set_IsEngineRunning", nameof(WriteRunning), typeof(bool));
        Method(typeof(Entity), "get_IsDead", nameof(ReadDead));
        Method(typeof(Vehicle), "Repair", nameof(RepairVehicle));
        Method(typeof(VehicleDoor), "get_IsBroken", nameof(ReadDoorBroken));
        Method(typeof(VehicleDoor), "get_IsOpen", nameof(ReadDoorOpen));
        Method(typeof(VehicleDoor), "Break", nameof(BreakDoor), typeof(bool));
        Method(typeof(VehicleDoor), "Open", nameof(OpenDoor), typeof(bool), typeof(bool));
        Method(typeof(VehicleDoor), "Close", nameof(CloseDoor), typeof(bool));
        Method(typeof(VehicleDoorCollection), "GetEnumerator", nameof(ReadDoors));
        Method(typeof(VehicleWindow), "get_IsIntact", nameof(ReadWindowIntact));
        Method(typeof(VehicleWindow), "Smash", nameof(SmashWindow));
        Method(typeof(VehicleWindow), "Repair", nameof(RepairWindow));
        Method(typeof(VehicleWheelCollection), "GetEnumerator", nameof(ReadWheels));
        Method(typeof(Vehicle), "get_IsLeftHeadLightBroken", nameof(ReadLeftLight));
        Method(typeof(Vehicle), "get_IsRightHeadLightBroken", nameof(ReadRightLight));
        Method(typeof(Entity), "get_Model", nameof(ReadModel));
        Method(typeof(Vehicle), "Exists", nameof(ReadExists));
        MethodInfo movie = typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!;
        harmony.Patch(movie, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
        NativeTargets.Add(movie);
        MethodInfo display = typeof(SyncedVehicle).GetMethod("DisplayVehicle", Instance)!;
        harmony.Patch(display, prefix: new HarmonyMethod(typeof(Program), nameof(SkipDisplay)));
        NativeTargets.Add(display);
        MethodInfo update = typeof(SyncedVehicle).GetMethod("Update", Instance)!;
        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateDeferredRepair)) { priority = Priority.Last });
        NativeTargets.Add(update);
        MethodInfo damage = EngineDamageGuard.FindTarget()!;
        harmony.Patch(damage, transpiler: new HarmonyMethod(typeof(Program), nameof(SubstituteNativeDamageBoundary)) { priority = Priority.Last });
        NativeTargets.Add(damage);
        MethodInfo capture = EngineDamageGuard.FindCaptureTarget()!;
        harmony.Patch(capture, transpiler: new HarmonyMethod(typeof(Program), nameof(SubstituteNativeDamageBoundary)) { priority = Priority.Last });
        NativeTargets.Add(capture);
    }

    private static IEnumerable<CodeInstruction> IsolateDeferredRepair(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        int fire = codes.FindIndex(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Entity) && method.Name == "get_IsOnFire");
        if (fire < 2) { throw new InvalidOperationException("The unrelated Update boundary moved."); }
        var end = new CodeInstruction(OpCodes.Ret);
        foreach (CodeInstruction code in codes.Skip(fire - 2)) { end.labels.AddRange(code.labels); }
        codes.RemoveRange(fire - 2, codes.Count - fire + 2);
        codes.Add(end);
        foreach (CodeInstruction code in codes)
        {
            if (code.operand is MethodInfo boundary && boundary.DeclaringType == typeof(Vehicle) &&
                (boundary.Name == "get_SteeringAngle" || boundary.Name == "set_ThrottlePower" || boundary.Name == "set_BrakePower"))
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(boundary.Name == "get_SteeringAngle" ? nameof(ReadSteering) : nameof(IgnoreFloat), Static)!;
            }
            if (code.operand is MethodInfo method && method.DeclaringType == Host && method.Name == "Delay")
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(nameof(CaptureDelay), Static)!;
            }
        }
        return codes;
    }
    private static void CaptureDelay(Action action, int milliseconds)
    {
        if (milliseconds != 1000) { throw new InvalidOperationException("The installed repair grace period changed."); }
        Delayed.Add(action);
    }
    private static bool ReadModel(ref Model __result) { __result = new Model(123); return false; }
    private static bool ReadExists(ref bool __result) { __result = true; return false; }
    private static float ReadSteering(Vehicle vehicle) => 0f;
    private static void IgnoreFloat(Vehicle vehicle, float value) { }
    private static bool SkipMovie(ref Scaleform? __result) { __result = null; return false; }
    private static bool SkipDisplay() => false;

    private static IEnumerable<CodeInstruction> SubstituteNativeDamageBoundary(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(Vehicle) &&
                (method.Name == "set_IsLeftHeadLightBroken" || method.Name == "set_IsRightHeadLightBroken"))
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(method.Name == "set_IsLeftHeadLightBroken" ? nameof(ApplyLeftLight) : nameof(ApplyRightLight), BindingFlags.Static | BindingFlags.NonPublic)!;
            }
            if (code.operand is MethodInfo wheelMethod)
            {
                string? boundary = wheelMethod.DeclaringType == typeof(VehicleWheelCollection) && wheelMethod.Name == "GetAllWheels" ? nameof(ReadAllWheels) :
                    wheelMethod.DeclaringType != typeof(VehicleWheel) ? null : wheelMethod.Name switch
                    {
                        "get_IsBursted" => nameof(ReadTyreBurst),
                        "Puncture" => nameof(PunctureTyre),
                        "Burst" => nameof(BurstTyre),
                        "Fix" => nameof(FixTyre),
                        _ => null
                    };
                if (boundary != null)
                {
                    code.opcode = OpCodes.Call;
                    code.operand = typeof(Program).GetMethod(boundary, Static)!;
                }
            }
            yield return code;
        }
    }

    private static bool ReadFloat(MethodBase __originalMethod, ref float __result)
    {
        __result = __originalMethod.Name switch
        {
            "get_EngineHealth" => _engine,
            "get_BodyHealth" => _body,
            "get_PetrolTankHealth" => _tank,
            "get_DirtLevel" => _dirt,
            _ => throw new InvalidOperationException("Unexpected float getter.")
        };
        return false;
    }

    private static bool WriteFloat(MethodBase __originalMethod, float __0)
    {
        switch (__originalMethod.Name)
        {
            case "set_EngineHealth": _engine = __0; break;
            case "set_BodyHealth": _body = __0; break;
            case "set_PetrolTankHealth": _tank = __0; break;
            case "set_DirtLevel": _dirt = __0; break;
            default: throw new InvalidOperationException("Unexpected float setter.");
        }
        return false;
    }

    private static bool ReadRunning(ref bool __result) { __result = _running; return false; }
    private static bool WriteRunning(bool __0) { _running = __0; return false; }
    private static bool ReadDead(ref bool __result) { __result = _dead; return false; }
    private static void ApplyLeftLight(Vehicle vehicle, bool broken) { _leftLight = broken; }
    private static void ApplyRightLight(Vehicle vehicle, bool broken) { _rightLight = broken; }
    private static bool ReadDoorBroken(VehicleDoor __instance, ref bool __result) { __result = (_brokenDoors & (1 << (int)__instance.Index)) != 0; return false; }
    private static bool ReadDoorOpen(VehicleDoor __instance, ref bool __result) { __result = (_openedDoors & (1 << (int)__instance.Index)) != 0; return false; }
    private static bool BreakDoor(VehicleDoor __instance) { _brokenDoors |= 1 << (int)__instance.Index; return false; }
    private static bool OpenDoor(VehicleDoor __instance) { _openedDoors |= 1 << (int)__instance.Index; return false; }
    private static bool CloseDoor(VehicleDoor __instance) { _openedDoors &= ~(1 << (int)__instance.Index); return false; }
    private static bool ReadWindowIntact(VehicleWindow __instance, ref bool __result) { __result = (_brokenWindows & (1 << (int)__instance.Index)) == 0; return false; }
    private static bool SmashWindow(VehicleWindow __instance) { _brokenWindows |= 1 << (int)__instance.Index; return false; }
    private static bool RepairWindow(VehicleWindow __instance) { _brokenWindows &= ~(1 << (int)__instance.Index); _windowRepairs++; return false; }
    private static bool ReadDoors(VehicleDoorCollection __instance, ref IEnumerator<VehicleDoor> __result)
    {
        __result = Enumerable.Range(0, 8).Select(index => __instance[(VehicleDoorIndex)index]).GetEnumerator();
        return false;
    }
    private static bool ReadWheels(ref IEnumerator<VehicleWheel> __result) { __result = ((IEnumerable<VehicleWheel>)_wheels).GetEnumerator(); return false; }
    private static VehicleWheel[] ReadAllWheels(VehicleWheelCollection collection) => _wheels;
    private static bool ReadTyreBurst(VehicleWheel wheel) => (_burstedTyres & (1 << (int)wheel.BoneId)) != 0;
    private static void PunctureTyre(VehicleWheel wheel, float damage)
    {
        if (damage != 1000f) { throw new InvalidOperationException("Unexpected tyre puncture amount."); }
        _tyrePunctures++;
    }
    private static void BurstTyre(VehicleWheel wheel) { _burstedTyres |= 1 << (int)wheel.BoneId; _tyreBursts++; }
    private static void FixTyre(VehicleWheel wheel) { _burstedTyres &= ~(1 << (int)wheel.BoneId); _tyreFixes++; }
    private static bool ReadLeftLight(ref bool __result) { __result = _leftLight; return false; }
    private static bool ReadRightLight(ref bool __result) { __result = _rightLight; return false; }

    private static bool RepairVehicle(Vehicle __instance)
    {
        _vehicleRepairs++;
        _lastRepairHandle = __instance.Handle;
        _dead = false;
        _brokenDoors = _brokenWindows = _openedDoors = 0;
        _engine = _body = _tank = 1000f;
        _dirt = 0f;
        _running = true;
        return false;
    }

    private static void ResetNative()
    {
        _brokenDoors = _brokenWindows = _openedDoors = _vehicleRepairs = _windowRepairs = 0;
        _burstedTyres = _tyrePunctures = _tyreBursts = _tyreFixes = 0;
        _wheels = Array.Empty<VehicleWheel>();
        _engine = 1000f;
        _body = 675f;
        _tank = 430f;
        _dirt = 12f;
        _running = _dead = _leftLight = _rightLight = false;
    }

    private static Vehicle NativeVehicle(int handle) =>
        (Vehicle)typeof(Vehicle).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
        Console.WriteLine("PASS " + message);
    }
}
