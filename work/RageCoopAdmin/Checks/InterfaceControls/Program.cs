using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CoopInterfaceCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckStorage(directory);
            CheckHeldShortcut();
            CheckShortcutMappings();
            CheckBumperChord();
            CheckCancelReleaseGuard();
            CheckOpeningChordReleaseGuard();
            CheckKeyboardBoost();
            CheckWorldTimeSelection();
            Console.WriteLine("Interface control checks passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckStorage(string directory)
    {
        var store = new InterfaceSettingsStore(directory);
        InterfaceSettings initial = store.Load();
        Assert(initial.Version == 5 && initial.RepairShortcut == 0 &&
            initial.ShowSpeedometer && initial.GamepadShortcuts && initial.KeyboardBoost,
            "new profile uses safe keyboard choices and visible HUD");
        Assert(!Directory.EnumerateFiles(directory).Any(), "loading a new profile does not create a settings file");
        string unrelatedPath = Path.Combine(directory, "unrelated.xml");
        const string unrelated = "<OtherSettings>keep me</OtherSettings>";
        File.WriteAllText(unrelatedPath, unrelated);
        var chosen = new InterfaceSettings
        {
            RepairShortcut = 1,
            ShowSpeedometer = false,
            GamepadShortcuts = false,
            KeyboardBoost = false
        };
        store.Save(chosen);
        InterfaceSettings loaded = new InterfaceSettingsStore(directory).Load();
        Assert(loaded.RepairShortcut == 1 &&
            !loaded.ShowSpeedometer && !loaded.GamepadShortcuts && !loaded.KeyboardBoost,
            "personal choices survive reopen");
        Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, loaded) == InterfaceShortcut.Menu &&
            InterfaceShortcutMatcher.Match(Keys.K, Keys.None, loaded) == InterfaceShortcut.None,
            "menu remains available after reopening a profile with all optional controls disabled");
        store.Save(new InterfaceSettings { RepairShortcut = 0 });
        Assert(store.Load().RepairShortcut == 0,
            "atomic replacement can reenable repair without changing the menu binding");
        string path = Path.Combine(directory, "interface.xml");
        string previous = File.ReadAllText(path);
        Assert(previous.Contains("version=\"5\"") && !previous.Contains("MenuShortcut"),
            "new saves use v5 without a configurable menu shortcut");
        MustReject(() => store.Save(new InterfaceSettings { RepairShortcut = 2 }), "removed shortcut choice rejected");
        MustReject(() => store.Save(new InterfaceSettings { RepairShortcut = -1 }), "negative repair shortcut choice rejected");
        MustReject(() => store.Save(new InterfaceSettings { Version = 4 }), "saving requires the current settings format");
        Assert(File.ReadAllText(path) == previous, "invalid save preserves previous choices");
        CheckLegacyStorage(store, path);
        File.WriteAllText(path, "<Interface version=\"5\"><RepairShortcut>2</RepairShortcut></Interface>");
        MustReject(() => store.Load(), "current format cannot restore a removed repair shortcut choice");
        File.WriteAllText(path, "<Interface version=\"99\"><MenuShortcut>0</MenuShortcut></Interface>");
        string unsupported = File.ReadAllText(path);
        MustReject(() => store.Load(), "future format rejected");
        Assert(File.ReadAllText(path) == unsupported, "failed read preserves file");
        File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///never-read-this\">]><Interface version=\"1\"/>");
        MustReject(() => store.Load(), "DTD rejected");
        File.WriteAllText(path, new string(' ', 17 * 1024));
        MustReject(() => store.Load(), "oversized file rejected");
        Assert(!Directory.GetFiles(directory, "*.tmp").Any(), "temporary writes cleaned");
        Assert(File.ReadAllText(unrelatedPath) == unrelated && Directory.GetFiles(directory).Length == 2,
            "settings operations preserve unrelated files and create no duplicate profiles");
    }

    private static void CheckLegacyStorage(InterfaceSettingsStore store, string path)
    {
        for (int version = 1; version <= 4; version++)
        {
            int maximumChoice = version <= 3 ? 2 : 1;
            for (int menu = 0; menu <= maximumChoice; menu++)
            {
                for (int repair = 0; repair <= maximumChoice; repair++)
                {
                    string legacy = LegacyProfileXml(version, menu, repair);
                    File.WriteAllText(path, legacy);
                    InterfaceSettings migrated = store.Load();
                    int expectedRepair = version <= 3 ? (repair == 2 ? 1 : 0) : repair;
                    Assert(migrated.Version == 5 && migrated.RepairShortcut == expectedRepair &&
                        !migrated.GamepadShortcuts && !migrated.ShowSpeedometer &&
                        migrated.KeyboardBoost == (version <= 2),
                        $"v{version} migrates menu choice {menu} and repair choice {repair} without resetting preferences");
                    Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, migrated) ==
                        InterfaceShortcut.Menu &&
                        InterfaceShortcutMatcher.Match(Keys.K, Keys.None, migrated) ==
                        (expectedRepair == 1 ? InterfaceShortcut.None : InterfaceShortcut.Repair) &&
                        InterfaceShortcutMatcher.Match(Keys.U, Keys.None, migrated) == InterfaceShortcut.None &&
                        InterfaceShortcutMatcher.Match(Keys.I, Keys.None, migrated) == InterfaceShortcut.None,
                        "migrated profile always opens on J and preserves whether K is enabled");
                    Assert(File.ReadAllText(path) == legacy, "migration read preserves the stored profile");
                    store.Save(migrated);
                    InterfaceSettings reopened = store.Load();
                    string saved = File.ReadAllText(path);
                    Assert(reopened.Version == 5 && reopened.RepairShortcut == migrated.RepairShortcut &&
                        reopened.KeyboardBoost == migrated.KeyboardBoost &&
                        !reopened.GamepadShortcuts && !reopened.ShowSpeedometer &&
                        InterfaceShortcutMatcher.Match(Keys.J, Keys.None, reopened) == InterfaceShortcut.Menu &&
                        saved.Contains("version=\"5\"") && !saved.Contains("MenuShortcut") &&
                        !saved.Contains("ShowPlayersHud"),
                        "next save drops the obsolete menu choice and preserves preferences across reopen");
                }
            }
        }

        File.WriteAllText(path, LegacyProfileXml(3, 1, 2).Replace(" version=\"3\"", string.Empty));
        InterfaceSettings unversioned = store.Load();
        Assert(unversioned.Version == 5 && unversioned.RepairShortcut == 1 &&
            !unversioned.KeyboardBoost,
            "unversioned profiles retain the previous v3 interpretation during migration");
        for (int version = 1; version <= 4; version++)
        {
            string obsoleteMenu = LegacyProfileXml(version, 0, 0).Replace("<MenuShortcut>0</MenuShortcut>",
                "<MenuShortcut>unused</MenuShortcut>");
            File.WriteAllText(path, obsoleteMenu);
            InterfaceSettings migrated = store.Load();
            Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, migrated) == InterfaceShortcut.Menu &&
                File.ReadAllText(path) == obsoleteMenu,
                $"v{version} ignores the obsolete menu setting without rewriting the profile");
        }
        File.WriteAllText(path, LegacyProfileXml(3, 0, 3));
        string invalid = File.ReadAllText(path);
        MustReject(() => store.Load(), "unknown legacy repair choice rejected before migration");
        Assert(File.ReadAllText(path) == invalid, "invalid legacy profile remains intact");
        File.WriteAllText(path, LegacyProfileXml(4, 1, 2));
        MustReject(() => store.Load(), "v4 rejects invalid repair choices");
    }

    private static string LegacyProfileXml(int version, int menu, int repair) =>
        $"<Interface version=\"{version}\"><MenuShortcut>{menu}</MenuShortcut><RepairShortcut>{repair}</RepairShortcut>" +
        "<GamepadShortcuts>false</GamepadShortcuts><KeyboardBoost>false</KeyboardBoost>" +
        "<ShowPlayersHud>false</ShowPlayersHud><ShowSpeedometer>false</ShowSpeedometer></Interface>";

    private static void CheckHeldShortcut()
    {
        var held = new ShortcutLatch();
        Assert(held.TryPress(77), "first menu key press accepted");
        Assert(!held.TryPress(77), "Windows key repeat cannot retoggle menu");
        held.Release(82);
        Assert(!held.TryPress(77), "another key release cannot rearm menu");
        held.Release(77);
        Assert(held.TryPress(77), "new physical press accepted after release");
        held.Clear();
        Assert(held.TryPress(77), "cleanup clears stale held state");
    }

    private static void CheckShortcutMappings()
    {
        var settings = new InterfaceSettings();
        Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, settings) == InterfaceShortcut.Menu &&
            InterfaceShortcutMatcher.Match(Keys.K, Keys.None, settings) == InterfaceShortcut.Repair,
            "unmodified J and K trigger the default actions");
        foreach (Keys modifier in new[] { Keys.Control, Keys.Shift, Keys.Alt })
        {
            Assert(InterfaceShortcutMatcher.Match(Keys.J, modifier, settings) == InterfaceShortcut.None &&
                InterfaceShortcutMatcher.Match(Keys.K, modifier, settings) == InterfaceShortcut.None &&
                InterfaceShortcutMatcher.Match(Keys.O, modifier, settings) == InterfaceShortcut.None,
                "modified J, K and O cannot trigger shortcuts");
        }
        Assert(InterfaceShortcutMatcher.Match(Keys.M, Keys.Control, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.R, Keys.Control, settings) == InterfaceShortcut.None,
            "removed Ctrl shortcuts cannot toggle stealth through our actions");
        Assert(InterfaceShortcutMatcher.Match(Keys.F10, Keys.None, settings) == InterfaceShortcut.None,
            "F10 has no menu binding");
        Assert(InterfaceShortcutMatcher.Match(Keys.O, Keys.None, settings) == InterfaceShortcut.Boost,
            "held vehicle boost is enabled by default");
        settings.KeyboardBoost = true;
        Assert(InterfaceShortcutMatcher.Match(Keys.O, Keys.None, settings) == InterfaceShortcut.Boost &&
            InterfaceShortcutMatcher.Match(Keys.O, Keys.Control, settings) == InterfaceShortcut.None,
            "only bare O requests the enabled vehicle boost");
        Assert(InterfaceShortcutMatcher.Match(Keys.U, Keys.None, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.I, Keys.None, settings) == InterfaceShortcut.None,
            "U and I have no binding when keyboard shortcuts are enabled");
        settings.RepairShortcut = 1;
        Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, settings) == InterfaceShortcut.Menu &&
            InterfaceShortcutMatcher.Match(Keys.K, Keys.None, settings) == InterfaceShortcut.None,
            "disabling K preserves menu on J");
        settings.KeyboardBoost = false;
        settings.GamepadShortcuts = false;
        settings.ShowSpeedometer = false;
        Assert(InterfaceShortcutMatcher.Match(Keys.J, Keys.None, settings) == InterfaceShortcut.Menu &&
            InterfaceShortcutMatcher.Match(Keys.K, Keys.None, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.O, Keys.None, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.U, Keys.None, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.I, Keys.None, settings) == InterfaceShortcut.None &&
            InterfaceShortcutMatcher.Match(Keys.F10, Keys.None, settings) == InterfaceShortcut.None,
            "disabling optional controls keeps the menu available on J without hidden alternatives");
    }

    private static void CheckBumperChord()
    {
        var chord = new BumperShortcutChord();
        Assert(chord.Update(true, true) && chord.IsActive,
            "simultaneous Xbox bumpers activate the chord");
        Assert(!chord.Update(false, true) && !chord.IsActive && chord.Update(true, true),
            "either bumper order works without a hidden hold interval");
        chord.CancelUntilRelease();
        Assert(!chord.Update(true, true) && !chord.Update(true, false),
            "input focus prevents a held chord leaking into gameplay");
        chord.Update(false, false);
        Assert(chord.Update(true, true), "full release permits another intentional chord");
    }

    private static void CheckCancelReleaseGuard()
    {
        var guard = new MenuInputReleaseGuard();
        Assert(!guard.Update(false, true), "normal pause keys work before our menu opens");
        Assert(guard.Update(true, false) && guard.Update(true, true),
            "visible menu reserves the game's pause actions");
        Assert(guard.Update(false, true) && guard.Update(false, true),
            "closing menu keeps held Esc or B from opening the pause menu");
        Assert(guard.Update(false, false) && !guard.Update(false, false),
            "release frame remains reserved then normal pause returns");
        Assert(guard.Update(true, false) && guard.Update(false, false) && guard.Update(false, true),
            "a cancel still held after an intermediate release frame remains consumed");
    }

    private static void CheckOpeningChordReleaseGuard()
    {
        var guard = new MenuInputReleaseGuard();
        Assert(guard.Update(true, true) && guard.Update(true, true),
            "opening chord suppresses menu navigation while both bumpers remain held");
        Assert(guard.Update(false, true) && guard.Update(false, true),
            "releasing bumpers cannot turn the still-held opening direction into menu navigation");
        Assert(guard.Update(false, false) && !guard.Update(false, false),
            "navigation returns after the opening direction has been released");
    }

    private static void CheckKeyboardBoost()
    {
        Assert(KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, 1f / 60f, out float target) &&
            Math.Abs(target - 51f) < 0.0001f,
            "one 60 FPS step adds one metre per second at a fixed acceleration of sixty metres per second squared");
        Assert(KeyboardVehicleBoost.TryGetForwardSpeed(true, 0f, 1f / 60f, out float stopped) &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, 2f, 1f / 60f, out float rolling) &&
            Math.Abs(stopped - 4f) < 0.0001f && stopped == rolling,
            "a stopped or slow car launches from Native Trainer's three metres per second floor");

        float atThirtyFrames = SimulateBoost(50f, 30, 1);
        float atSixtyFrames = SimulateBoost(50f, 60, 1);
        float atOneHundredFortyFourFrames = SimulateBoost(50f, 144, 1);
        Assert(Math.Abs(atThirtyFrames - atSixtyFrames) / atSixtyFrames < 0.00001f &&
            Math.Abs(atOneHundredFortyFourFrames - atSixtyFrames) / atSixtyFrames < 0.00001f &&
            Math.Abs(atSixtyFrames - 110f) < 0.001f,
            "one second adds sixty metres per second at 30, 60 and 144 FPS without exponential growth");
        Assert(!KeyboardVehicleBoost.TryGetForwardSpeed(false, 50f, 1f / 60f, out target) && target == 0f &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, 1f / 60f, out target) && target > 50f,
            "release suppresses the next speed write and another press resumes immediately");

        float uninterrupted = SimulateBoost(50f, 60, 2);
        Assert(Math.Abs(uninterrupted - 170f) < 0.001f &&
            Math.Abs(SimulateBoost(50f, 30, 2) - uninterrupted) < 0.001f &&
            Math.Abs(SimulateBoost(50f, 144, 2) - uninterrupted) < 0.001f &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, 1000000f, 1f / 60f, out target) && target > 1000000f,
            "two seconds keep the same acceleration at 30, 60 and 144 FPS without fuel, cooldown, duration or speed cap");
        foreach (float startingSpeed in new[] { 150f / 3.6f, 200f / 3.6f })
        {
            float afterOneSecondKmh = SimulateBoost(startingSpeed, 60, 1) * 3.6f;
            Assert(afterOneSecondKmh > 360f && afterOneSecondKmh < 420f,
                "holding O for one second from 150 or 200 km/h reaches roughly four hundred km/h before GTA physics");
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            Assert(!KeyboardVehicleBoost.TryGetForwardSpeed(true, invalid, 1f / 60f, out target) && target == 0f,
                "invalid current speed cannot reach the vehicle native");
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, 0f })
        {
            Assert(!KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, invalid, out target) && target == 0f,
                "invalid elapsed time cannot reach the vehicle native");
        }
        Assert(KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, 5f, out float stalled) &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, 0.1f, out float bounded) && stalled == bounded &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, 50f, float.MaxValue, out float longest) && longest == bounded,
            "a stalled frame applies at most one tenth of a second without catching up the pause");
        Assert(KeyboardVehicleBoost.TryGetForwardSpeed(true, float.MaxValue, 1f / 60f, out target) && target == float.MaxValue &&
            KeyboardVehicleBoost.TryGetForwardSpeed(true, float.MaxValue / 2f, 1f / 60f, out target) &&
            !float.IsNaN(target) && !float.IsInfinity(target),
            "large finite speeds never reset to zero when float precision cannot represent another small increase");
    }

    private static float SimulateBoost(float speed, int framesPerSecond, int seconds)
    {
        for (int frame = 0; frame < framesPerSecond * seconds; frame++)
        {
            if (!KeyboardVehicleBoost.TryGetForwardSpeed(true, speed, 1f / framesPerSecond, out speed))
            {
                throw new InvalidOperationException("A held boost frame must yield a finite speed.");
            }
        }

        return speed;
    }

    private static void CheckWorldTimeSelection()
    {
        var time = new WorldTimeSelection();
        time.Refresh(17, 37);
        Assert(time.Hour == 17 && time.Minute == 37 &&
            !time.TryTakeRequest(Seconds(1), out _, out _),
            "opening time selection keeps the exact current time without changing the world");
        time.SelectHour(18, Seconds(1));
        time.Refresh(5, 15);
        Assert(time.Hour == 18 && time.Minute == 37 &&
            !time.TryTakeRequest(Seconds(1.5), out _, out _),
            "hour selection preserves minutes and cannot be overwritten by a menu refresh");
        time.SelectMinute(42, Seconds(1.5));
        Assert(!time.TryTakeRequest(Seconds(2), out _, out _) &&
            time.TryTakeRequest(Seconds(2.2), out int hour, out int minute) && hour == 18 && minute == 42 &&
            !time.TryTakeRequest(Seconds(3), out _, out _),
            "rapid hour and minute edits send only the final pair after navigation stops");

        time.SelectMinute(43, Seconds(2.3));
        time.Confirm(18, 42);
        Assert(time.Hour == 18 && time.Minute == 43 &&
            time.TryTakeRequest(Seconds(3), out hour, out minute) && hour == 18 && minute == 43,
            "confirmation of an earlier request cannot replace the player's newer selection");
        time.Confirm(18, 42);
        Assert(time.Minute == 43 && !time.TryTakeRequest(Seconds(4), out _, out _),
            "a delayed older confirmation neither overwrites nor echoes the latest request");
        time.Confirm(18, 43);
        time.Refresh(18, 44);
        Assert(time.Minute == 44 && !time.TryExpire(Seconds(10)) &&
            !time.TryTakeRequest(Seconds(10), out _, out _),
            "confirmed time can refresh from the clock without producing another request or timeout");

        time.SelectHour(3, Seconds(10));
        time.SelectMinute(20, Seconds(10.1));
        time.SelectPreset(12, 0, Seconds(10.2));
        Assert(time.TryTakeRequest(Seconds(10.2), out hour, out minute) && hour == 12 && minute == 0 &&
            !time.TryTakeRequest(Seconds(11), out _, out _),
            "a time preset immediately replaces both values and cancels the earlier queued selection");
        time.SelectPreset(23, 0, Seconds(10.3));
        Assert(!time.TryTakeRequest(Seconds(10.4), out _, out _) &&
            time.TryTakeRequest(Seconds(10.9), out hour, out minute) && hour == 23 && minute == 0,
            "a rapid preset change waits past the server's cooldown and keeps the final choice");
        Assert(!time.TryExpire(Seconds(15.8)) && time.TryExpire(Seconds(16)) &&
            !time.TryExpire(Seconds(17)) && !time.TryTakeRequest(Seconds(17), out _, out _),
            "an unconfirmed time change reports one timeout without repeating global changes");
        time.Refresh(7, 11);
        Assert(time.Hour == 7 && time.Minute == 11,
            "after a timeout the controls can return to the actual game clock");

        time.SelectMinute(59, Seconds(18));
        time.Cancel();
        time.Refresh(9, 23);
        Assert(!time.TryTakeRequest(Seconds(20), out _, out _) && !time.TryExpire(Seconds(30)) &&
            time.Hour == 9 && time.Minute == 23,
            "disconnect cancels queued changes and permits a fresh clock on reconnect");
        time.SelectHour(9, Seconds(31));
        time.SelectMinute(23, Seconds(31));
        Assert(!time.TryTakeRequest(Seconds(32), out _, out _),
            "selecting the current values does not resend the world time");

        bool rejected = false;
        try { time.SelectMinute(60, Seconds(33)); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Assert(rejected && time.Hour == 9 && time.Minute == 23,
            "invalid time leaves the existing choice unchanged");
    }

    private static long Seconds(double value) => (long)(value * Stopwatch.Frequency);

    private static void Assert(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void MustReject(Action action, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
            ex is InvalidOperationException || ex is System.Xml.XmlException)
        { rejected = true; }
        Assert(rejected, label);
    }
}
