using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.EarlyClient;

internal static partial class EarlyClientLifecycle
{
    private static void CheckEarlyLocalGuardOwners(bool installed)
    {
        CheckServerBrowserTlsOwners(installed);
        foreach (var pair in new[] { (Type: "LoggerFileGuard", Owner: "friendslegacy.ragecoop.earlylogger", Count: 2),
            (Type: "ClientSettingsGuard", Owner: "friendslegacy.ragecoop.earlysettings", Count: 3),
            (Type: "ResourceAssemblyLoadGuard", Owner: "friendslegacy.ragecoop.earlyresources", Count: 1) })
        {
            Type type = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient." + pair.Type, true)!;
            MethodBase[] targets = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(pair.Owner) == true).ToArray();
            Assert(targets.Length == (installed ? pair.Count : 0) && targets.All(target =>
                Harmony.GetPatchInfo(target)!.Prefixes.Concat(Harmony.GetPatchInfo(target)!.Transpilers).Concat(Harmony.GetPatchInfo(target)!.Finalizers)
                    .Where(patch => patch.owner == pair.Owner).All(patch => patch.PatchMethod.DeclaringType == type)),
                pair.Type + " has only its expected installed-DLL patches during constructor, abort and reload.");
        }
    }

    private static void UninstallEarlyLocalGuards()
    {
        foreach (string name in new[] { "ServerBrowserTlsGuard", "ResourceAssemblyLoadGuard", "ClientSettingsGuard", "LoggerFileGuard" })
            typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient." + name, true)!.GetMethod("Uninstall", Static)!.Invoke(null, null);
    }

    private static void CheckClientSettings(string binaries)
    {
        CheckStartupOrder(binaries);
        Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.ClientSettingsGuard", true)!;
        ConstructorInfo main = (ConstructorInfo)guard.GetProperty("MainConstructor", Static)!.GetValue(null)!;
        MethodInfo read = (MethodInfo)guard.GetProperty("ReadTarget", Static)!.GetValue(null)!;
        MethodInfo save = (MethodInfo)guard.GetProperty("SaveTarget", Static)!.GetValue(null)!;
        FieldInfo defaultPath = read.DeclaringType!.GetField("SettingsPath", Static)!;
        object previousPath = defaultPath.GetValue(null)!;
        string root = Path.Combine(Path.GetTempPath(), "CoopClientSettings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "RageCoop.Client.Settings.xml");
        Directory.CreateDirectory(root);
        Settings Read(string file) => (Settings)read.Invoke(null, new object[] { file })!;
        bool Save(Settings settings) => (bool)save.Invoke(null, new object[] { path, settings })!;
        void Same(byte[] bytes) => Assert(bytes.SequenceEqual(File.ReadAllBytes(path)), "The existing client settings bytes are preserved.");
        try
        {
            Assert((bool)guard.GetMethod("HasSupportedContract", Static)!.Invoke(null, null)!,
                "The early settings guard matches the actual installed Main constructor and both Util methods.");
            Assert(new MethodBase[] { main, read, save }.All(method => Harmony.GetPatchInfo(method)?.Owners.Contains("friendslegacy.ragecoop.earlysettings") == true),
                "The actual early constructor protects settings before any synthetic host event.");

            byte[] malformed = Encoding.UTF8.GetBytes("<Settings><Username>private-fixture</Username><Password>do-not-report</Password>");
            File.WriteAllBytes(path, malformed);
            defaultPath.SetValue(null, path);
            int constructors = _baseConstructors;
            Exception? failure = null;
            try { main.Invoke(Array.Empty<object>()); }
            catch (TargetInvocationException error) { failure = error.InnerException; }
            Assert(failure is InvalidDataException && !failure.ToString().Contains("private-fixture") && !failure.ToString().Contains("do-not-report") &&
                _baseConstructors == constructors,
                "The real installed Main constructor rejects malformed settings before its base/game body and destructive catch, without exposing XML values.");
            Same(malformed);
            Assert(!Save(new Settings()), "The actual Util.SaveSettings refuses to replace an unreadable existing file.");
            Same(malformed);
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Rejected client settings leave no temporary file.");

            var configured = new Settings
            {
                Username = "Проверка",
                Password = "fixture-only-value",
                LastServerAddress = "192.0.2.8:4499",
                MasterServer = "https://example.invalid/",
                LogLevel = 2,
                FlipMenu = true,
                Voice = true,
                MenuKey = System.Windows.Forms.Keys.F8,
                PassengerKey = System.Windows.Forms.Keys.H,
                DisableTraffic = true,
                DisableAlternatePause = false,
                WorldVehicleSoftLimit = 40,
                WorldPedSoftLimit = 60,
                DataDirectory = "fixture-data",
                ShowEntityOwnerName = true,
                ShowPlayerNameTag = false,
                ShowPlayerBlip = false
            };
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(Settings)).Serialize(stream, configured);
            byte[] valid = File.ReadAllBytes(path);
            Settings loaded = Read(path);
            Assert(SettingsEqual(configured, loaded), "The actual read preserves every normal installed client setting, including explicit false values.");
            Same(valid);
            Assert(SettingsEqual(configured, Read(path)), "A second read preserves the same settings and bytes.");
            Same(valid);

            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert(!Save(new Settings()), "An unavailable existing file causes the installed bool save contract to fail without truncation.");
            Same(valid);
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "A failed write leaves no owned temporary file.");
            Settings unserializable = Read(path);
            unserializable.Username = "\ud800";
            Assert(!Save(unserializable), "A serialization failure reports false instead of truncating the previous client settings.");
            Same(valid);
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "A serialization failure removes only its own incomplete temporary file.");
            loaded.WorldVehicleSoftLimit = 48;
            loaded.Password = "changed-fixture-only";
            Assert(Save(loaded) && SettingsEqual(loaded, Read(path)), "A normal actual Util save commits the complete changed settings atomically.");
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "A successful write consumes its owned temporary file.");

            string initial = Path.Combine(root, "initial", "RageCoop.Client.Settings.xml");
            Settings defaults = Read(initial);
            byte[] first = File.ReadAllBytes(initial);
            Assert(SettingsEqual(new Settings(), defaults) && SettingsEqual(defaults, Read(initial)) && first.SequenceEqual(File.ReadAllBytes(initial)),
                "First startup publishes ordinary installed defaults once; later reads do not rewrite them.");
            Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Initial publication leaves no owned temporary file.");

            MethodInfo reject = guard.GetMethod("RejectDefaultOverwrite", Static)!;
            Assert(PatchProcessor.GetCurrentInstructions(main).Count(code => code.Calls(reject)) == 1 &&
                !PatchProcessor.GetCurrentInstructions(main).Any(code => code.Calls(save)),
                "The patched real Main catch cannot overwrite a file that changes after the initial validation.");
        }
        finally
        {
            defaultPath.SetValue(null, previousPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool SettingsEqual(Settings first, Settings second) => typeof(Settings).GetProperties().All(property =>
        Equals(property.GetValue(first), property.GetValue(second))) && typeof(Settings).GetFields().All(field => Equals(field.GetValue(first), field.GetValue(second)));

    private static void CheckStartupOrder(string binaries)
    {
        // Inspect the exact managed metadata of the installed native ASI. It is
        // never loaded for execution. A separate domain keeps its identity apart
        // from the server stub used to declare the synthetic host boundary.
        AppDomain inspection = AppDomain.CreateDomain("Inspect installed SHVDN startup metadata");
        try
        {
            object boundary = inspection.CreateInstanceFromAndUnwrap(
                Path.Combine(binaries, "EarlyClientCheck.exe"), typeof(EarlyStartupContractBoundary).FullName);
            Assert((bool)boundary.GetType().GetMethod("Check")!.Invoke(boundary,
                new object[] { Path.Combine(binaries, "HostContract", "ScriptHookVDotNet.asi") })!,
                "The installed ASI retains the inspected SortedList, default comparer, assembly-name keys and ordered Start enumeration.");
        }
        finally { AppDomain.Unload(inspection); }
        AssemblyName early = typeof(EarlyClientScript).Assembly.GetName(), engine = typeof(Settings).Assembly.GetName();
        string earlyKey = early.Name + "-" + early.Version + typeof(EarlyClientScript).FullName + "%%";
        string mainKey = engine.Name + "-" + engine.Version + "RageCoop.Client.Main%%";
        var actualOrder = new SortedList<string, string> { [mainKey] = "Main", [earlyKey] = "Early" };
        Assert(early.Name == "CoopControl.EarlyClient" && actualOrder.Values.SequenceEqual(new[] { "Early", "Main" }),
            "The built early assembly sorts before the installed RageCoop.Client.Main under the actual host key contract.");
    }
}

public sealed class EarlyStartupContractBoundary : MarshalByRefObject
{
    public bool Check(string path)
    {
        ResolveEventHandler resolver = (_, args) => Assembly.ReflectionOnlyLoad(args.Name);
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
        try { return Inspect(path); }
        finally { AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver; }
    }

    private static bool Inspect(string path)
    {
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        Assembly host = Assembly.ReflectionOnlyLoad(File.ReadAllBytes(path));
        Type domain = host.GetType("SHVDN.ScriptDomain", true)!;
        FieldInfo types = domain.GetField("_scriptTypes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Type map = types.FieldType;
        if (!(map.IsGenericType && map.GetGenericTypeDefinition().FullName == typeof(SortedList<,>).FullName &&
            map.GetGenericArguments()[0].FullName == typeof(string).FullName &&
            map.GetGenericArguments()[1].FullName == "SHVDN.ScriptDomain+ScriptTypeInfo")) return false;
        MethodInfo register = domain.GetMethods(members).Single(method => method.Name.Contains("g__RegisterScriptTypesInAssembly"));
        MethodInfo start = domain.GetMethod("Start", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        return MethodHash(domain.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single()) ==
            "ca8287a26464460b46542f28af0fbffd451e52e108401df2b826cad800cf6aec" &&
            MethodHash(register) == "bded331aae65200a1b3f7c9cb6b28589ae423482b14c483888a186861515564b" &&
            MethodHash(start) == "d3c80e5cbd09a820ca15f78a1f9a14c7655e5cef992078a00de43fe958ff2dd6";
    }

    private static string MethodHash(MethodBase method)
    {
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(method.GetMethodBody()!.GetILAsByteArray()!)).Replace("-", string.Empty).ToLowerInvariant();
    }
}
