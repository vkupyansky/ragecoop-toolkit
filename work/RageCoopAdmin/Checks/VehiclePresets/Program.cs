using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoopControl.Shared;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CoopVehiclePresetsCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckWireContract();
            CheckStorage(directory);
            Console.WriteLine("Vehicle preset and wire contract checks passed");
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

    private static VehicleAppearance Appearance()
    {
        var value = new VehicleAppearance
        {
            ModelHash = -1216765807,
            Primary = 27,
            Secondary = 111,
            PrimaryRgb = 0x0A24F1,
            WheelType = 7,
            FrontCustomTires = true,
            RearCustomTires = false,
            NeonMask = 9,
            NeonRgb = 0x00FF19,
            ExtrasPresent = 7,
            ExtrasEnabled = 5,
            Plate = "FRIENDS",
            Livery = 2,
            ToggleMask = 63
        };
        value.Mods[0] = 4;
        value.Mods[23] = 12;
        return value;
    }

    private static void CheckWireContract()
    {
        VehicleAppearance expected = Appearance();
        string xml = VehicleAppearanceCodec.Encode(expected);
        VehicleAppearance actual = VehicleAppearanceCodec.Decode(xml);
        Assert(actual.ModelHash == expected.ModelHash && actual.Mods.SequenceEqual(expected.Mods) &&
            actual.PrimaryRgb == expected.PrimaryRgb && actual.FrontCustomTires && !actual.RearCustomTires && actual.NeonMask == 9 &&
            actual.ExtrasEnabled == 5 && actual.ToggleMask == 63 && actual.Plate == "FRIENDS",
            "network roundtrip preserves signed model, mods, RGB, toggles and plate");
        MustReject(() => VehicleAppearanceCodec.Decode(new string(' ', VehicleAppearanceCodec.MaximumXmlCharacters + 1)), "network payload size is bounded");
        MustReject(() => VehicleAppearanceCodec.Decode("<!DOCTYPE VehicleAppearance [<!ENTITY x SYSTEM 'file:///do-not-read'>]>" + xml.Substring(xml.IndexOf("<VehicleAppearance", StringComparison.Ordinal))), "network DTD is rejected");
        VehicleAppearance invalid = Appearance();
        invalid.Mods[23] = 201;
        MustReject(() => VehicleAppearanceCodec.Encode(invalid), "out-of-range native mod is rejected");
        invalid = Appearance();
        invalid.NeonMask = 16;
        MustReject(() => VehicleAppearanceCodec.Encode(invalid), "unsupported neon bits are rejected");
        invalid = Appearance();
        invalid.ExtrasEnabled = 8;
        MustReject(() => VehicleAppearanceCodec.Encode(invalid), "nonexistent extras cannot be enabled");
        invalid = Appearance();
        invalid.Plate = "~r~TEXT";
        MustReject(() => VehicleAppearanceCodec.Encode(invalid), "GTA markup cannot enter plate text");
        invalid = Appearance();
        invalid.Mods = invalid.Mods.Take(49).ToArray();
        MustReject(() => VehicleAppearanceCodec.Encode(invalid), "partial native mod arrays are rejected");
    }

    private static void CheckStorage(string directory)
    {
        var store = new VehiclePresetStore(directory);
        Assert(store.Load().Count == 0, "new profile starts empty");
        store.Save(new List<VehiclePreset> { new VehiclePreset { Name = "Моя машина", Appearance = Appearance() } });
        List<VehiclePreset> loaded = new VehiclePresetStore(directory).Load();
        Assert(loaded.Count == 1 && loaded[0].Name == "Моя машина" && loaded[0].Appearance.Mods[23] == 12,
            "named tuned vehicle survives reopen");
        string path = Path.Combine(directory, "vehicle-presets.xml");
        string previous = File.ReadAllText(path);
        MustReject(() => store.Save(new List<VehiclePreset>
        {
            new VehiclePreset { Name = "Same", Appearance = Appearance() },
            new VehiclePreset { Name = "same", Appearance = Appearance() }
        }), "duplicate names are rejected case insensitively");
        Assert(File.ReadAllText(path) == previous, "invalid update preserves saved configurations");
        MustReject(() => store.Save(Enumerable.Range(0, 65).Select(i => new VehiclePreset { Name = "Car " + i, Appearance = Appearance() }).ToList()),
            "personal collection is bounded");
        store.Save(new List<VehiclePreset>());
        Assert(new VehiclePresetStore(directory).Load().Count == 0, "atomic replacement can remove a preset");
        File.WriteAllText(path, "<SavedVehicles version=\"99\"><Vehicles /></SavedVehicles>");
        string unsupported = File.ReadAllText(path);
        MustReject(() => store.Load(), "unsupported format is rejected");
        Assert(File.ReadAllText(path) == unsupported, "failed load never overwrites the original file");
        File.WriteAllText(path, "<!DOCTYPE SavedVehicles [<!ENTITY x SYSTEM 'file:///do-not-read'>]><SavedVehicles version=\"1\"><Vehicles /></SavedVehicles>");
        MustReject(() => store.Load(), "stored DTD is rejected");
        string blocked = Path.Combine(directory, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "vehicle-presets.xml"));
        MustReject(() => new VehiclePresetStore(blocked).Save(new List<VehiclePreset>()), "commit failure is reported");
        Assert(!Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "failed commits clean temporary files");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        Console.WriteLine("PASS " + label);
    }

    private static void MustReject(Action action, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.Xml.XmlException)
        { rejected = true; }
        Assert(rejected, label);
    }
}
