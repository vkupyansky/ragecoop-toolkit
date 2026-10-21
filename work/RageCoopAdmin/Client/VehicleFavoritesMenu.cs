using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTA;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private readonly Dictionary<NativeListItem<string>, int> _vehicleRows =
        new Dictionary<NativeListItem<string>, int>();
    private readonly Dictionary<NativeItem, int> _unavailableFavoriteRows = new Dictionary<NativeItem, int>();
    private HashSet<int> _favoriteHashes = new HashSet<int>();
    private FavoriteVehicleStore? _favoriteStore;
    private NativeMenu? _favoritesMenu;
    private bool _favoritesUnavailable;
    private bool _favoritesDirty;
    private bool _catalogReady;
    private Model? _preloadedSpawnModel;

    private void InitializeFavorites()
    {
        try
        {
            _favoriteStore = new FavoriteVehicleStore(CurrentResource.DataFolder);
            _favoriteHashes = _favoriteStore.Load();
        }
        catch (Exception ex) when (IsFavoriteStorageError(ex))
        {
            // Keep an unreadable file intact rather than overwriting it with an empty list.
            _favoritesUnavailable = true;
            Logger.Warning("Coop Control: favorites could not be loaded: " + ex.GetType().Name);
            Notification.PostTicker("~r~Избранное:~s~ не удалось прочитать настройки. Список сохранён без изменений.", false);
        }
    }

    private void BuildFavoritesMenu()
    {
        if (_vehicleMenu == null || _pool == null)
        {
            return;
        }

        _favoritesMenu = CreateMenu("Совместная игра", "Избранное");
        _vehicleMenu.AddSubMenu(_favoritesMenu);
        _pool.Add(_favoritesMenu);
        RefreshFavoritesMenu();
    }

    private void AddVehicleItem(NativeMenu menu, VehicleEntry entry)
    {
        AddonVehicleDefinition? addon = FindAddonVehicle(entry.Hash);
        var item = new VehicleMenuListItem(entry.DisplayName,
            addon?.CardDescription(installed: true) ??
                "~h~" + entry.DisplayName + "~h~",
            VehicleMenuText.Create, FavoriteAction(entry.Hash));
        _vehicleRows.Add(item, entry.Hash);
        item.Selected += (_, _) => PreloadSpawnModel(entry.Hash);
        item.Activated += (_, _) =>
        {
            if (item.SelectedIndex == 0)
            {
                PreloadSpawnModel(entry.Hash);
                SendSpawn(entry.Hash);
            }
            else
            {
                ToggleFavorite(entry.Hash);
            }

            item.SelectedIndex = 0;
        };
        menu.Add(item);
    }

    private void PreloadSpawnModel(int modelHash)
    {
        if (!API.IsOnServer || !IsVehicleModelAvailable(modelHash))
        {
            ReleaseSpawnModelPreload();
            return;
        }

        if (_preloadedSpawnModel is Model current && current.Hash == modelHash)
        {
            current.Request();
            return;
        }

        ReleaseSpawnModelPreload();
        // GTA loads the selected model while the player is navigating the menu.
        // RageCoop can then create the shared car without waiting for streaming.
        Model model = new Model(modelHash);
        model.Request();
        _preloadedSpawnModel = model;
    }

    private void ReleaseSpawnModelPreload()
    {
        if (_preloadedSpawnModel is Model model)
        {
            _preloadedSpawnModel = null;
            model.MarkAsNoLongerNeeded();
        }
    }

    private bool IsSpawnMenuVisible() =>
        _vehicleMenu?.Visible == true || _favoritesMenu?.Visible == true ||
        _addonVehiclesMenu?.Visible == true ||
        _searchMenu?.Visible == true || _presetsMenu?.Visible == true ||
        _classMenus.Values.Any(menu => menu.Visible);

    private string FavoriteAction(int modelHash) =>
        _favoriteHashes.Contains(modelHash) ? VehicleMenuText.RemoveFavorite : VehicleMenuText.AddFavorite;

    private void ToggleFavorite(int modelHash)
    {
        if (_favoritesUnavailable || _favoriteStore == null)
        {
            Notification.PostTicker("~r~Избранное:~s~ настройки недоступны. Каталог и создание транспорта работают.", false);
            return;
        }

        bool added = !_favoriteHashes.Contains(modelHash);
        HashSet<int> updated;
        try
        {
            updated = _favoriteStore.Update(values =>
            {
                if (added) values.Add(modelHash);
                else values.Remove(modelHash);
            });
        }
        catch (Exception ex) when (IsFavoriteStorageError(ex))
        {
            Logger.Warning("Coop Control: favorites could not be saved: " + ex.GetType().Name);
            Notification.PostTicker("~r~Избранное:~s~ изменение не сохранено. Список не изменён.", false);
            return;
        }

        _favoriteHashes = updated;
        foreach (KeyValuePair<NativeListItem<string>, int> row in _vehicleRows)
        {
            row.Key.Items = new List<string> { VehicleMenuText.Create, FavoriteAction(row.Value) };
        }

        _favoritesDirty = true;
        Notification.PostTicker(added
            ? "~g~Избранное:~s~ модель добавлена."
            : "~o~Избранное:~s~ модель убрана.", false);
    }

    private void RefreshFavoritesMenu()
    {
        if (_favoritesMenu == null)
        {
            return;
        }

        NativeItem? selected = _favoritesMenu.SelectedItem;
        int? selectedHash = selected is NativeListItem<string> selectedRow && _vehicleRows.TryGetValue(selectedRow, out int hash)
            ? hash : selected != null && _unavailableFavoriteRows.TryGetValue(selected, out hash) ? hash : null;
        RemoveTrackedRows(_favoritesMenu);
        _unavailableFavoriteRows.Clear();
        _favoritesMenu.Clear();
        _favoritesDirty = false;
        if (_favoritesUnavailable)
        {
            _favoritesMenu.Add(new NativeItem("Настройки недоступны",
                "Не удалось прочитать избранное. Пока можно выбрать транспорт в каталоге.")
            { Enabled = false });
            return;
        }

        NativeItem addCurrent = new NativeItem("Добавить текущий транспорт",
            "Сядьте в нужный транспорт, чтобы сохранить его модель в личном списке.");
        addCurrent.Activated += (_, _) => AddCurrentVehicleToFavorites();
        _favoritesMenu.Add(addCurrent);
        if (!_catalogReady)
        {
            _favoritesMenu.Add(new NativeItem("Каталог загружается…",
                "Личный список появится после проверки доступных моделей.")
            { Enabled = false });
            return;
        }

        var favorites = _favoriteHashes.Select(hashValue => new
        {
            Hash = hashValue,
            Entry = FindFavoriteEntry(hashValue),
            Addon = FindAddonVehicle(hashValue)
        }).Select(value => new
        {
            value.Hash,
            value.Entry,
            value.Addon,
            Title = value.Entry?.DisplayName ?? value.Addon?.Name ?? "Модель недоступна"
        }).OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(value => value.Hash);
        foreach (var favorite in favorites)
        {
            int favoriteHash = favorite.Hash;
            VehicleEntry? entry = favorite.Entry;
            if (entry.HasValue)
            {
                AddVehicleItem(_favoritesMenu, entry.Value);
            }
            else
            {
                AddonVehicleDefinition? addon = favorite.Addon;
                var unavailable = new VehicleMenuListItem(favorite.Title,
                    addon?.CardDescription(installed: false) ??
                        "~h~" + favorite.Title + "~h~~n~Этой модели нет в вашей версии игры.",
                    VehicleMenuText.Unavailable, VehicleMenuText.RemoveFavorite);
                unavailable.Selected += (_, _) => ReleaseSpawnModelPreload();
                unavailable.Activated += (_, _) =>
                {
                    if (unavailable.SelectedIndex == 1) ToggleFavorite(favoriteHash);
                    else Notification.PostTicker("~o~Этой модели нет в вашей версии игры.~s~ Избранное не изменено.", false);
                    unavailable.SelectedIndex = 0;
                };
                _unavailableFavoriteRows.Add(unavailable, favoriteHash);
                _favoritesMenu.Add(unavailable);
            }
        }

        if (_favoritesMenu.Items.Count == 1)
        {
            _favoritesMenu.Add(new NativeItem("Пока пусто",
                "В каталоге или поиске выберите у модели действие «В избранное» стрелками ←/→ и подтвердите выбор.")
            { Enabled = false });
            return;
        }

        if (selectedHash.HasValue)
        {
            foreach (NativeItem item in _favoritesMenu.Items)
            {
                if ((item is NativeListItem<string> row && _vehicleRows.TryGetValue(row, out int rowHash) && rowHash == selectedHash.Value) ||
                    (_unavailableFavoriteRows.TryGetValue(item, out int unavailableHash) && unavailableHash == selectedHash.Value))
                {
                    _favoritesMenu.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private void AddCurrentVehicleToFavorites()
    {
        Ped player = Game.Player.Character;
        Vehicle? current = player?.CurrentVehicle;
        if (current == null || !current.Exists())
        {
            Notification.PostTicker("~o~Избранное:~s~ сначала сядьте в нужный транспорт.", false);
            return;
        }

        int hash = current.Model.Hash;
        if (_favoriteHashes.Contains(hash))
        {
            Notification.PostTicker("~o~Избранное:~s~ эта модель уже в списке.", false);
            return;
        }

        ToggleFavorite(hash);
    }

    private VehicleEntry? FindFavoriteEntry(int modelHash)
    {
        AddonVehicleDefinition? addon = FindAddonVehicle(modelHash);
        if (addon != null)
        {
            return IsVehicleModelAvailable(modelHash)
                ? new VehicleEntry(modelHash, addon.SpawnCode, addon.Name, -1) : (VehicleEntry?)null;
        }

        foreach (VehicleEntry entry in _catalog)
        {
            if (entry.Hash == modelHash)
            {
                return entry;
            }
        }

        return DescribeVehicle(modelHash);
    }

    private void RemoveTrackedRows(NativeMenu menu)
    {
        foreach (NativeListItem<string> item in menu.Items.OfType<NativeListItem<string>>())
        {
            _vehicleRows.Remove(item);
        }
    }

    private static bool IsFavoriteStorageError(Exception ex) =>
        ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
        ex is ArgumentException || ex is System.Xml.XmlException;
}
