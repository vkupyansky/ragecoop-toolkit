using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Input;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using LemonUI.Menus;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using RageCoopAdmin.Client;

internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string GuardId = "friendslegacy.ragecoop.passengerseat";
    private static readonly Type Host = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Main", true)!;
    private static readonly MethodInfo KeyDown = Host.GetMethod("OnKeyDown", Instance)!;
    private static readonly MethodInfo HostTick = Host.GetMethod("OnTick", Instance)!;
    private static readonly MethodInfo Display = typeof(SyncedPed).GetMethod("DisplayInVehicle", Instance)!;
    private static readonly List<MethodBase> Boundaries = new List<MethodBase>();
    private static readonly Dictionary<MethodInfo, DynamicMethod> Stubs = new Dictionary<MethodInfo, DynamicMethod>();
    private static readonly List<int> CheckedSeats = new List<int>();
    private static readonly List<ControlAction> ReadControls = new List<ControlAction>();
    private static GTA.Player _player = null!;
    private static Ped _ped = null!;
    private static Vehicle _vehicle = null!;
    private static Vehicle? _entering;
    private static Vehicle? _current;
    private static Vehicle[] _nearby = Array.Empty<Vehicle>();
    private static readonly Dictionary<int, (Vector3 minimum, Vector3 maximum, Vector3 local)> Bounds = new Dictionary<int, (Vector3, Vector3, Vector3)>();
    private static Ped? _occupant;
    private static Ped? _driver;
    private static readonly Type Pool = Host.Assembly.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly Type PlayerList = Host.Assembly.GetType("RageCoop.Client.PlayerList", true)!;
    private static bool _online = true, _chat, _hostMenu, _resourceBlocked, _dead, _inVehicle;
    private static bool _pressed, _justPressed, _disabledInput, _driverOccupied = true;
    private static int _capacity = 3, _targetSeat = -1, _freeSeat, _entries, _clears, _suppressed, _lookups, _notices, _remoteDisplays, _chatKeys;
    private static int _lastSeat = -2, _lastVehicle;

    private static int Main()
    {
        var boundary = new Harmony("friendslegacy.check.vehicleentryboundary");
        try
        {
            _player = (GTA.Player)typeof(GTA.Player).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { 0 });
            _ped = Native<Ped>(910001);
            _vehicle = Native<Vehicle>(910071);
            InstallBoundary(boundary);
            CheckShortcutRewrite();
            Assert(PassengerSeatGuard.Install(() => _resourceBlocked), "The guard installs against the actual client entry and seat methods.");
            PassengerSeatGuard.Install(() => false);
            Assert(Harmony.GetPatchInfo(KeyDown)?.Transpilers.Count(patch => patch.owner == GuardId) == 1 &&
                Harmony.GetPatchInfo(HostTick)?.Prefixes.Count(patch => patch.owner == GuardId) == 1,
                "Repeated installation preserves exactly one keyboard branch patch and one early engine tick prefix.");
            CheckRemovedPassengerKey();
            CheckOriginalSetting(boundary);
            CheckEntry();
            CheckDriverIdentity();
            CheckLargeVehicleEntry();
            CheckDriverPreservation();
            PassengerSeatGuard.Uninstall();
            PassengerSeatGuard.Uninstall();
            Assert(new[] { KeyDown, HostTick, Display }.All(method => Harmony.GetPatchInfo(method)?.Owners.Contains(GuardId) != true),
                "Unload removes all three guard patches.");
            Console.WriteLine("Actual .NET 4.8 vehicle entry guard, native Enter input, seat selection, cancellation and installed RageCoop shortcut checks passed; GTA boundaries were isolated.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            PassengerSeatSetting.Restore();
            PassengerSeatGuard.Uninstall();
            foreach (MethodBase method in Boundaries) { boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id); }
        }
    }

    private static void CheckShortcutRewrite()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(KeyDown);
        int comparison = PassengerSeatGuard.FindPassengerComparison(KeyDown, original);
        Assert(comparison >= 0, "The installed OnKeyDown has the exact PassengerKey comparison and final return branch.");
        List<CodeInstruction> rewritten = PassengerSeatGuard.DisablePassengerShortcut(original, KeyDown).ToList();
        Assert(rewritten.Count == original.Count && original.Where((code, index) => code.opcode != rewritten[index].opcode || !Equals(code.operand, rewritten[index].operand)).Count() == 1,
            "Only PassengerKey equality changes; chat, pause, menu and player-list instructions stay intact.");
        original[comparison + 3].opcode = OpCodes.Brtrue;
        Assert(PassengerSeatGuard.FindPassengerComparison(KeyDown, original) < 0, "A changed PassengerKey branch is rejected before patching.");
        bool rejected = false;
        try { PassengerSeatGuard.DisablePassengerShortcut(new[] { new CodeInstruction(OpCodes.Ret) }, KeyDown).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unknown client IL cannot receive a guessed shortcut rewrite.");
    }

    private static void CheckRemovedPassengerKey()
    {
        object host = FormatterServices.GetUninitializedObject(Host);
        Type settings = Host.Assembly.GetType("RageCoop.Client.Settings", true)!;
        object config = FormatterServices.GetUninitializedObject(settings);
        settings.GetProperty("MenuKey")!.SetValue(config, Keys.F9);
        Host.GetField("Settings", Static)!.SetValue(null, config);
        Type chat = Host.Assembly.GetType("RageCoop.Client.Chat", true)!;
        Host.GetField("MainChat", Static)!.SetValue(null, FormatterServices.GetUninitializedObject(chat));
        Reset();
        foreach (Keys key in new[] { Keys.G, Keys.H, Keys.F })
        {
            settings.GetProperty("PassengerKey")!.SetValue(config, key);
            KeyDown.Invoke(host, new object[] { host, new KeyEventArgs(key) });
            Assert(_entries == 0 && _clears == 0 && _lookups == 0, "The actual patched OnKeyDown never starts or cancels entry through the old configurable PassengerKey.");
        }
        _chat = true;
        KeyDown.Invoke(host, new object[] { host, new KeyEventArgs(Keys.G) });
        Assert(_chatKeys == 1, "G still reaches ordinary chat input when chat is focused.");
    }

    private static void CheckEntry()
    {
        foreach (int capacity in new[] { 3, 1, 5 })
        {
            Reset();
            _capacity = capacity;
            _freeSeat = capacity - 1;
            Press();
            Tick();
            Assert(_entries == 1 && _lastSeat == capacity - 1 && _lastVehicle == _vehicle.Handle && _suppressed == 1 && CheckedSeats.All(seat => seat >= 0 && seat < capacity),
                "A car, plane or helicopter capacity selects only a valid free passenger seat and blocks the ordinary driver entry.");
            Assert(ReadControls.All(action => action == ControlAction.Enter) && ReadControls.Count > 0,
                "Entry reads the native GTA Enter action, so keyboard and gamepad remaps share one path.");
            _justPressed = false;
            Tick();
            Assert(_entries == 1 && _lookups == 1 && _suppressed == 2, "Holding Enter cannot repeat the task or rescan the vehicle pool.");
            _pressed = false;
            Tick();
            Press();
            Tick();
            Assert(_clears == 1 && _entries == 1, "A new Enter press cancels our active passenger approach without selecting another seat.");
        }
        Reset();
        for (int frame = 0; frame < 60; frame++) { Tick(); }
        Assert(_lookups == 0 && CheckedSeats.Count == 0 && _entries == 0, "Idle frames never scan the vehicle pool or enumerate passenger seats.");
        Press();
        Tick();
        _inVehicle = true;
        Press();
        Tick();
        Assert(_entries == 1 && _clears == 0 && _suppressed == 1, "After completing passenger entry the same action is immediately available for ordinary exit.");
        Reset();
        _disabledInput = true;
        Press();
        Tick();
        Assert(_entries == 1 && _suppressed == 1, "Disabled native input is read while the guard suppresses the standard Enter action.");
        Reset();
        _driverOccupied = false;
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0 && CheckedSeats.Count == 0, "A vehicle without a driver keeps GTA's ordinary driver entry.");
        Reset();
        _inVehicle = true;
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0 && ReadControls.Count == 0, "A seated player keeps the ordinary exit action.");
        foreach (Action block in new Action[] { () => _chat = true, () => _hostMenu = true, () => _resourceBlocked = true, () => _dead = true, () => _online = false })
        {
            Reset();
            block();
            Press();
            Tick();
            Assert(_entries == 0 && _suppressed == 0 && _lookups == 0, "Chat, host/resource menus, pause/text-input blocking, death and disconnect cannot start or suppress entry.");
        }
        foreach (int capacity in new[] { 0, 3 })
        {
            Reset();
            _capacity = capacity;
            _freeSeat = -1;
            Press();
            Tick();
            _justPressed = false;
            Tick();
            Assert(_entries == 0 && _suppressed == 2 && _lastSeat != -1, "A full or single-seat occupied vehicle cannot fall back to carjacking its driver.");
        }
        Reset();
        _entering = _vehicle;
        _nearby = Array.Empty<Vehicle>();
        _targetSeat = -1;
        Press();
        Tick();
        Assert(_entries == 1 && _lastSeat == 0 && _clears == 1 && _lookups == 0 && _suppressed == 1,
            "An existing GTA driver target redirects before engine sync, including an aircraft outside the nearby fallback radius.");
        Reset();
        Press();
        Tick();
        _justPressed = false;
        _occupant = Native<Ped>(910099);
        Tick();
        Assert(_clears == 1 && _entries == 1, "If the chosen passenger seat becomes occupied, entry cancels before it can eject that occupant.");
        Reset();
        Press();
        Tick();
        _justPressed = false;
        typeof(PassengerSeatGuard).GetField("_entryStarted", Static)!.SetValue(null, System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 11);
        Tick();
        Assert(_clears == 1 && _entries == 1, "A passenger approach that exceeds its bounded timeout is cancelled without a repeated task.");
        Reset();
        _nearby = Array.Empty<Vehicle>();
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0, "Missing nearby transport leaves native input alone.");
    }

    private static Dictionary<int, SyncedPed> PedRecords => (Dictionary<int, SyncedPed>)Pool.GetField("PedsByID", Static)!.GetValue(null)!;
    private static Dictionary<int, SyncedPed> PedHandles => (Dictionary<int, SyncedPed>)Pool.GetField("PedsByHandle", Static)!.GetValue(null)!;
    private static Dictionary<int, RageCoop.Client.Player> Roster => (Dictionary<int, RageCoop.Client.Player>)PlayerList.GetField("Players", Static)!.GetValue(null)!;

    private static SyncedPed AddDriverRecord(int id, int owner, Ped actual)
    {
        var record = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.ID))!.SetValue(record, id);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(record, owner);
        typeof(SyncedPed).GetProperty(nameof(SyncedPed.MainPed))!.SetValue(record, actual);
        PedRecords[id] = record;
        PedHandles[actual.Handle] = record;
        if (id == owner)
        {
            var peer = new RageCoop.Client.Player();
            typeof(RageCoop.Client.Player).GetProperty(nameof(RageCoop.Client.Player.ID))!.SetValue(peer, id);
            typeof(RageCoop.Client.Player).GetProperty(nameof(RageCoop.Client.Player.Character))!.SetValue(peer, record);
            Roster[id] = peer;
        }
        return record;
    }

    private static void CheckDriverIdentity()
    {
        Reset();
        PedHandles.Clear();
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0 && _clears == 0,
            "An ordinary unregistered NPC driver keeps GTA's native carjack action, rather than becoming a passenger target.");
        Reset();
        Ped? npc = _driver;
        SyncedPed record = AddDriverRecord(77, 2, npc!);
        Assert(!record.IsPlayer && Roster.ContainsKey(record.OwnerID), "The fixture is a real RageCoop NPC owned by a connected player.");
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0 && CheckedSeats.Count == 0,
            "A synced NPC sharing its owner's ID through OwnerID is not mistaken for that player's character.");
        _entering = _vehicle;
        _justPressed = false;
        Tick();
        Assert(_entries == 0 && _clears == 0 && _suppressed == 0,
            "An existing native GTA entry task may continue ejecting an NPC driver without redirection or cancellation.");
        Reset();
        Press();
        Tick();
        Assert(_entries == 1 && _suppressed == 1 && _lastSeat >= 0,
            "A current remote player driver is protected through the actual EntityPool and connected PlayerList records.");
        Reset();
        _driverOccupied = false;
        _driver = null;
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0, "An empty driver seat keeps ordinary driver entry.");
        Reset();
        SyncedPed previous = PedRecords[2];
        typeof(SyncedPed).GetProperty(nameof(SyncedPed.MainPed))!.SetValue(previous, Native<Ped>(910222));
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0,
            "A stale player handle mapping cannot classify a different current GTA occupant as that player.");
        Reset();
        previous = PedRecords[2];
        AddDriverRecord(2, 2, Native<Ped>(910222));
        PedHandles[_driver!.Handle] = previous;
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0,
            "A replaced SyncedPed left in the old handle table cannot protect an obsolete player body.");
        Reset();
        Roster.Remove(2);
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0, "A disconnected player's leftover ped record is not a current connected player.");
        Reset();
        typeof(RageCoop.Client.Player).GetProperty(nameof(RageCoop.Client.Player.Character))!.SetValue(Roster[2], null);
        Press();
        Tick();
        Assert(_entries == 1 && _suppressed == 1,
            "A temporarily missing Character link cannot authorize ejecting an otherwise confirmed connected player's current ped.");
        Reset();
        var ownedNpc = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.ID))!.SetValue(ownedNpc, 77);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(ownedNpc, 2);
        typeof(RageCoop.Client.Player).GetProperty(nameof(RageCoop.Client.Player.Character))!.SetValue(Roster[2], ownedNpc);
        Press();
        Tick();
        Assert(_entries == 1 && _suppressed == 1,
            "An owner's NPC accidentally linked as Character cannot remove protection from the confirmed real player at the wheel.");
        Reset();
        var replacement = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.ID))!.SetValue(replacement, 2);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(replacement, 2);
        typeof(SyncedPed).GetProperty(nameof(SyncedPed.MainPed))!.SetValue(replacement, Native<Ped>(910222));
        typeof(RageCoop.Client.Player).GetProperty(nameof(RageCoop.Client.Player.Character))!.SetValue(Roster[2], replacement);
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0, "A confirmed replacement player character cannot authenticate its old ped body.");
    }

    private static void CheckLargeVehicleEntry()
    {
        foreach (var dimensions in new[]
        {
            (new Vector3(-7f, -12f, -2f), new Vector3(7f, 12f, 3f), new Vector3(0f, 14f, 0f)),
            (new Vector3(-20f, -30f, -3f), new Vector3(20f, 30f, 8f), new Vector3(0f, 32f, 0f))
        })
        {
            Reset();
            Bounds[_vehicle.Handle] = dimensions;
            Press();
            Tick();
            Assert(_entries == 1 && _suppressed == 1 && _lookups == 1,
                "The first Enter press beside a large helicopter or plane protects its occupied pilot without waiting for GTA's entry target or approaching its distant origin.");
        }
        Reset();
        Bounds[_vehicle.Handle] = (new Vector3(-1f, -2f, -1f), new Vector3(1f, 2f, 1f), new Vector3(0f, 40f, 0f));
        Press();
        Tick();
        Assert(_entries == 0 && _suppressed == 0, "A vehicle in the 50 m candidate set is not entered when its actual model bounds are far away.");
        Reset();
        Bounds[_vehicle.Handle] = (new Vector3(-1f, -2f, -1f), new Vector3(1f, 2f, 1f), new Vector3(0f, 0f, 5f));
        Press();
        Tick();
        Assert(_entries == 0, "Vertical distance rejects a vehicle on another level even when its horizontal bounds overlap the player.");
        Reset();
        Vehicle distantCar = Native<Vehicle>(910072);
        _nearby = new[] { distantCar, _vehicle };
        Bounds[distantCar.Handle] = (new Vector3(-1f, -2f, -1f), new Vector3(1f, 2f, 1f), new Vector3(0f, 8f, 0f));
        Bounds[_vehicle.Handle] = (new Vector3(-20f, -30f, -3f), new Vector3(20f, 30f, 8f), new Vector3(0f, 32f, 0f));
        Press();
        Tick();
        Assert(_entries == 1 && _lastVehicle == _vehicle.Handle, "An unrelated vehicle with a nearer center cannot hide the large aircraft whose body is beside the player.");
        Vector3 minimum = new Vector3(-1f, -2f, -1f), maximum = new Vector3(1f, 2f, 1f);
        Assert(PassengerSeatGuard.DistanceToBoundsSquared(new Vector3(3f, 4f, 1f), minimum, maximum) == 8f,
            "Corner distance uses the Euclidean distance to the local model box, including both axes.");
        Assert(float.IsPositiveInfinity(PassengerSeatGuard.DistanceToBoundsSquared(new Vector3(float.NaN, 0f, 0f), minimum, maximum)) &&
            float.IsPositiveInfinity(PassengerSeatGuard.DistanceToBoundsSquared(Vector3.Zero, maximum, minimum)),
            "Invalid external geometry cannot authorize entry.");
    }

    private static void CheckOriginalSetting(Harmony boundary)
    {
        Assert(PassengerSeatSetting.HasSupportedContract(), "The installed SettingsMenu initializer adds exactly the known Passenger Key item to its own menu.");
        MethodInfo drawing = typeof(NativeMenu).GetMethod("UpdateItems", Instance)!;
        boundary.Patch(drawing, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuInitialization)));
        Boundaries.Add(drawing);
        NativeMenu menu = EmptyMenu();
        NativeItem before = BareItem(), passenger = BareItem(), after = BareItem(), unrelated = BareItem();
        menu.Items.AddRange(new[] { before, passenger, after, unrelated });
        typeof(NativeMenu).GetField("index", Instance)!.SetValue(menu, 2);
        Assert(PassengerSeatSetting.Hide(menu, passenger) && PassengerSeatSetting.Hide(menu, passenger), "Hiding the original setting is idempotent.");
        Assert(menu.Items.SequenceEqual(new[] { before, after, unrelated }) && ReferenceEquals(menu.SelectedItem, after),
            "Only the exact Passenger Key instance is removed; surrounding settings and the selected row survive.");
        NativeItem inserted = BareItem();
        menu.Items.Insert(1, inserted);
        menu.SelectedItem = after;
        NativeMenu replacement = EmptyMenu();
        replacement.Items.Add(unrelated);
        PassengerSeatSetting.Restore();
        PassengerSeatSetting.Restore();
        Assert(menu.Items.SequenceEqual(new[] { before, inserted, passenger, after, unrelated }) &&
            replacement.Items.SequenceEqual(new[] { unrelated }) && ReferenceEquals(menu.SelectedItem, after),
            "Unload restores the same item before its surviving next sibling, preserves the current selection and leaves a replaced menu untouched.");
        Assert(PassengerSeatSetting.Hide(menu, passenger), "A later resource load can hide the same upstream setting again.");
        menu.Items.Add(passenger);
        PassengerSeatSetting.Restore();
        Assert(menu.Items.Count(item => ReferenceEquals(item, passenger)) == 1, "If another script already restored the item, unload cannot duplicate it.");
    }

    private static NativeMenu EmptyMenu()
    {
        var menu = (NativeMenu)FormatterServices.GetUninitializedObject(typeof(NativeMenu));
        typeof(NativeMenu).GetField("<Items>k__BackingField", Instance)!.SetValue(menu, new List<NativeItem>());
        typeof(NativeMenu).GetField("maxItems", Instance)!.SetValue(menu, 10);
        return menu;
    }
    private static NativeItem BareItem() => (NativeItem)FormatterServices.GetUninitializedObject(typeof(NativeItem));
    private static bool SkipMenuInitialization() => false;

    private static void CheckDriverPreservation()
    {
        Reset();
        var remote = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.ID))!.SetValue(remote, 2);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(remote, 2);
        var syncedVehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        typeof(SyncedVehicle).GetProperty(nameof(SyncedVehicle.MainVehicle))!.SetValue(syncedVehicle, _vehicle);
        typeof(SyncedPed).GetProperty("CurrentVehicle", Instance)!.SetValue(remote, syncedVehicle);
        typeof(SyncedPed).GetField("Seat", Instance)!.SetValue(remote, VehicleSeat.Driver);
        _current = _vehicle;
        foreach (byte speed in new byte[] { 4, 5 })
        {
            remote.Speed = speed;
            Display.Invoke(remote, Array.Empty<object>());
        }
        Assert(_remoteDisplays == 0, "Actual remote DisplayInVehicle cannot replace the real local driver with a stale driver/entry state.");
        typeof(SyncedPed).GetField("Seat", Instance)!.SetValue(remote, VehicleSeat.Passenger);
        Display.Invoke(remote, Array.Empty<object>());
        Assert(_remoteDisplays == 1, "An actual remote passenger still reaches ordinary display processing.");
    }

    private static void Reset()
    {
        typeof(PassengerSeatGuard).GetMethod("ResetEntry", Static)!.Invoke(null, Array.Empty<object>());
        typeof(PassengerSeatGuard).GetField("_suppressUntilReleased", Static)!.SetValue(null, false);
        _online = true;
        _chat = _hostMenu = _resourceBlocked = _dead = _inVehicle = _pressed = _justPressed = _disabledInput = false;
        _driverOccupied = true;
        _driver = Native<Ped>(910002);
        PedRecords.Clear();
        PedHandles.Clear();
        Roster.Clear();
        AddDriverRecord(2, 2, _driver);
        _capacity = 3;
        _targetSeat = -1;
        _freeSeat = 0;
        _entries = _clears = _suppressed = _lookups = _notices = 0;
        _lastSeat = -2;
        _nearby = new[] { _vehicle };
        Bounds.Clear();
        Bounds.Add(_vehicle.Handle, (new Vector3(-1f, -2f, -1f), new Vector3(1f, 2f, 1f), new Vector3(2f, 0f, 0f)));
        _entering = _current = null;
        _occupant = null;
        CheckedSeats.Clear();
        ReadControls.Clear();
    }
    private static void Press() { _pressed = _justPressed = true; }
    private static void Tick()
    {
        typeof(PassengerSeatGuard).GetMethod("ProcessEnterControl", Static)!.Invoke(null, Array.Empty<object>());
        Assert(!(bool)typeof(PassengerSeatGuard).GetField("_reportedRuntimeFailure", Static)!.GetValue(null)!, "The actual guard completed without a hidden runtime failure.");
    }

    private static void InstallBoundary(Harmony harmony)
    {
        IEnumerable<MethodInfo> closures = typeof(PassengerSeatGuard).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(Instance | BindingFlags.DeclaredOnly)).Where(method => method.Name.Contains("BeginPassengerEntry"));
        IEnumerable<MethodInfo> helpers = typeof(PassengerSeatGuard).GetMethods(Static).Where(method => new[]
            { "HasPlayerDriver", "FindNearbyVehicle", "BeginPassengerEntry", "DisableEnter", "PreserveLocalDriver", "ReportRuntimeFailure", "ProcessEnterControl" }.Contains(method.Name))
            .OrderBy(method => method.Name == "ProcessEnterControl" ? 1 : 0);
        foreach (MethodInfo method in closures.Concat(helpers))
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateGtaCalls)));
            Boundaries.Add(method);
        }
        harmony.Patch(KeyDown, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateGtaCalls)) { priority = Priority.Last });
        Boundaries.Add(KeyDown);
        harmony.Patch(Display, transpiler: new HarmonyMethod(typeof(Program), nameof(CaptureDisplay)));
        Boundaries.Add(Display);
        MethodInfo failure = typeof(PassengerSeatGuard).GetMethod("ReportRuntimeFailure", Static)!;
        harmony.Patch(failure, prefix: new HarmonyMethod(typeof(Program), nameof(FailOnRuntimeError)));
        MethodInfo scaleform = typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!;
        harmony.Patch(scaleform, prefix: new HarmonyMethod(typeof(Program), nameof(SkipScaleform)));
        Boundaries.Add(scaleform);
    }

    private static IEnumerable<CodeInstruction> IsolateGtaCalls(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && IsBoundary(method))
            {
                code.opcode = OpCodes.Call;
                code.operand = NativeStub(method);
            }
            else if (code.operand is MethodInfo dimensions && dimensions.DeclaringType == typeof(Model) && dimensions.Name == nameof(Model.GetDimensions))
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(nameof(GetDimensions), Static)!;
            }
            yield return code;
        }
    }
    private static bool IsBoundary(MethodInfo method)
    {
        Type? type = method.DeclaringType;
        if (type == typeof(InputArgument) || type?.FullName == "GTA.PoolObject") { return false; }
        if (type == typeof(API) || type?.FullName == "RageCoop.Client.Chat" || type?.FullName == "RageCoop.Client.Voice") { return true; }
        if (type?.FullName == "RageCoop.Client.Networking") { return method.Name == "get_IsOnServer"; }
        if (type?.Assembly != typeof(Ped).Assembly || type.IsValueType) { return false; }
        return type != typeof(Entity) || (method.Name != "op_Equality" && method.Name != "op_Inequality");
    }

    private static DynamicMethod NativeStub(MethodInfo target)
    {
        if (Stubs.TryGetValue(target, out DynamicMethod? existing)) { return existing; }
        Type[] arguments = target.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (!target.IsStatic) { arguments = new[] { target.DeclaringType! }.Concat(arguments).ToArray(); }
        var stub = new DynamicMethod("EntryBoundary_" + Stubs.Count, target.ReturnType, arguments, typeof(Program), true);
        ILGenerator il = stub.GetILGenerator();
        string? field = target.Name switch
        {
            "get_IsOnServer" => nameof(_online),
            "get_IsChatFocused" or "get_Focused" => nameof(_chat),
            "get_IsMenuVisible" => nameof(_hostMenu),
            "get_Player" => nameof(_player),
            "get_Character" => nameof(_ped),
            "get_IsDead" => nameof(_dead),
            "IsInVehicle" => nameof(_inVehicle),
            "get_VehicleTryingToEnter" => nameof(_entering),
            "get_CurrentVehicle" => nameof(_current),
            "get_PassengerCapacity" => nameof(_capacity),
            _ => null
        };
        string? handler = target.Name switch
        {
            "DisableControlActionThisFrame" => nameof(Suppress),
            "IsSeatFree" => nameof(DriverSeatFree),
            "GetPedOnSeat" => nameof(Occupant),
            "EnterVehicle" => nameof(Enter),
            "ClearAll" => nameof(Clear),
            "GetNearbyVehicles" => nameof(Nearby),
            "get_Model" => nameof(GetModel),
            "GetPositionOffset" => nameof(PositionOffset),
            "LocalChatMessage" => nameof(Notice),
            "OnKeyDown" => nameof(ChatKey),
            _ => null
        };
        if (field != null) { il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(field, Static)!); }
        else if (target.Name == "get_SeatIndex") { il.Emit(OpCodes.Ldc_I4_M1); }
        else if (target.Name == "Exists") { il.Emit(OpCodes.Ldc_I4_1); }
        else if (target.DeclaringType == typeof(GTA.Game) && target.ReturnType == typeof(bool)) { il.Emit(OpCodes.Ldc_I4_0); }
        else if (target.Name == "IsControlPressed" || target.Name == "IsControlJustPressed" || target.Name == "IsDisabledControlPressed" || target.Name == "IsDisabledControlJustPressed")
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(target.Name.StartsWith("IsDisabled", StringComparison.Ordinal) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(target.Name.Contains("Just") ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(ReadControl), Static)!);
        }
        else if (target.DeclaringType == typeof(Function) && target.Name == "Call" && target.ReturnType != typeof(void))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, arguments.Length - 1);
            il.Emit(OpCodes.Newarr, typeof(InputArgument));
            for (int index = 1; index < arguments.Length; index++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index - 1);
                il.Emit(OpCodes.Ldarg, index);
                il.Emit(OpCodes.Stelem_Ref);
            }
            string nativeHandler = target.ReturnType == typeof(bool) ? nameof(SeatFree) : nameof(SeatTryingToEnter);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(nativeHandler, Static)!);
        }
        else if (handler != null)
        {
            MethodInfo method = typeof(Program).GetMethod(handler, Static)!;
            int offset = arguments.Length - method.GetParameters().Length;
            for (int index = offset; index < arguments.Length; index++) { il.Emit(OpCodes.Ldarg, index); }
            il.Emit(OpCodes.Call, method);
        }
        else if (target.ReturnType != typeof(void))
        {
            if (target.ReturnType.IsValueType)
            {
                LocalBuilder value = il.DeclareLocal(target.ReturnType);
                il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Initobj, target.ReturnType);
                il.Emit(OpCodes.Ldloc, value);
            }
            else { il.Emit(OpCodes.Ldnull); }
        }
        il.Emit(OpCodes.Ret);
        Stubs.Add(target, stub);
        return stub;
    }

    private static bool ReadControl(ControlAction action, bool disabled, bool edge)
    {
        ReadControls.Add(action);
        return disabled == _disabledInput && (edge ? _justPressed : _pressed);
    }
    private static void Suppress(ControlType type, ControlAction action, bool related)
    {
        Assert(type == ControlType.PlayerControl && action == ControlAction.Enter, "Only the on-foot Enter action may be disabled.");
        _suppressed++;
    }
    private static bool DriverSeatFree(VehicleSeat seat)
    {
        Assert(seat == VehicleSeat.Driver, "The driver occupancy lookup uses the driver seat.");
        return !_driverOccupied;
    }
    private static Ped? Occupant(VehicleSeat seat) => seat == VehicleSeat.Driver ? _driver : _occupant;
    private static bool SeatFree(Hash hash, InputArgument[] arguments)
    {
        Assert(hash == Hash.IS_VEHICLE_SEAT_FREE && arguments.Length == 3 && ReadArgument(arguments[0]) == _vehicle.Handle && ReadArgument(arguments[2]) == 1,
            "Passenger selection checks the target vehicle and considers an existing entry task as occupying its seat.");
        int seat = ReadArgument(arguments[1]);
        CheckedSeats.Add(seat);
        return seat == _freeSeat;
    }
    private static int SeatTryingToEnter(Hash hash, InputArgument[] arguments)
    {
        Assert(hash == Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER, "Only the real GTA target seat is read.");
        return _targetSeat;
    }
    private static int ReadArgument(InputArgument argument) => unchecked((int)(ulong)typeof(InputArgument).GetField("_data", Instance)!.GetValue(argument)!);
    private static void Enter(Vehicle vehicle, VehicleSeat seat, int timeout, float speed, EnterVehicleFlags flags)
    {
        Assert((int)seat >= 0 && timeout > 0 && (int)flags == 0, "Passenger entry uses an actual seat, bounded normal approach and no warp/carjack flags.");
        _entries++;
        _lastSeat = (int)seat;
        _lastVehicle = vehicle.Handle;
        _entering = vehicle;
    }
    private static void Clear() { _clears++; _entering = null; }
    private static Vehicle[] Nearby(Vector3 position, float radius, Model[] models)
    {
        Assert(radius == 50f && models.Length == 0, "The one-off candidate query includes large aircraft; model bounds decide entry proximity.");
        _lookups++;
        return _nearby;
    }
    private static Model GetModel(Entity entity) => new Model(entity.Handle);
    private static void GetDimensions(ref Model model, out Vector3 minimum, out Vector3 maximum)
    {
        (minimum, maximum, _) = Bounds[model.Hash];
    }
    private static Vector3 PositionOffset(Entity entity, Vector3 position) => Bounds[entity.Handle].local;
    private static void Notice(string sender, string message) { _notices++; }
    private static void ChatKey(Keys key) { _chatKeys++; }
    private static IEnumerable<CodeInstruction> CaptureDisplay(IEnumerable<CodeInstruction> original)
    {
        return new[] { new CodeInstruction(OpCodes.Call, typeof(Program).GetMethod(nameof(RecordDisplay), Static)!), new CodeInstruction(OpCodes.Ret) };
    }
    private static void RecordDisplay() { _remoteDisplays++; }
    private static bool SkipScaleform(ref Scaleform? __result) { __result = null; return false; }
    private static bool FailOnRuntimeError(Exception ex) => throw new InvalidOperationException("The entry guard reached its runtime failure boundary.", ex);
    private static T Native<T>(int handle) where T : Entity => (T)typeof(T).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });
    private static void Assert(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
