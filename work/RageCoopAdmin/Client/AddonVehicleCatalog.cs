using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RageCoopAdmin.Client;

internal static class AddonVehicleCatalog
{
    private const string ResourceName = "RageCoopAdmin.Client.AddonVehicles.json";

    internal static IReadOnlyList<AddonVehicleDefinition> LoadEmbedded()
    {
        using Stream stream = typeof(AddonVehicleCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException("The embedded add-on vehicle catalog is missing.");
        return Load(stream);
    }

    internal static IReadOnlyList<AddonVehicleDefinition> Load(Stream stream)
    {
        var serializer = new DataContractJsonSerializer(typeof(CatalogDocument));
        if (serializer.ReadObject(stream) is not CatalogDocument document || document.Version != 1 ||
            document.Vehicles == null || document.Vehicles.Length > 64)
        {
            throw new InvalidDataException("Unsupported add-on vehicle catalog.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AddonVehicleDefinition vehicle in document.Vehicles)
        {
            if (vehicle == null || !IsToken(vehicle.Id, 64, allowHyphen: true) ||
                !IsToken(vehicle.SpawnCode, 24, allowHyphen: false) ||
                !IsToken(vehicle.DlcFolder, 64, allowHyphen: false) ||
                !IsText(vehicle.Name, 96) || !IsText(vehicle.Purpose, 48) ||
                (vehicle.Drive != "front" && vehicle.Drive != "rear" && vehicle.Drive != "all") ||
                vehicle.Gears < 1 || vehicle.Gears > 10 ||
                !ids.Add(vehicle.Id) || !codes.Add(vehicle.SpawnCode) || !folders.Add(vehicle.DlcFolder))
            {
                throw new InvalidDataException("Invalid or duplicate add-on vehicle entry.");
            }
        }

        return Array.AsReadOnly(document.Vehicles);
    }

    private static bool IsToken(string? value, int maximumLength, bool allowHyphen) =>
        value != null && value.Length > 0 && value.Length <= maximumLength && value.All(character =>
            (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') || character == '_' || (allowHyphen && character == '-'));

    private static bool IsText(string? value, int maximumLength) =>
        value != null && !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value == value.Trim() && !value.Any(character => char.IsControl(character) || character == '~');

    [DataContract]
    private sealed class CatalogDocument
    {
        [DataMember(Name = "version", IsRequired = true)]
        public int Version { get; private set; }

        [DataMember(Name = "vehicles", IsRequired = true)]
        public AddonVehicleDefinition[]? Vehicles { get; private set; }
    }
}

[DataContract]
internal sealed class AddonVehicleDefinition
{
    [DataMember(Name = "id", IsRequired = true)]
    public string Id { get; private set; } = string.Empty;

    [DataMember(Name = "name", IsRequired = true)]
    public string Name { get; private set; } = string.Empty;

    [DataMember(Name = "spawnCode", IsRequired = true)]
    public string SpawnCode { get; private set; } = string.Empty;

    [DataMember(Name = "dlcFolder", IsRequired = true)]
    public string DlcFolder { get; private set; } = string.Empty;

    [DataMember(Name = "purpose", IsRequired = true)]
    public string Purpose { get; private set; } = string.Empty;

    [DataMember(Name = "drive", IsRequired = true)]
    public string Drive { get; private set; } = string.Empty;

    [DataMember(Name = "gears", IsRequired = true)]
    public int Gears { get; private set; }

    internal bool Matches(string query) => VehicleSearch.Matches(Name, SpawnCode, query);

    internal string CardDescription(bool installed) => "~h~" + Name + "~h~~n~" +
        "~c~Назначение:~s~ " + Purpose + "~n~" +
        "~c~Привод:~s~ " + (Drive == "front" ? "Передний" : Drive == "rear" ? "Задний" : "Полный") + "~n~" +
        "~c~Передачи:~s~ " + Gears +
        (installed ? string.Empty :
            "~n~~n~~o~Не установлена~s~~n~Установите общий набор машин и перезапустите игру.");
}
