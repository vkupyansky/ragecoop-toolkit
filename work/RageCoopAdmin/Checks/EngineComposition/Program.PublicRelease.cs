using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using GTA;
using LemonUI.Menus;

internal static partial class Program
{
    private static int CheckPublicEngine()
    {
        var boundary = new Harmony("friendslegacy.check.publicengine");
        try
        {
            boundary.Patch(typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
            InitializeHost();
            CheckEngineGuardComposition();
            Console.WriteLine("Bounded actual engine guard composition passed: install, shared sends, packet decode, reinstall and unload against the loaded baseline; GTA natives and live connections were isolated. This is not the complete input or game test suite.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            RemoveSharedRuntimeGuards();
            foreach (Guard guard in Guards.Reverse()) { guard.Uninstall(); }
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static int CheckReleaseProfile(bool publicRelease)
    {
        var boundary = new Harmony("friendslegacy.check.publicrelease");
        string directory = Path.Combine(Path.GetTempPath(), "CoopPublicRelease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            boundary.Patch(typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
            InitializeHost();
            if (!publicRelease)
            {
                CheckSpeedometerAssets();
                CheckSpeedometerTextureCache();
                CheckSpeedometerRendering();
                CheckLocalVehicleInput();
                CheckSpeedometerInitialization();
                Console.WriteLine("Private client profile passed against the actual built DLL; GTA graphics and physical devices were isolated.");
                return 0;
            }
            const string catalogName = "RageCoopAdmin.Client.AddonVehicles.json";
            Assert(Resource.GetManifestResourceNames().SequenceEqual(new[] { catalogName }),
                "The actual public DLL contains only the vehicle catalog resource and no PNG or FSS assets.");
            Type catalog = Resource.GetType("RageCoopAdmin.Client.AddonVehicleCatalog", true)!;
            var entries = (IList)catalog.GetMethod("LoadEmbedded", Static)!.Invoke(null, Array.Empty<object>())!;
            Assert(entries.Count == 0, "The actual public DLL loads a valid empty vehicle catalog.");
            Assert(Resource.GetTypes().All(type => type.Name.IndexOf("Speedometer", StringComparison.OrdinalIgnoreCase) < 0),
                "The public DLL has no speedometer implementation, texture cache or initialization types.");
            Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
            Assert(script.GetMembers(Instance).All(member => member.Name.IndexOf("Speedometer", StringComparison.OrdinalIgnoreCase) < 0) &&
                Resource.GetType("RageCoopAdmin.Client.InterfaceSettings", true)!.GetProperty("ShowSpeedometer") == null,
                "The public script and settings expose no speedometer fields, handlers or option.");
            Assert(!PatchProcessor.GetOriginalInstructions(script.GetMethod("BuildInterfaceMenu", Instance)!)
                .Any(code => code.operand is string text && text.IndexOf("Спидометр", StringComparison.OrdinalIgnoreCase) >= 0),
                "The compiled settings builder contains no speedometer label.");

            PatchMenuStorageBoundaries(boundary);
            var host = new MenuStorageHost(directory);
            host.Set("_interfaceSettings", Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client.InterfaceSettings", true)!)!);
            host.Set("_mainMenu", new NativeMenu("Проверка", "Главное меню"));
            host.Call("BuildInterfaceMenu");
            NativeMenu settings = host.Pool.OfType<NativeMenu>().Single(menu => menu.Name == "Настройки");
            Assert(settings.Items.Select(item => item.Title).SequenceEqual(new[]
                { "Ремонт клавишей K", "Сочетания на геймпаде", "Разгон клавишей O" }),
                "The real public settings menu retains its three controls without an empty HUD option.");
            int count = host.Pool.Count();
            host.Call("BuildAddonVehiclesMenu");
            Assert(host.Pool.Count() == count && host.Get("_addonVehiclesMenu") == null,
                "The real builder adds no empty add-on vehicle submenu.");
            CheckVehicleToolsComposition();
            CheckMenuStorageComposition();
            CheckPrivateSettingsPreservation(directory);
            Console.WriteLine("Public client profile passed against the actual built DLL; GTA graphics and network connections were isolated.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            boundary.UnpatchAll(boundary.Id);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckPrivateSettingsPreservation(string directory)
    {
        Type settingsType = Resource.GetType("RageCoopAdmin.Client.InterfaceSettings", true)!;
        Type storeType = Resource.GetType("RageCoopAdmin.Client.InterfaceSettingsStore", true)!;
        object store = Activator.CreateInstance(storeType, Instance, null, new object[] { directory }, null)!;
        string path = Path.Combine(directory, "interface.xml");
        File.WriteAllText(path, "<Interface version=\"5\"><RepairShortcut>0</RepairShortcut>" +
            "<ShowSpeedometer>false</ShowSpeedometer></Interface>");
        object loaded = storeType.GetMethod("Load", Instance)!.Invoke(store, Array.Empty<object>())!;
        settingsType.GetProperty("RepairShortcut")!.SetValue(loaded, 1);
        storeType.GetMethod("Save", Instance)!.Invoke(store, new[] { loaded });
        void Preserved(string label)
        {
            var document = new System.Xml.XmlDocument();
            document.Load(path);
            Assert(document.SelectSingleNode("/Interface/ShowSpeedometer")?.InnerText == "false", label);
        }
        Preserved("Saving an edited public settings object retains the existing private HUD opt-out as opaque XML.");
        object fresh = Activator.CreateInstance(settingsType)!;
        storeType.GetMethod("Save", Instance)!.Invoke(store, new[] { fresh });
        Preserved("Saving a fresh public settings object also retains the latest private HUD opt-out.");
        var change = new Action<object>(value => settingsType.GetProperty("RepairShortcut")!.SetValue(value, 1));
        storeType.GetMethod("Update", Instance)!.Invoke(store, new object[] { change });
        Preserved("The actual public Update transaction carries the private option through a public control change.");
    }
}
