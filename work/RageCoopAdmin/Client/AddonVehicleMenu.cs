using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private readonly IReadOnlyList<AddonVehicleDefinition> _addonVehicles = AddonVehicleCatalog.LoadEmbedded();
    private NativeMenu? _addonVehiclesMenu;

    private void BuildAddonVehiclesMenu()
    {
        if (_vehicleMenu == null || _pool == null || _addonVehicles.Count == 0) { return; }
        _addonVehiclesMenu = CreateMenu("Совместная игра", "Добавленные машины");
        _vehicleMenu.AddSubMenu(_addonVehiclesMenu);
        _pool.Add(_addonVehiclesMenu);
        foreach (AddonVehicleDefinition definition in _addonVehicles)
        {
            AddAddonVehicleItem(_addonVehiclesMenu, definition);
        }
    }

    private void AddAddonVehicleItem(NativeMenu menu, AddonVehicleDefinition definition)
    {
        int hash = AddonModelHash(definition);
        if (IsVehicleModelAvailable(hash))
        {
            AddVehicleItem(menu, new VehicleEntry(hash, definition.SpawnCode, definition.Name, -1));
            return;
        }

        // A missing DLC must remain visible with its real name, without requesting its model.
        var unavailable = new VehicleMenuItem(definition.Name, definition.CardDescription(installed: false))
        {
            // The reason is already in the selected car's description. A second,
            // long right-hand label collides with the name in LemonUI's fixed row.
            Enabled = false
        };
        unavailable.Selected += (_, _) => ReleaseSpawnModelPreload();
        menu.Add(unavailable);
    }

    private AddonVehicleDefinition? FindAddonVehicle(int hash) =>
        _addonVehicles.FirstOrDefault(definition => AddonModelHash(definition) == hash);

    private static int AddonModelHash(AddonVehicleDefinition definition) =>
        unchecked((int)StringHash.AtStringHash(definition.SpawnCode.ToLowerInvariant()));

    private static bool IsVehicleModelAvailable(int hash)
    {
        Model model = new Model(hash);
        return model.IsInCdImage && model.IsVehicle;
    }

    private bool EnsureSpawnModelAvailable(int hash)
    {
        if (IsVehicleModelAvailable(hash)) { return true; }
        ReleaseSpawnModelPreload();
        Notification.PostTicker(FindAddonVehicle(hash) is AddonVehicleDefinition definition
            ? "~o~" + definition.Name + ":~s~ машина не установлена. Установите общий набор добавленных машин и перезапустите игру."
            : "~o~Этой модели нет в вашей версии GTA.", false);
        return false;
    }

}
