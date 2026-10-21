using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using CoopControl.Shared;

namespace RageCoopAdmin.Client;

internal sealed class VehiclePresetStore
{
    internal const int MaximumPresets = 64;
    private const long MaximumFileBytes = 1024 * 1024;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(VehiclePresetSettings));
    private readonly string _path;

    internal VehiclePresetStore(string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new ArgumentException("The resource data folder is required.", nameof(dataFolder));
        }

        _path = Path.Combine(dataFolder, "vehicle-presets.xml");
    }

    internal List<VehiclePreset> Load() => LoadCore();

    private List<VehiclePreset> LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new List<VehiclePreset>();
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The vehicle presets file is too large.");
        }

        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes
        });
        if (Serializer.Deserialize(reader) is not VehiclePresetSettings settings || settings.Version != 1 || settings.Vehicles == null)
        {
            throw new InvalidDataException("The vehicle presets format is unsupported.");
        }

        Validate(settings.Vehicles);
        return settings.Vehicles;
    }

    internal void Save(List<VehiclePreset> presets)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
        LoadCore();
        SaveCore(presets);
    }

    internal List<VehiclePreset> Update(Func<List<VehiclePreset>, bool> change)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
        List<VehiclePreset> latest = LoadCore();
        if (change(latest)) SaveCore(latest);
        return latest;
    }

    private void SaveCore(List<VehiclePreset> presets)
    {
        Validate(presets);
        string directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The resource data folder is unavailable.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".vehicle-presets-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Serializer.Serialize(stream, new VehiclePresetSettings { Vehicles = presets });
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, null);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(List<VehiclePreset> presets)
    {
        if (presets.Count > MaximumPresets || presets.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name) ||
            p.Name.Length > 40 || p.Name.Any(c => char.IsControl(c) || c == '~') || p.Appearance == null) ||
            presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != presets.Count)
        {
            throw new InvalidDataException("Vehicle presets must have distinct names and fit the 64 item limit.");
        }

        foreach (VehiclePreset preset in presets)
        {
            VehicleAppearanceCodec.Validate(preset.Appearance);
        }
    }
}

[XmlRoot("SavedVehicles")]
public sealed class VehiclePresetSettings
{
    [XmlAttribute("version")]
    public int Version { get; set; } = 1;
    [XmlArrayItem("Vehicle")]
    public List<VehiclePreset> Vehicles { get; set; } = new List<VehiclePreset>();
}

public sealed class VehiclePreset
{
    public string Name { get; set; } = string.Empty;
    public VehicleAppearance Appearance { get; set; } = new VehicleAppearance();
}
