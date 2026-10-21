using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace RageCoopAdmin.Client;

// Stores model choices only. Game saves and vehicle state are not part of this file.
internal sealed class FavoriteVehicleStore
{
    private const int MaximumModels = 2048;
    private const long MaximumFileBytes = 128 * 1024;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(FavoriteVehicleSettings));
    private readonly string _path;

    internal FavoriteVehicleStore(string dataFolder)
    {
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new ArgumentException("The resource data folder is required.", nameof(dataFolder));
        }

        _path = Path.Combine(dataFolder, "vehicle-favorites.xml");
    }

    internal HashSet<int> Load() => LoadCore();

    private HashSet<int> LoadCore()
    {
        if (!File.Exists(_path))
        {
            return new HashSet<int>();
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The favorites file is too large.");
        }

        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes
        });
        if (Serializer.Deserialize(reader) is not FavoriteVehicleSettings settings ||
            settings.Version != 1 || settings.Models == null)
        {
            throw new InvalidDataException("The favorites file has an unsupported format.");
        }

        Validate(settings.Models);
        return new HashSet<int>(settings.Models);
    }

    internal void Save(IEnumerable<int> modelHashes)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
        LoadCore();
        SaveCore(modelHashes);
    }

    internal HashSet<int> Update(Action<HashSet<int>> change)
    {
        using StorageFileLock held = StorageFileLock.Acquire(_path);
        HashSet<int> latest = LoadCore();
        change(latest);
        SaveCore(latest);
        return latest;
    }

    private void SaveCore(IEnumerable<int> modelHashes)
    {
        List<int> models = modelHashes.OrderBy(hash => hash).ToList();
        Validate(models);
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The resource data folder is unavailable.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, ".vehicle-favorites-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Serializer.Serialize(stream, new FavoriteVehicleSettings { Models = models });
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

    private static void Validate(List<int> models)
    {
        if (models.Count > MaximumModels || models.Any(hash => hash == 0) ||
            models.Distinct().Count() != models.Count)
        {
            throw new InvalidDataException("The favorites list must contain up to 2048 distinct, nonzero model hashes.");
        }
    }
}

[XmlRoot("FavoriteVehicles")]
public sealed class FavoriteVehicleSettings
{
    [XmlAttribute("version")]
    public int Version { get; set; } = 1;

    [XmlArray("Models")]
    [XmlArrayItem("Model")]
    public List<int> Models { get; set; } = new List<int>();
}
