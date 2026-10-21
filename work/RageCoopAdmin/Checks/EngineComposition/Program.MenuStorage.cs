using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using GTA;
using HarmonyLib;
using LemonUI;
using LemonUI.Menus;

internal static partial class Program
{
    private static readonly List<string> MenuStorageNotifications = new List<string>();
    private static readonly List<string> MenuStorageWarnings = new List<string>();
    private const int MissingFavoriteHash = -989301;

    private static void CheckMenuStorageComposition()
    {
        var boundary = new Harmony("friendslegacy.check.menustorageboundary");
        string directory = Path.Combine(Path.GetTempPath(), "CoopMenuStorageCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PatchMenuStorageBoundaries(boundary);
            var host = new MenuStorageHost(directory);
            CheckGarageDeletion(host, directory);
            CheckFavoriteRows(host, directory);
            CheckPeerSelection(host);
        }
        finally
        {
            InputHeld.Clear(); InputEdges.Clear(); InputKeys.Clear();
            _inputConnected = true;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void PatchMenuStorageBoundaries(Harmony boundary)
    {
        // The actual resource builders, item callbacks, LemonUI input and XML
        // stores run unchanged. Only GTA graphics, streaming and connection
        // state are supplied by the isolated host.
        foreach (string name in new[] { "Draw", "UpdateItems", "Recalculate", "UpdateDescription", "ResetCursor" })
            boundary.Patch(typeof(NativeMenu).GetMethod(name, Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        foreach (MethodInfo method in typeof(Sound).GetMethods(Instance).Where(method => method.Name == "PlayFrontend"))
            boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        foreach (Type element in new[] { typeof(LemonUI.Elements.ScaledText), typeof(LemonUI.Elements.ScaledTexture), typeof(LemonUI.Elements.ScaledRectangle) })
            boundary.Patch(element.GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Elements.ScaledTexture).GetMethod("Request", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Elements.ScaledText).GetProperty("Width")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(IsolateTextWidth)));
        Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
        boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(System.Drawing.PointF) }, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(KeepLayoutPoint)));
        boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButton).GetConstructor(new[] { typeof(string), typeof(GTA.Control) })!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButtons).GetMethod("Add", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        boundary.Patch(typeof(LemonUI.Scaleform.BaseScaleform).GetConstructor(Instance, null, new[] { typeof(string) }, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        Type controls = typeof(NativeMenu).Assembly.GetType("LemonUI.Controls", true)!;
        foreach (MethodInfo method in controls.GetMethods(Static).Where(method => method.Name is "IsPressed" or "IsJustPressed" or "DisableThisFrame" || method.Name == "get_IsUsingController"))
            boundary.Patch(method, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
        boundary.Patch(typeof(NativeMenu).GetMethod("ProcessControls", Instance)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
        boundary.Patch(Engine.GetType("RageCoop.Client.Scripting.API", true)!.GetProperty("IsOnServer")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(InputConnectionState)));
        foreach (string name in new[] { "IsInCdImage", "IsVehicle" })
            boundary.Patch(typeof(Model).GetProperty(name)!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(MenuStorageModelAvailable)));
        foreach (string name in new[] { "Request", "MarkAsNoLongerNeeded" })
            boundary.Patch(typeof(Model).GetMethod(name, Instance, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
        foreach (MethodInfo method in typeof(GTA.UI.Notification).GetMethods(Static).Where(method => method.Name == "PostTicker"))
            boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureMenuStorageNotification)));
        foreach (MethodInfo method in typeof(RageCoop.Core.Logger).GetMethods(Instance).Where(method => method.Name == "Warning"))
            boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureMenuStorageWarning)));
        MethodInfo roster = Resource.GetType("RageCoopAdmin.Client.PlayerSnapshots", true)!.GetMethods(Static)
            .Single(method => method.Name == "TryGet" && method.GetParameters().Length == 1);
        boundary.Patch(roster, prefix: new HarmonyMethod(typeof(Program), nameof(MenuStoragePeerRosterUnavailable)));
    }

    private static void CheckGarageDeletion(MenuStorageHost host, string directory)
    {
        object first = host.Preset("Альфа", 101), chosen = host.Preset("Бета", 202), last = host.Preset("Гамма", 303);
        host.SetPresets(first, chosen, last);
        host.SavePresets();
        host.Call("BuildVehicleToolsMenu");
        NativeMenu garage = host.Menu("_presetsMenu"), confirmation = host.Menu("_presetDeleteMenu");
        garage.SelectedItem = garage.Items.Single(item => item.Title == "Бета");
        host.Presets.Insert(0, host.Preset("Авангард", 404));
        host.SavePresets();
        host.Call("RefreshVehiclePresets");
        Assert(garage.SelectedItem.Title == "Бета" && garage.SelectedIndex == 3,
            "Rebuilding the actual garage preserves the selected saved-car identity after another row is inserted.");

        string path = Path.Combine(directory, "vehicle-presets.xml");
        byte[] before = File.ReadAllBytes(path);
        int poolSize = host.Pool.Count();
        var row = (NativeListItem<string>)garage.SelectedItem;
        row.SelectedIndex = 1;
        garage.Visible = true;
        host.Controls(garage);
        host.Accept(held: true);
        host.Controls(garage);
        host.Controls(confirmation);
        Assert(confirmation.Visible && !garage.Visible && confirmation.SelectedIndex == 0 &&
            confirmation.Items[0].Title == "Отмена" && confirmation.Items[1].Title.Contains("Бета") &&
            host.Pool.Count() == poolSize && before.SequenceEqual(File.ReadAllBytes(path)),
            "Choosing delete opens the prebuilt named confirmation with Cancel selected and does not change the XML or pool.");
        InputEdges.Clear();
        for (int frame = 0; frame < 3; frame++) host.Controls(confirmation);
        Assert(confirmation.Visible && before.SequenceEqual(File.ReadAllBytes(path)),
            "The actual LemonUI control loop cannot consume held opening A as confirmation or delete a saved car.");
        host.Accept(held: false);
        host.Controls(confirmation);
        host.Activate(confirmation.Items[0]);
        Assert(garage.Visible && !confirmation.Visible && before.SequenceEqual(File.ReadAllBytes(path)),
            "The explicit Cancel action returns to the garage without changing its saved file.");

        host.OpenDelete(chosen);
        host.Controls(confirmation);
        InputHeld.Add((0, 177)); InputEdges.Add((0, 177));
        host.Controls(confirmation);
        host.Accept(held: false);
        host.Activate(confirmation.Items[1]);
        Assert(garage.Visible && !confirmation.Visible && before.SequenceEqual(File.ReadAllBytes(path)),
            "Actual LemonUI Back cancels the deletion request and a late confirmation callback cannot use it.");

        host.OpenDelete(chosen);
        host.Activate(confirmation.Items[1]);
        Assert(host.Presets.Count == 3 && !host.PresetNames().Contains("Бета") && host.StoredPresetNames().SequenceEqual(host.PresetNames()),
            "A separate explicit confirmation removes exactly the chosen saved car through the real XML store.");
        byte[] deleted = File.ReadAllBytes(path);
        host.Activate(confirmation.Items[1]);
        Assert(deleted.SequenceEqual(File.ReadAllBytes(path)), "Repeated confirmation activation cannot remove another saved car.");

        object protectedPreset = host.Presets[0]!;
        host.OpenDelete(protectedPreset);
        string originalName = host.Name(protectedPreset);
        protectedPreset.GetType().GetProperty("Name")!.SetValue(protectedPreset, originalName + " изменена");
        host.Activate(confirmation.Items[1]);
        Assert(deleted.SequenceEqual(File.ReadAllBytes(path)) && host.PresetNames().Contains(originalName),
            "Changing the selected in-memory preset while confirmation is open rejects deletion and reloads the stored list.");

        host.OpenDelete(host.Presets[0]!);
        IList external = host.LoadPresets();
        external.Add(host.Preset("Внешняя машина", 505));
        host.SavePresets(external);
        byte[] externalBytes = File.ReadAllBytes(path);
        host.Activate(confirmation.Items[1]);
        Assert(externalBytes.SequenceEqual(File.ReadAllBytes(path)) && host.PresetNames().Contains("Внешняя машина"),
            "An external XML change during confirmation is preserved, including other saved cars.");

        host.OpenDelete(host.Presets[0]!);
        IList changedAppearance = host.LoadPresets();
        object appearance = changedAppearance[0]!.GetType().GetProperty("Appearance")!.GetValue(changedAppearance[0])!;
        appearance.GetType().GetProperty("Primary")!.SetValue(appearance, 55);
        host.SavePresets(changedAppearance);
        byte[] appearanceBytes = File.ReadAllBytes(path);
        host.Activate(confirmation.Items[1]);
        Assert(appearanceBytes.SequenceEqual(File.ReadAllBytes(path)) && host.Presets.Count == changedAppearance.Count,
            "A changed saved configuration with the same name and roster size also invalidates deletion.");

        host.OpenDelete(host.Presets[0]!);
        File.WriteAllText(path, "<unsupported>");
        byte[] invalid = File.ReadAllBytes(path);
        int warnings = MenuStorageWarnings.Count;
        host.Activate(confirmation.Items[1]);
        Assert(invalid.SequenceEqual(File.ReadAllBytes(path)) && MenuStorageWarnings.Count == warnings + 1,
            "Unreadable saved-car XML follows the real logged error branch and is never overwritten by a deletion confirmation.");
        File.Delete(path); // Reset this deliberately corrupted temporary fixture explicitly.
        host.SavePresets();

        host.OpenDelete(host.Presets[0]!);
        _inputConnected = false;
        byte[] connected = File.ReadAllBytes(path);
        host.Activate(confirmation.Items[1]);
        Assert(connected.SequenceEqual(File.ReadAllBytes(path)) && !garage.Visible,
            "Disconnect invalidates an open confirmation without deleting storage or restoring its parent.");
        _inputConnected = true;
    }

    private static void CheckFavoriteRows(MenuStorageHost host, string directory)
    {
        host.AddCatalog(9, "zeta", "Зета");
        host.AddCatalog(1, "omega", "Омега");
        host.AddCatalog(8, "alpha", "Альфа");
        host.SetFavorites(9, 1, 8, MissingFavoriteHash);
        host.Call("BuildFavoritesMenu");
        NativeMenu favorites = host.Menu("_favoritesMenu");
        Assert(favorites.Items.Skip(1).Select(item => item.Title).SequenceEqual(new[] { "Альфа", "Зета", "Модель недоступна", "Омега" }),
            "The actual favorite menu orders available and unavailable rows alphabetically by their displayed names.");
        favorites.SelectedItem = favorites.Items.Single(item => item.Title == "Омега");
        host.Call("RefreshFavoritesMenu");
        Assert(favorites.SelectedItem.Title == "Омега", "Favorite rebuilds preserve the selected model identity.");

        var unavailable = (NativeListItem<string>)favorites.Items.Single(item => item.Title == "Модель недоступна");
        favorites.SelectedItem = unavailable;
        string path = Path.Combine(directory, "vehicle-favorites.xml");
        byte[] before = File.ReadAllBytes(path);
        host.Activate(unavailable);
        Assert(before.SequenceEqual(File.ReadAllBytes(path)) && host.Favorites.Contains(MissingFavoriteHash),
            "An ordinary A activation on an unavailable favorite cannot remove it or write its XML.");
        host.Call("RefreshFavoritesMenu");
        Assert(favorites.SelectedItem.Title == "Модель недоступна", "An unavailable favorite also retains its selected identity after rebuilding.");
        unavailable = (NativeListItem<string>)favorites.SelectedItem;
        unavailable.SelectedIndex = 1;
        host.Activate(unavailable);
        Assert(!host.Favorites.Contains(MissingFavoriteHash) && host.StoredFavorites().SetEquals(new[] { 9, 1, 8 }),
            "Choosing the explicit remove action deletes only the unavailable model through the existing favorites store.");
    }

    private static void CheckPeerSelection(MenuStorageHost host)
    {
        var main = new NativeMenu("Проверка", "Главное меню");
        host.Set("_mainMenu", main);
        host.Call("BuildPlayersMenu");
        NativeMenu menu = host.Menu("_peersMenu");
        Assert(main.Items.Single() is NativeSubmenuItem && menu.Parent == main && host.Pool.Contains(menu),
            "The actual partner builder uses LemonUI submenu navigation and registers its child before activation.");
        host.SetPeers((10, "Первый"), (20, "Выбранный"));
        int poolSize = host.Pool.Count();
        main.Visible = true;
        host.Activate(main.Items.Single());
        Assert(menu.Visible && !main.Visible && menu.Items.Count == 2 && host.Pool.Count() == poolSize,
            "The real submenu activation opens partners and populates rows on Shown without changing the pool.");
        menu.SelectedIndex = 1;
        host.SetPeers((30, "Новый"), (10, "Первый"), (20, "Выбранный"));
        host.Call("RefreshPeerRows");
        Assert(menu.SelectedItem.Title == "К Выбранный" && menu.SelectedIndex == 2,
            "A real roster rebuild keeps the selected peer ID when another player joins ahead of it.");
        host.SetPeers((20, "Выбранный"), (10, "Первый"));
        host.Call("RefreshPeerRows");
        Assert(menu.SelectedItem.Title == "К Выбранный", "A different player leaving cannot move selection to another peer.");
    }

    private static bool MenuStorageModelAvailable(Model __instance, ref bool __result)
    { __result = __instance.Hash != MissingFavoriteHash; return false; }

    private static bool CaptureMenuStorageNotification(string __0)
    { MenuStorageNotifications.Add(__0); return false; }

    private static bool CaptureMenuStorageWarning(string __0)
    { MenuStorageWarnings.Add(__0); return false; }

    private static bool MenuStoragePeerRosterUnavailable(out Dictionary<int, RageCoop.Client.Player> __0, ref bool __result)
    { __0 = new Dictionary<int, RageCoop.Client.Player>(); __result = false; return false; }

    private sealed class MenuStorageHost
    {
        private readonly Type _script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        private readonly object _instance;
        internal readonly ObjectPool Pool;
        internal IList Presets => (IList)Get("_presets");
        internal HashSet<int> Favorites => (HashSet<int>)Get("_favoriteHashes");

        internal MenuStorageHost(string directory)
        {
            _instance = FormatterServices.GetUninitializedObject(_script);
            Type resourceType = Engine.GetType("RageCoop.Client.Scripting.ClientResource", true)!;
            object resource = Activator.CreateInstance(resourceType)!;
            resourceType.GetProperty("DataFolder")!.SetValue(resource, directory);
            resourceType.GetProperty("Logger")!.SetValue(resource,
                FormatterServices.GetUninitializedObject(typeof(RageCoop.Core.Logger)));
            _script.BaseType!.GetProperty("CurrentResource")!.SetValue(_instance, resource);
            foreach (string field in new[] { "_presets", "_vehicleRows", "_unavailableFavoriteRows", "_favoriteHashes", "_catalog", "_peers", "_peerRows" })
                Set(field, Activator.CreateInstance(_script.GetField(field, Instance)!.FieldType)!);
            Type addon = Resource.GetType("RageCoopAdmin.Client.AddonVehicleDefinition", true)!;
            Set("_addonVehicles", Array.CreateInstance(addon, 0));
            Set("_active", true);
            Set("_catalogReady", true);
            Set("_presetStore", Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client.VehiclePresetStore", true)!, Instance, null, new object[] { directory }, null)!);
            Set("_favoriteStore", Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client.FavoriteVehicleStore", true)!, Instance, null, new object[] { directory }, null)!);
            Pool = (ObjectPool)FormatterServices.GetUninitializedObject(typeof(ObjectPool));
            typeof(ObjectPool).GetFields(Instance).Single(field => field.FieldType == typeof(List<IProcessable>)).SetValue(Pool, new List<IProcessable>());
            Set("_pool", Pool);
            var vehicle = new NativeMenu("Проверка", "Транспорт");
            Set("_vehicleMenu", vehicle);
            Pool.Add(vehicle);
        }

        internal object Get(string name) => _script.GetField(name, Instance)!.GetValue(_instance)!;
        internal void Set(string name, object value) => _script.GetField(name, Instance)!.SetValue(_instance, value);
        internal void Call(string name, params object[] args) => _script.GetMethod(name, Instance)!.Invoke(_instance, args);
        internal NativeMenu Menu(string name) => (NativeMenu)Get(name);
        internal string Name(object preset) => (string)preset.GetType().GetProperty("Name")!.GetValue(preset)!;
        internal string[] PresetNames() => Presets.Cast<object>().Select(Name).ToArray();
        internal string[] StoredPresetNames() => LoadPresets().Cast<object>().Select(Name).ToArray();
        internal object Preset(string name, int hash)
        {
            object preset = Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client.VehiclePreset", true)!)!;
            preset.GetType().GetProperty("Name")!.SetValue(preset, name);
            object appearance = preset.GetType().GetProperty("Appearance")!.GetValue(preset)!;
            appearance.GetType().GetProperty("ModelHash")!.SetValue(appearance, hash);
            return preset;
        }
        internal void SetPresets(params object[] presets)
        { Presets.Clear(); foreach (object preset in presets) Presets.Add(preset); }
        internal void SavePresets(IList? values = null)
        {
            object store = Get("_presetStore");
            store.GetType().GetMethod("Save", Instance)!.Invoke(store, new object[] { values ?? Presets });
        }
        internal IList LoadPresets()
        {
            object store = Get("_presetStore");
            return (IList)store.GetType().GetMethod("Load", Instance)!.Invoke(store, Array.Empty<object>())!;
        }
        internal void OpenDelete(object preset) => Call("RequestPresetDeletion", preset);
        internal void SetFavorites(params int[] hashes)
        {
            Favorites.Clear(); Favorites.UnionWith(hashes);
            object store = Get("_favoriteStore");
            store.GetType().GetMethod("Save", Instance)!.Invoke(store, new object[] { Favorites });
        }
        internal HashSet<int> StoredFavorites()
        {
            object store = Get("_favoriteStore");
            return (HashSet<int>)store.GetType().GetMethod("Load", Instance)!.Invoke(store, Array.Empty<object>())!;
        }
        internal void AddCatalog(int hash, string code, string title)
        {
            Type entry = _script.GetNestedType("VehicleEntry", BindingFlags.NonPublic)!;
            ((IList)Get("_catalog")).Add(Activator.CreateInstance(entry, new object[] { hash, code, title, 0 })!);
        }
        internal void SetPeers(params (int Id, string Name)[] entries)
        {
            IList peers = (IList)Get("_peers");
            peers.Clear();
            Type peer = _script.GetNestedType("PeerSnapshot", BindingFlags.NonPublic)!;
            foreach (var entry in entries)
                peers.Add(Activator.CreateInstance(peer, Instance, null, new object[] { entry.Id, entry.Name, 10f, "Пешком", 1 }, null)!);
        }
        internal void Activate(NativeItem item) => typeof(NativeItem).GetMethod("OnActivated", Instance)!.Invoke(item, new object[] { item });
        internal void Controls(NativeMenu menu) => typeof(NativeMenu).GetMethod("ProcessControls", Instance)!.Invoke(menu, Array.Empty<object>());
        internal void Accept(bool held)
        {
            InputHeld.Clear(); InputEdges.Clear();
            if (held) { InputHeld.UnionWith(new[] { (0, 176), (0, 201) }); InputEdges.UnionWith(InputHeld); }
        }
    }
}
