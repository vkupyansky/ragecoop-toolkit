using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using HarmonyLib;
using LemonUI.Menus;
using RageCoop.Client;

internal static partial class Program
{
    private static object _storageOwnedVehicle = null!;
    private static object _storageAppearance = null!;

    private static void CheckConcurrentMenuStorage()
    {
        var boundary = new Harmony("friendslegacy.check.concurrentmenustorage");
        string directory = Path.Combine(Path.GetTempPath(), "CoopStorageTransactions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        AppDomain? domain = null;
        StorageWriterProbe? probe = null;
        try
        {
            PatchMenuStorageBoundaries(boundary);
            Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
            boundary.Patch(script.GetMethod("TryGetOwnedVehicle", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(StorageOwnedVehicle)));
            boundary.Patch(script.GetMethod("TryCaptureVehicleAppearance", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(StorageCaptureAppearance)));
            var host = new MenuStorageHost(directory);
            host.SetPresets(host.Preset("Первая", 101));
            host.SavePresets();
            IList external = host.LoadPresets();
            external.Add(host.Preset("Вторая", 202));
            host.SavePresets(external);
            var synced = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
            typeof(SyncedEntity).GetProperty("ID")!.SetValue(synced, 901);
            Type watched = script.GetNestedType("WatchedVehicle", BindingFlags.NonPublic)!;
            _storageOwnedVehicle = Activator.CreateInstance(watched, new object[] { synced })!;
            object snapshot = host.Preset("Снимок", 303);
            _storageAppearance = snapshot.GetType().GetProperty("Appearance")!.GetValue(snapshot)!;
            host.Call("SaveNamedVehiclePreset", 901, "Третья");
            Assert(host.StoredPresetNames().SequenceEqual(new[] { "Первая", "Вторая", "Третья" }),
                "The real named-save callback merges an externally added car instead of overwriting it with the session's old garage.");
            string presetsPath = Path.Combine(directory, "vehicle-presets.xml");
            File.WriteAllText(presetsPath, "<broken>");
            byte[] invalid = File.ReadAllBytes(presetsPath);
            host.Call("SaveNamedVehiclePreset", 901, "Четвёртая");
            Assert(invalid.SequenceEqual(File.ReadAllBytes(presetsPath)), "Saving a new car preserves XML that became unreadable after joining.");

            host.SetFavorites(11);
            object favorites = host.Get("_favoriteStore");
            favorites.GetType().GetMethod("Save", Instance)!.Invoke(favorites, new object[] { new HashSet<int> { 11, 22 } });
            host.Call("ToggleFavorite", 33);
            Assert(host.StoredFavorites().SetEquals(new[] { 11, 22, 33 }) && host.Favorites.SetEquals(new[] { 11, 22, 33 }),
                "The actual favorite action preserves another writer's models and refreshes the session from the committed list.");
            string favoritePath = Path.Combine(directory, "vehicle-favorites.xml");
            File.WriteAllText(favoritePath, "<broken>");
            invalid = File.ReadAllBytes(favoritePath);
            host.Call("ToggleFavorite", 44);
            Assert(invalid.SequenceEqual(File.ReadAllBytes(favoritePath)) && !host.Favorites.Contains(44),
                "A favorite action cannot overwrite a file that became unreadable after its initial load.");
            File.Delete(favoritePath);
            host.SetFavorites(11, 22, 33);

            Type settingsType = Resource.GetType("RageCoopAdmin.Client.InterfaceSettings", true)!;
            Type settingsStoreType = Resource.GetType("RageCoopAdmin.Client.InterfaceSettingsStore", true)!;
            object settingsStore = Activator.CreateInstance(settingsStoreType, Instance, null, new object[] { directory }, null)!;
            object settings = Activator.CreateInstance(settingsType)!;
            host.Set("_interfaceStore", settingsStore);
            host.Set("_interfaceSettings", settings);
            host.Set("_mainMenu", new NativeMenu("Проверка", "Главное меню"));
            host.Call("BuildInterfaceMenu");
            object externalSettings = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("KeyboardBoost")!.SetValue(externalSettings, false);
            settingsType.GetProperty("GamepadShortcuts")!.SetValue(externalSettings, false);
            settingsStoreType.GetMethod("Save", Instance)!.Invoke(settingsStore, new[] { externalSettings });
            // The UI has its old checkbox values. Change only the speedometer.
            NativeMenu options = host.Pool.OfType<NativeMenu>().Single(menu => menu.Name == "Настройки");
            var speedometer = (NativeCheckboxItem)options.Items.Single(item => item.Title == "Спидометр");
            speedometer.Checked = false;
            object saved = settingsStoreType.GetMethod("Load", Instance)!.Invoke(settingsStore, Array.Empty<object>())!;
            Assert(!(bool)settingsType.GetProperty("KeyboardBoost")!.GetValue(saved)! &&
                !(bool)settingsType.GetProperty("GamepadShortcuts")!.GetValue(saved)! &&
                !(bool)settingsType.GetProperty("ShowSpeedometer")!.GetValue(saved)!,
                "Changing one real settings checkbox preserves externally saved opt-outs for the other controls.");
            string settingsPath = Path.Combine(directory, "interface.xml");
            File.WriteAllText(settingsPath, "<broken>");
            invalid = File.ReadAllBytes(settingsPath);
            speedometer.Checked = true;
            Assert(invalid.SequenceEqual(File.ReadAllBytes(settingsPath)), "A settings checkbox also preserves a newly unreadable source file.");

            domain = AppDomain.CreateDomain("Storage mutex " + Guid.NewGuid().ToString("N"));
            probe = (StorageWriterProbe)domain.CreateInstanceFromAndUnwrap(typeof(StorageWriterProbe).Assembly.Location, typeof(StorageWriterProbe).FullName);
            File.Delete(settingsPath);
            settingsStoreType.GetMethod("Save", Instance)!.Invoke(settingsStore, new[] { Activator.CreateInstance(settingsType)! });
            var boost = (NativeCheckboxItem)options.Items.Single(item => item.Title == "Разгон клавишей O");
            // Reenable in memory and on disk before exercising a failed opt-out.
            boost.Checked = true;
            probe.Hold(Resource.Location, settingsPath);
            boost.Checked = false;
            saved = settingsStoreType.GetMethod("Load", Instance)!.Invoke(settingsStore, Array.Empty<object>())!;
            Assert((bool)settingsType.GetProperty("KeyboardBoost")!.GetValue(saved)! &&
                !(bool)settingsType.GetProperty("KeyboardBoost")!.GetValue(host.Get("_interfaceSettings"))!,
                "A busy real writer leaves a failed boost opt-out active in the running UI while preserving the file.");
            probe.Release();
            var gamepad = (NativeCheckboxItem)options.Items.Single(item => item.Title == "Сочетания на геймпаде");
            gamepad.Checked = false;
            saved = settingsStoreType.GetMethod("Load", Instance)!.Invoke(settingsStore, Array.Empty<object>())!;
            Assert(!(bool)settingsType.GetProperty("KeyboardBoost")!.GetValue(saved)! &&
                !(bool)settingsType.GetProperty("GamepadShortcuts")!.GetValue(saved)! && !boost.Checked,
                "Saving another real checkbox retries earlier unsaved opt-outs instead of unexpectedly reenabling boost.");
            probe = (StorageWriterProbe)domain.CreateInstanceFromAndUnwrap(typeof(StorageWriterProbe).Assembly.Location, typeof(StorageWriterProbe).FullName);
            probe.Hold(Resource.Location, favoritePath);
            byte[] before = File.ReadAllBytes(favoritePath);
            Assert(host.StoredFavorites().SetEquals(new[] { 11, 22, 33 }),
                "A writer reservation does not prevent atomic snapshot reads or force an existing profile back to defaults.");
            host.Call("ToggleFavorite", 55);
            Assert(before.SequenceEqual(File.ReadAllBytes(favoritePath)) && !host.Favorites.Contains(55),
                "A real named mutex held by another AppDomain rejects a competing UI write without replacing data.");
            probe.Release();
            host.Call("ToggleFavorite", 55);
            Assert(host.StoredFavorites().SetEquals(new[] { 11, 22, 33, 55 }), "Retry after the other writer releases its lock commits the requested action.");
        }
        finally
        {
            probe?.Release();
            if (domain != null) AppDomain.Unload(domain);
            _storageOwnedVehicle = _storageAppearance = null!;
            boundary.UnpatchAll(boundary.Id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static bool StorageOwnedVehicle(object[] __args, ref bool __result)
    { __args[0] = _storageOwnedVehicle; __result = true; return false; }

    private static bool StorageCaptureAppearance(object[] __args, ref bool __result)
    { __args[1] = _storageAppearance; __result = true; return false; }
}

public sealed class StorageWriterProbe : MarshalByRefObject
{
    private Thread? _thread;
    private readonly ManualResetEvent _ready = new ManualResetEvent(false);
    private readonly ManualResetEvent _release = new ManualResetEvent(false);
    private Exception? _error;

    public void Hold(string assembly, string path)
    {
        _thread = new Thread(() =>
        {
            try
            {
                Type type = Assembly.LoadFrom(assembly).GetType("RageCoopAdmin.Client.StorageFileLock", true)!;
                using var held = (IDisposable)type.GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { path })!;
                _ready.Set();
                if (!_release.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("The isolated writer was not released.");
            }
            catch (Exception ex) { _error = ex; _ready.Set(); }
        });
        _thread.Start();
        if (!_ready.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("The isolated writer did not start.");
        if (_error != null) throw _error;
    }

    public void Release()
    {
        if (_thread == null) return;
        _release.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("The isolated writer did not finish.");
        _thread = null;
        _ready.Dispose(); _release.Dispose();
        if (_error != null) throw _error;
    }
}
