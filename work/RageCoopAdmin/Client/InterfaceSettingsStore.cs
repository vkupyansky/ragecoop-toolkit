using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace RageCoopAdmin.Client;

internal sealed class InterfaceSettingsStore
{
    private const long MaximumFileBytes = 16 * 1024;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(InterfaceSettings));
    private readonly string _path;

    internal InterfaceSettingsStore(string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new ArgumentException("The resource data folder is required.", nameof(dataFolder));
        }

        _path = Path.Combine(dataFolder, "interface.xml");
    }

    internal InterfaceSettings Load() => LoadCore();

    private InterfaceSettings LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new InterfaceSettings();
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The interface settings file is too large.");
        }

        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes
        });
        reader.MoveToContent();
        // Before v4, an omitted version was interpreted as v3 by the serializer's default.
        bool unversioned = reader.GetAttribute("version") == null;
        if (Serializer.Deserialize(reader) is not InterfaceSettings settings)
        {
            throw new InvalidDataException("The interface settings format is invalid.");
        }

        if (unversioned)
        {
            settings.Version = 3;
        }

        if (settings.Version >= 1 && settings.Version <= 3)
        {
            ValidateChoices(settings, maximumChoice: 2);
            if (settings.Version <= 2)
            {
                // Preserve the v3 migration from an impulse to held boost for older profiles.
                settings.KeyboardBoost = true;
            }

            // Legacy I now uses K; a disabled repair binding stays disabled.
            settings.RepairShortcut = settings.RepairShortcut == 2 ? 1 : 0;
            settings.Version = 5;
        }
        else if (settings.Version == 4)
        {
            settings.Version = 5;
        }

        Validate(settings);
        return settings;
    }

    internal void Save(InterfaceSettings settings)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
#if PUBLIC_RELEASE
        // The public profile does not own the private HUD preference. Carry
        // it from the current file even when callers save a fresh settings object.
        settings.PrivateProfileOptions = LoadCore().PrivateProfileOptions;
#else
        LoadCore();
#endif
        SaveCore(settings);
    }

    internal InterfaceSettings Update(Action<InterfaceSettings> change)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
        InterfaceSettings latest = LoadCore();
        change(latest);
        SaveCore(latest);
        return latest;
    }

    private void SaveCore(InterfaceSettings settings)
    {
        Validate(settings);
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The resource data folder is unavailable.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".interface-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Serializer.Serialize(stream, settings);
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

    private static void Validate(InterfaceSettings settings)
    {
        if (settings.Version != 5)
        {
            throw new InvalidDataException("Unsupported interface settings.");
        }

        ValidateChoices(settings, maximumChoice: 1);
    }

    private static void ValidateChoices(InterfaceSettings settings, int maximumChoice)
    {
        if (settings.RepairShortcut < 0 || settings.RepairShortcut > maximumChoice)
        {
            throw new InvalidDataException("Unsupported interface settings.");
        }
    }
}

[XmlRoot("Interface")]
public sealed class InterfaceSettings
{
    [XmlAttribute("version")]
    public int Version { get; set; } = 5;

    // v4-v5: 0 enables K, 1 disables it. v1-v3 are converted while loading.
    public int RepairShortcut { get; set; }
    public bool GamepadShortcuts { get; set; } = true;
    public bool KeyboardBoost { get; set; } = true;
#if !PUBLIC_RELEASE
    public bool ShowSpeedometer { get; set; } = true;
#else
    [XmlAnyElement("ShowSpeedometer")]
    public XmlElement[]? PrivateProfileOptions { get; set; }
#endif
}
