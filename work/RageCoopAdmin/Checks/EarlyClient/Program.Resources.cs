using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using RageCoop.EarlyClient;

internal static partial class EarlyClientLifecycle
{
    private const string ResourceLoadOwner = "friendslegacy.ragecoop.earlyresources";
    private static readonly List<string> ResourceTemporaryFiles = new List<string>();
    private static bool _resourceCopyFailure;
    private static IDisposable? _lastResourceArchive;

    internal static void CheckResourceAssemblyLoad(string binaries)
    {
        Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.ResourceAssemblyLoadGuard", true)!;
        MethodInfo target = (MethodInfo)guard.GetProperty("Target", Static)!.GetValue(null)!;
        Type resources = target.DeclaringType!;
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type zipType = target.GetParameters()[0].ParameterType;
        Type outputType = zipType.Assembly.GetType("ICSharpCode.SharpZipLib.Zip.ZipOutputStream", true)!;
        Type entryType = zipType.Assembly.GetType("ICSharpCode.SharpZipLib.Zip.ZipEntry", true)!;
        MethodInfo unload = resources.GetMethod("Unload", instance)!;
        MethodInfo load = resources.GetMethod("Load", instance)!;
        MethodInfo clearQueue = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Main", true)!.GetMethod("ClearQueuedActions", Static)!;
        string root = Path.Combine(Path.GetTempPath(), "CoopResourceLoad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "unrelated.tmp");
        File.WriteAllText(sentinel, "preserve unrelated files");
        var boundary = new Harmony("friendslegacy.check.earlyresources.boundary");
        var foreign = new Harmony("friendslegacy.check.earlyresources.foreign");
        void Install() => guard.GetMethod("Install", Static)!.Invoke(null, null);
        void Uninstall() => guard.GetMethod("Uninstall", Static)!.Invoke(null, null);
        object NewLoader() => Activator.CreateInstance(resources, true)!;
        IList Loaded(object loader) => (IList)resources.GetField("LoadedResources", instance)!.GetValue(loader)!;
        void Unload(object loader)
        {
            try { unload.Invoke(loader, null); }
            finally { clearQueue.Invoke(null, null); }
            Assert(Loaded(loader).Count == 0, "The actual resource Unload clears its loaded list; host reload callbacks are isolated and released.");
        }
        string Archive(string name, params (string Name, byte[] Bytes)[] entries)
        {
            string path = Path.Combine(root, name + ".res");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var zip = (Stream)Activator.CreateInstance(outputType, new object[] { file })!)
            {
                outputType.GetMethod("SetLevel")!.Invoke(zip, new object[] { 0 });
                foreach (var item in entries)
                {
                    object entry = Activator.CreateInstance(entryType, new object[] { item.Name })!;
                    outputType.GetMethod("PutNextEntry")!.Invoke(zip, new[] { entry });
                    zip.Write(item.Bytes, 0, item.Bytes.Length);
                }
            }
            return path;
        }
        void Load(object loader, string path)
        {
            object zip = Activator.CreateInstance(zipType, new object[] { path })!;
            try { target.Invoke(loader, new[] { zip, Path.Combine(root, "data") }); }
            finally { AssertArchiveReleased(path); }
        }
        void LoadThroughCaller(object loader, string path)
        {
            try { load.Invoke(loader, new object[] { root, new[] { Path.GetFileName(path) } }); }
            finally { AssertArchiveReleased(path); }
        }
        void AssertClean(int start)
        {
            Assert(ResourceTemporaryFiles.Count > start && ResourceTemporaryFiles.Skip(start).All(path => !File.Exists(path)),
                "The actual loader removes every exact temporary DLL it created, including skipped native DLLs.");
            Assert(File.ReadAllText(sentinel) == "preserve unrelated files", "Resource cleanup preserves an unrelated temporary file.");
        }
        try
        {
            boundary.Patch(typeof(Path).GetMethod(nameof(Path.GetTempFileName), Type.EmptyTypes)!,
                postfix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(CaptureResourceTemporaryFile)));
            boundary.Patch(typeof(Stream).GetMethod(nameof(Stream.CopyTo), new[] { typeof(Stream) })!,
                prefix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(ResourceCopyBoundary)));
            boundary.Patch(zipType.GetConstructor(new[] { typeof(string) })!,
                postfix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(CaptureResourceArchive)));
            foreign.Patch(target, postfix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(ResourceForeignPostfix)));
            Assert((bool)guard.GetMethod("HasSupportedContract", Static)!.Invoke(null, null)!,
                "The early resource guard matches the installed exact LoadResource IL and temporary-file/assembly call signatures.");
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains(ResourceLoadOwner) == true,
                "The real early script constructor installs resource cleanup before any resource download or host event.");

            string callerFailure = Archive("caller-failure", ("native.dll", new byte[] { 1, 2, 3 }));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "caller-failure"), "block resource directory");
            bool failed = false;
            try { LoadThroughCaller(NewLoader(), callerFailure); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
            Assert(failed, "Initial protected real Resources.Load retains its original data-path failure.");
            string native = Archive("native-only", ("native.dll", new byte[] { 1, 2, 3 }));
            int start = ResourceTemporaryFiles.Count;
            Install(); Install();
            Patches patches = Harmony.GetPatchInfo(target)!;
            Assert(patches.Prefixes.Count(patch => patch.owner == ResourceLoadOwner) == 1 &&
                patches.Transpilers.Count(patch => patch.owner == ResourceLoadOwner) == 1 &&
                patches.Finalizers.Count(patch => patch.owner == ResourceLoadOwner) == 1,
                "Repeated installation retains one owned prefix, transpiler and finalizer on the actual resource loader.");
            bool rejected = false;
            try
            {
                ((IEnumerable<CodeInstruction>)guard.GetMethod("ProtectTemporaryAssemblies", Static)!.Invoke(null,
                new object[] { new[] { new CodeInstruction(OpCodes.Ret) } })!).ToArray();
            }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "An incompatible resource body cannot receive a guessed temporary-assembly rewrite.");

            byte[] client = File.ReadAllBytes(Path.Combine(binaries, "RageCoopAdmin.Client.dll"));
            byte[] harmony = File.ReadAllBytes(Path.Combine(binaries, "0Harmony.dll"));
            string actual = Archive("CoopControl.fixture", ("RageCoopAdmin.Client.dll", client), ("0Harmony.dll", harmony),
                ("native.dll", new byte[] { 1, 2, 3 }), ("nested/ignored.dll", client),
                ("assets/readme.txt", Encoding.UTF8.GetBytes("resource fixture")));
            for (int reload = 0; reload < 2; reload++)
            {
                object loader = NewLoader();
                start = ResourceTemporaryFiles.Count;
                Load(loader, actual);
                AssertClean(start);
                var loaded = (ClientResource)Loaded(loader)[0]!;
                Assert(Loaded(loader).Count == 1 && loaded.Name == "CoopControl.fixture" &&
                    loaded.DataFolder == Path.Combine(root, "data", "CoopControl.fixture") && loaded.Files.Count == 5 &&
                    loaded.Files["assets/readme.txt"].Name == "assets/readme.txt" && !loaded.Files["native.dll"].IsDirectory &&
                    loaded.Files.ContainsKey("nested/ignored.dll"),
                    "The actual loader retains archive file metadata, resource name and its data-directory contract.");
                Assert(loaded.Scripts.Count == 1 && loaded.Scripts[0].GetType().Assembly == Resource &&
                    loaded.Scripts[0].GetType().FullName == "RageCoopAdmin.Client.AdminMenuScript" &&
                    ReferenceEquals(loaded.Scripts[0].CurrentResource, loaded) && loaded.Scripts[0].CurrentFile.Name == "RageCoopAdmin.Client.dll",
                    "The actual unchanged script constructor, discovery and resource/file assignments still run after the extracted DLLs are removed.");
                Assert(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.FullName == typeof(Harmony).Assembly.FullName) == 1 &&
                    AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.FullName == Resource.FullName) == 1,
                    "A two-DLL resource and its reload reuse the existing client resource and Harmony identities instead of loading duplicate copies.");
                using (Stream embedded = loaded.Scripts[0].GetType().Assembly.GetManifestResourceStream("RageCoopAdmin.Client.Assets.Speedometer.base.png")!)
                    Assert(embedded != null && embedded.Length > 0, "The real script can still read embedded assets after temporary DLL cleanup.");
                bool closedArchive = false;
                try { using Stream ignored = loaded.Files["assets/readme.txt"].GetStream(); }
                catch (ObjectDisposedException) { closedArchive = true; }
                Assert(closedArchive, "The installed loader's existing closed-ZIP GetStream limitation is unchanged; cleanup does not claim to fix archive-backed file access.");
                Unload(loader);
            }

            CheckFreshResourceAssembly(binaries, root, Archive, Load, NewLoader, Loaded, Unload, AssertClean);

            byte[] invalidManaged = new byte[512];
            Array.Copy(BitConverter.GetBytes(128), 0, invalidManaged, 60, 4);
            Array.Copy(BitConverter.GetBytes(17744), 0, invalidManaged, 128, 4);
            Array.Copy(BitConverter.GetBytes((ushort)267), 0, invalidManaged, 152, 2);
            Array.Copy(BitConverter.GetBytes(1), 0, invalidManaged, 360, 4);
            string corrupt = Archive("invalid-managed", ("broken.dll", invalidManaged));
            object failedLoader = NewLoader();
            start = ResourceTemporaryFiles.Count;
            failed = false;
            try { LoadThroughCaller(failedLoader, corrupt); }
            catch (TargetInvocationException error) when (error.InnerException is BadImageFormatException) { failed = true; }
            Assert(failed && Loaded(failedLoader).Count == 0, "A real invalid managed assembly preserves BadImageFormatException and cannot publish a successful resource.");
            AssertClean(start);

            _resourceCopyFailure = true;
            start = ResourceTemporaryFiles.Count;
            failed = false;
            try { LoadThroughCaller(failedLoader, actual); }
            catch (TargetInvocationException error) when (error.InnerException is IOException failure && failure.Message == "Isolated resource copy failure.") { failed = true; }
            finally { _resourceCopyFailure = false; }
            Assert(failed && Loaded(failedLoader).Count == 0, "An archive copy error keeps its original cause and cannot publish a partially loaded resource.");
            AssertClean(start);

            // Run the destructive baseline last. Protected real caller tests
            // above run with the initial early installation, as the host does.
            Uninstall();
            object original = NewLoader();
            start = ResourceTemporaryFiles.Count;
            Load(original, native);
            Assert(ResourceTemporaryFiles.Count == start + 1 && File.Exists(ResourceTemporaryFiles[start]),
                "The actual unpatched loader reproduces the temporary-file leak even for a skipped native DLL.");
            Unload(original);
            Assert(File.Exists(ResourceTemporaryFiles[start]), "The actual original resource Unload does not remove its extracted DLL.");
            File.Delete(ResourceTemporaryFiles[start]);
            failed = false;
            _lastResourceArchive = null;
            try { load.Invoke(NewLoader(), new object[] { root, new[] { Path.GetFileName(callerFailure) } }); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
            bool archiveLocked = false;
            try { using var probe = new FileStream(callerFailure, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { archiveLocked = true; }
            Assert(failed && archiveLocked && _lastResourceArchive != null,
                "The real original Resources.Load failure leaves its internally created ZIP locked before any test cleanup.");
            // Release only this proven baseline leak. Guarded calls above had
            // to pass exclusive access before any test-side disposal.
            _lastResourceArchive!.Dispose();
            _lastResourceArchive = null;
            AssertArchiveReleased(callerFailure);

            Uninstall(); Uninstall();
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains(ResourceLoadOwner) != true &&
                Harmony.GetPatchInfo(target)?.Owners.Contains(foreign.Id) == true,
                "Repeated resource-guard unload removes only its hooks and retains another component's patch.");
            Install();
            object afterReload = NewLoader();
            start = ResourceTemporaryFiles.Count;
            Load(afterReload, native); AssertClean(start); Unload(afterReload);
            Console.WriteLine("Actual resource assembly loading passed: original native-DLL and caller ZIP leaks reproduced; managed/native and input-archive cleanup, loaded identities, script construction, assets, real Resources.Load failures, reload and scoped unload verified without test-side ZIP disposal. GTA and host reload actions were not executed; the installed closed-ZIP file-stream limitation remains.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Resource loading check failed before fixture cleanup: " + error);
            // Failure cleanup happens only after the exclusive-open assertion
            // has failed; it cannot turn an archive leak into a passing check.
            _lastResourceArchive?.Dispose();
            throw;
        }
        finally
        {
            _resourceCopyFailure = false;
            _lastResourceArchive = null;
            RemoveOwner(foreign); RemoveOwner(boundary);
            foreach (string path in ResourceTemporaryFiles) if (File.Exists(path)) File.Delete(path);
            ResourceTemporaryFiles.Clear();
            clearQueue.Invoke(null, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckFreshResourceAssembly(string binaries, string root,
        Func<string, (string Name, byte[] Bytes)[], string> archive, Action<object, string> load,
        Func<object> newLoader, Func<object, IList> loaded, Action<object> unload, Action<int> clean)
    {
        string name = "CoopResourceFixture" + Guid.NewGuid().ToString("N");
        AppDomain domain = AppDomain.CreateDomain("Create resource fixture " + name);
        try
        {
            object builder = domain.CreateInstanceFromAndUnwrap(
                Path.Combine(binaries, "EarlyClientCheck.exe"), typeof(ResourceAssemblyFixtureBuilder).FullName);
            builder.GetType().GetMethod("Build")!.Invoke(builder, new object[] { root, name });
        }
        finally { AppDomain.Unload(domain); }
        Assert(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == name),
            "The fresh managed fixture identity is absent before the real resource loader reads its DLL bytes.");
        string path = archive("fresh-managed", new[] { (name + ".dll", File.ReadAllBytes(Path.Combine(root, name + ".dll"))) });
        object loader = newLoader();
        int start = ResourceTemporaryFiles.Count;
        load(loader, path); clean(start);
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == name);
        Assert(assembly.Location.Length == 0 && (int)assembly.GetType("ResourceFixture", true)!.GetMethod("Read")!.Invoke(null, null)! == 42 &&
            loaded(loader).Count == 1 && ((ClientResource)loaded(loader)[0]!).Scripts.Count == 0,
            "A previously unseen managed DLL loads as executable bytes and remains usable after deleting its exact extracted file.");
        unload(loader);
    }

    private static void CaptureResourceTemporaryFile(string __result) => ResourceTemporaryFiles.Add(__result);
    private static void CaptureResourceArchive(IDisposable __instance) => _lastResourceArchive = __instance;
    private static void AssertArchiveReleased(string path)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert(probe.Length > 0, "The input archive is immediately available for exclusive access after resource loading, including failure.");
    }
    private static bool ResourceCopyBoundary()
    {
        if (_resourceCopyFailure) throw new IOException("Isolated resource copy failure.");
        return true;
    }
    private static void ResourceForeignPostfix() { }
}

public sealed class ResourceAssemblyFixtureBuilder : MarshalByRefObject
{
    public void Build(string directory, string name)
    {
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name) { Version = new Version(1, 0, 0, 0) },
            AssemblyBuilderAccess.Save, directory);
        ModuleBuilder module = assembly.DefineDynamicModule(name, name + ".dll");
        TypeBuilder type = module.DefineType("ResourceFixture", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder read = type.DefineMethod("Read", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        ILGenerator body = read.GetILGenerator(); body.Emit(OpCodes.Ldc_I4, 42); body.Emit(OpCodes.Ret);
        type.CreateType(); assembly.Save(name + ".dll");
    }
}
