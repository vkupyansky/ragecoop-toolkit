using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using RageCoop.Core;
using RageCoop.Server;
using RageCoop.Server.Scripting;

internal static class Program
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 0) { return RunBootstrappedChild(); }
        if (arguments.Length != 1 || arguments[0] != "--resource-child")
        {
            Console.Error.WriteLine("Unsupported resource-check arguments.");
            return 2;
        }
        return CheckResource();
    }

    private static int RunBootstrappedChild()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), "ragecoop-host-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            string runtimeConfig = Path.Combine(runtimeDirectory, "RageCoop.Server.runtimeconfig.json");
            // Preserve all actual host switches: SDK-generated test defaults can mask compatibility failures.
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Host", "RageCoop.Server.runtimeconfig.json"), runtimeConfig);
            return RunBootstrappedChild(runtimeConfig);
        }
        finally
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }

    private static int RunBootstrappedChild(string runtimeConfig)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The .NET host path is unavailable."))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(runtimeConfig);
        start.ArgumentList.Add("--roll-forward");
        start.ArgumentList.Add("Major");
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--resource-child");
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "Bootstrap", "CoopAdmin.Startup.dll");
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated resource check.");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(15000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("The actual resource-loader check did not finish.");
        }
        Console.Write(output.GetAwaiter().GetResult());
        Console.Error.Write(errors.GetAwaiter().GetResult());
        return child.ExitCode;
    }

    private static int CheckResource()
    {
        object? resource = null;
        ServerScript? script = null;
        Logger? logger = null;
        string data = Path.Combine(Path.GetTempPath(), "ragecoop-resource-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (AppContext.TargetFrameworkName != ".NETCoreApp,Version=v6.0")
            {
                throw new InvalidOperationException("The loader check must match the installed host's .NET 6 target metadata.");
            }
            bool hasFormatterSwitch = AppContext.TryGetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", out _);
            if (hasFormatterSwitch)
            {
                throw new InvalidOperationException("The installed host must expose its unchanged BinaryFormatter default; an explicit test switch would mask the regression.");
            }
            Console.WriteLine("Host target: " + AppContext.TargetFrameworkName + "; BinaryFormatter switch: absent; runtime: " + Environment.Version);
            logger = (Logger)Activator.CreateInstance(typeof(Logger), InstanceFlags, null, new object[] { true, false }, null)!;
            logger.UseConsole = true;
            logger.LogLevel = 0;
            Type resourceType = typeof(Server).Assembly.GetType("RageCoop.Server.Scripting.ServerResource", true)!;
            MethodInfo load = resourceType.GetMethod("LoadFrom", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(Logger) }, null)!;
            string resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "CoopAdmin");
            if (File.Exists(Path.Combine(resourcePath, "0Harmony.dll")))
            {
                throw new InvalidOperationException("Harmony must be outside the resource directory scanned by RageCoop.");
            }
            resource = load.Invoke(null, new object[] { resourcePath, data, logger })!;
            var scripts = (IDictionary)resourceType.GetProperty("Scripts")!.GetValue(resource)!;
            if (scripts.Count != 1) { throw new InvalidOperationException("The actual resource loader must instantiate exactly one script."); }
            script = scripts.Values.Cast<ServerScript>().Single();
            Assembly scriptAssembly = script.GetType().Assembly;
            Console.WriteLine("Resource ALC: " + AssemblyLoadContext.GetLoadContext(scriptAssembly)?.Name + "; in-memory: " + (scriptAssembly.Location.Length == 0));
            Assembly[] loadedHarmony = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "0Harmony").ToArray();
            if (loadedHarmony.Length != 1 || AssemblyLoadContext.GetLoadContext(loadedHarmony[0]) != AssemblyLoadContext.Default)
            {
                throw new InvalidOperationException("The runtime startup hook must provide exactly one shared Harmony assembly in Default.");
            }
            Console.WriteLine("Harmony ALC: Default; path: " + loadedHarmony[0].Location);
            if (loadedHarmony[0].GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName != ".NETCoreApp,Version=v10.0")
            {
                throw new InvalidOperationException("The deployed resource must use the .NET 10 Harmony asset to avoid removed BinaryFormatter serialization.");
            }

            var server = (Server)RuntimeHelpers.GetUninitializedObject(typeof(Server));
            FieldInfo handlers = typeof(Server).GetField("RequestHandlers", InstanceFlags)!;
            handlers.SetValue(server, Activator.CreateInstance(handlers.FieldType));
            typeof(Server).GetField("Logger", InstanceFlags)!.SetValue(server, logger);
            var api = (API)Activator.CreateInstance(typeof(API), InstanceFlags, null, new object[] { server }, null)!;
            script.API = api;
            script.OnStart();
            Type fragments = scriptAssembly.GetType("CoopControl.Shared.EngineFragmentGuard", true)!;
            MethodInfo fragmentTarget = (MethodInfo)fragments.GetMethod("FindFragmentTarget", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            Type peerType = typeof(Server).GetField("MainNetServer", InstanceFlags)!.FieldType;
            if (!fragmentTarget.DeclaringType!.IsAssignableFrom(peerType))
                throw new InvalidOperationException("Resource fragment guard targets another Lidgren assembly instead of the actual server peer.");
            Type harmonyApi = loadedHarmony[0].GetType("HarmonyLib.Harmony", true)!;
            MethodInfo getPatchInfo = harmonyApi.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) })!;
            string[] owners = { "friendslegacy.ragecoop.engineserver", "friendslegacy.ragecoop.transportfragments", "friendslegacy.ragecoop.enginecodec", "friendslegacy.ragecoop.enginelogger" };
            MethodBase[] RuntimeTargets() => ((IEnumerable)harmonyApi.GetMethod("GetAllPatchedMethods")!.Invoke(null, null)!).Cast<MethodBase>()
                .Where(method => HasOwner(getPatchInfo.Invoke(null, new object[] { method }), owners)).ToArray();
            if (!HasOwner(getPatchInfo.Invoke(null, new object[] { fragmentTarget }), new[] { owners[1] }) || RuntimeTargets().Length < 22)
                throw new InvalidOperationException("Actual resource start did not install the server, codec, fragment and logger guards.");
            Type guard = scriptAssembly.GetType("CoopAdmin.Server.EngineRelayGuard", true)!;
            if (guard.GetField("_harmony", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) == null)
            {
                throw new InvalidOperationException("OnStart did not install the real engine relay patch.");
            }
            script.OnStop();
            script = null;
            if (RuntimeTargets().Length != 0)
                throw new InvalidOperationException("Actual resource stop retained server/transport/logger hooks in the host.");
            if (guard.GetField("_harmony", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) != null)
            {
                throw new InvalidOperationException("OnStop left the engine relay patch installed.");
            }
            Console.WriteLine("PASS actual in-memory ServerResource loader, real CoopAdmin.OnStart and OnStop");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            script?.OnStop();
            if (resource != null) { resource.GetType().GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(resource, null); }
            logger?.Dispose();
            if (Directory.Exists(data)) { Directory.Delete(data, true); }
        }
    }

    private static bool HasOwner(object? patches, string[] owners) => patches != null &&
        ((IEnumerable)patches.GetType().GetProperty("Owners")!.GetValue(patches)!).Cast<string>().Any(owners.Contains);
}
