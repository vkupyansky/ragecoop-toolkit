using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RageCoop.Client.Scripting;

internal static partial class Program
{
    private static void CheckResourceIdentity()
    {
        Type identity = Resource.GetType("RageCoopAdmin.Client.ResourceIdentity", true)!;
        MethodInfo normalize = identity.GetMethod("Normalize", Static)!;
        MethodInfo cleanup = identity.GetMethod("CleanupObsolete", Static)!;
        Type hostResources = Engine.GetType("RageCoop.Client.Scripting.Resources", true)!;
        MethodInfo load = hostResources.GetMethod("LoadResource", Instance)!;
        Type zipType = load.GetParameters()[0].ParameterType;
        Type outputType = zipType.Assembly.GetType("ICSharpCode.SharpZipLib.Zip.ZipOutputStream", true)!;
        Type entryType = zipType.Assembly.GetType("ICSharpCode.SharpZipLib.Zip.ZipEntry", true)!;
        string root = Path.Combine(Path.GetTempPath(), "CoopResourceIdentity-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "data");
        string canonical = Path.Combine(data, "CoopControl");
        Directory.CreateDirectory(canonical);
        var warnings = new List<string>();
        void Normalize(ClientResource resource) => normalize.Invoke(null, new object[] { resource });
        void Cleanup(ClientResource resource) => cleanup.Invoke(null, new object[] { resource, new Action<string>(warnings.Add) });
        object Store(string type, ClientResource resource) => Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client." + type, true)!, Instance, null, new object[] { resource.DataFolder }, null)!;
        object LoadStore(object store) => store.GetType().GetMethod("Load", Instance)!.Invoke(store, Array.Empty<object>())!;
        void Save(object store, object value) => store.GetType().GetMethod("Save", Instance)!.Invoke(store, new[] { value });
        ClientResource LoadResource(string path)
        {
            object resources = Activator.CreateInstance(hostResources, true)!;
            object zip = Activator.CreateInstance(zipType, new object[] { path })!;
            load.Invoke(resources, new[] { zip, data });
            return ((IEnumerable)hostResources.GetField("LoadedResources", Instance)!.GetValue(resources)!).Cast<ClientResource>().Single();
        }
        string Archive(string text, bool legacy = false)
        {
            string temporary = Path.Combine(root, "creating.zip");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = (Stream)Activator.CreateInstance(outputType, new object[] { file })!)
            {
                outputType.GetMethod("SetLevel")!.Invoke(writer, new object[] { 0 });
                object entry = Activator.CreateInstance(entryType, new object[] { "version.txt" })!;
                entryType.GetProperty("DateTime")!.SetValue(entry, new DateTime(2020, 1, 2));
                outputType.GetMethod("PutNextEntry")!.Invoke(writer, new[] { entry });
                writer.Write(bytes, 0, bytes.Length);
            }
            string digest;
            using (SHA256 hash = SHA256.Create())
            using (Stream file = File.OpenRead(temporary))
                digest = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", string.Empty).ToLowerInvariant();
            string name = legacy ? "CoopControl.res" : "CoopControl." + digest + ".res";
            string result = Path.Combine(root, name);
            // Invoke the actual length-only host check while only the old
            // same-size archive exists. A new digest must force a download.
            if (!legacy)
            {
                MethodInfo exists = Engine.GetType("RageCoop.Client.DownloadManager", true)!.GetMethod("FileAlreadyExists", Static)!;
                Assert(!(bool)exists.Invoke(null, new object[] { root, name, new FileInfo(temporary).Length })!,
                    "The actual client download cache cannot reuse another revision for a new content-addressed filename.");
            }
            File.Move(temporary, result);
            return result;
        }
        try
        {
            ClientResource legacy = LoadResource(Archive("old-code", legacy: true));
            Normalize(legacy);
            Assert(legacy.Name == "CoopControl" && legacy.DataFolder == canonical, "The legacy resource keeps its original profile directory.");
            object favorites = Store("FavoriteVehicleStore", legacy);
            Save(favorites, new HashSet<int> { 11, 22 });
            object settingsStore = Store("InterfaceSettingsStore", legacy);
            object settings = LoadStore(settingsStore);
            foreach (string property in new[] { "KeyboardBoost", "GamepadShortcuts", "ShowSpeedometer" })
                settings.GetType().GetProperty(property)!.SetValue(settings, false);
            settings.GetType().GetProperty("RepairShortcut")!.SetValue(settings, 1);
            Save(settingsStore, settings);
            object presetsStore = Store("VehiclePresetStore", legacy);
            IList presets = (IList)LoadStore(presetsStore);
            object preset = Activator.CreateInstance(Resource.GetType("RageCoopAdmin.Client.VehiclePreset", true)!)!;
            preset.GetType().GetProperty("Name")!.SetValue(preset, "Моя машина");
            object appearance = preset.GetType().GetProperty("Appearance")!.GetValue(preset)!;
            appearance.GetType().GetProperty("ModelHash")!.SetValue(appearance, 12345);
            presets.Add(preset);
            Save(presetsStore, presets);
            byte[] interfaceBytes = File.ReadAllBytes(Path.Combine(canonical, "interface.xml"));
            byte[] presetBytes = File.ReadAllBytes(Path.Combine(canonical, "vehicle-presets.xml"));

            string firstPath = Archive("old-code");
            ClientResource first = LoadResource(firstPath);
            string firstAutomaticFolder = first.DataFolder;
            Assert(first.Name == Path.GetFileNameWithoutExtension(firstPath) && Directory.Exists(firstAutomaticFolder),
                "The actual installed resource loader derives hashed Name and creates its temporary data directory.");
            Normalize(first); Normalize(first);
            Assert(first.DataFolder == canonical && ((HashSet<int>)LoadStore(Store("FavoriteVehicleStore", first))).SetEquals(new[] { 11, 22 }),
                "Normalizing the real hashed ClientResource is idempotent and reads the existing favorites.");
            Cleanup(first);
            Assert(!Directory.Exists(firstAutomaticFolder), "After successful initialization only the empty automatically created hashed data directory is removed.");
            favorites = Store("FavoriteVehicleStore", first);
            favorites.GetType().GetMethod("Update", Instance)!.Invoke(favorites, new object[] { new Action<HashSet<int>>(values => values.Add(33)) });

            string nextPath = Archive("new-code");
            Assert(new FileInfo(firstPath).Length == new FileInfo(nextPath).Length && firstPath != nextPath,
                "Equal-size valid resource archives with changed content have different SHA256 filenames.");
            ClientResource next = LoadResource(nextPath);
            string nextAutomaticFolder = next.DataFolder;
            Normalize(next);
            object newSettings = LoadStore(Store("InterfaceSettingsStore", next));
            Assert(next.DataFolder == canonical && ((HashSet<int>)LoadStore(Store("FavoriteVehicleStore", next))).SetEquals(new[] { 11, 22, 33 }) &&
                ((IList)LoadStore(Store("VehiclePresetStore", next))).Count == 1 &&
                new[] { "KeyboardBoost", "GamepadShortcuts", "ShowSpeedometer" }.All(property => !(bool)newSettings.GetType().GetProperty(property)!.GetValue(newSettings)!) &&
                (int)newSettings.GetType().GetProperty("RepairShortcut")!.GetValue(newSettings)! == 1,
                "A same-size resource update reads the saved garage, favorites and explicit opt-outs from the same canonical profile.");

            string obsolete = Path.Combine(root, "CoopControl." + new string('a', 64) + ".res");
            string malformed = Path.Combine(root, "CoopControl." + new string('g', 64) + ".res");
            string shortHash = Path.Combine(root, "CoopControl." + new string('a', 63) + ".res");
            string other = Path.Combine(root, "OtherResource." + new string('a', 64) + ".res");
            foreach (string path in new[] { obsolete, malformed, shortHash, other }) File.WriteAllText(path, "isolated cache fixture");
            string retainedData = Path.Combine(nextAutomaticFolder, "do-not-delete.txt");
            File.WriteAllText(retainedData, "Existing data must be retained");
            Cleanup(next);
            Assert(!File.Exists(firstPath) && !File.Exists(obsolete) && File.Exists(nextPath) && File.Exists(Path.Combine(root, "CoopControl.res")) &&
                File.Exists(malformed) && File.Exists(shortHash) && File.Exists(other) && File.Exists(retainedData),
                "Cleanup removes only obsolete exact CoopControl SHA256 archives and preserves the active archive, other names and nonempty data.");
            File.Delete(retainedData);
            Cleanup(next);
            Assert(!Directory.Exists(nextAutomaticFolder) && interfaceBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(canonical, "interface.xml"))) &&
                presetBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(canonical, "vehicle-presets.xml"))) && warnings.Count == 0,
                "Cache cleanup removes an empty temporary folder without rewriting settings or saved vehicles.");

            var foreign = new ClientResource();
            typeof(ClientResource).GetProperty("Name")!.SetValue(foreign, "OtherResource." + new string('a', 64));
            string foreignFolder = Path.Combine(data, foreign.Name);
            typeof(ClientResource).GetProperty("DataFolder")!.SetValue(foreign, foreignFolder);
            bool rejected = false;
            try { Normalize(foreign); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
            File.WriteAllText(obsolete, "must remain outside a valid CoopControl initialization");
            Cleanup(foreign);
            Assert(rejected && foreign.DataFolder == foreignFolder && File.Exists(obsolete) && warnings.Count == 1,
                "A different resource identity cannot redirect its data or clean CoopControl archives.");

            List<CodeInstruction> initialize = PatchProcessor.GetOriginalInstructions(Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!.GetMethod("Initialize", Instance)!);
            int normalization = initialize.FindIndex(code => code.Calls(normalize));
            int complete = initialize.FindLastIndex(code => code.opcode == OpCodes.Stfld && code.operand is FieldInfo field && field.Name == "_active");
            Assert(normalization >= 0 && initialize.Where(code => code.operand is MethodInfo method &&
                method.Name is "InitializeFavorites" or "InitializeInterface" or "InitializeVehicleTools").All(code => initialize.IndexOf(code) > normalization) &&
                initialize.FindIndex(code => code.Calls(cleanup)) > complete,
                "The assembled Initialize normalizes before all stores and cleans download cache only after successful activation.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
