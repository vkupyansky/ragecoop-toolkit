using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;

// The runtime requires this exact class name in the global namespace.
internal static class StartupHook
{
    public static void Initialize()
    {
        string directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)
            ?? throw new InvalidOperationException("The CoopAdmin startup hook must be loaded from its installed file.");
        string path = Path.Combine(directory, "0Harmony.dll");
        AssemblyName dependency = AssemblyName.GetAssemblyName(path);
        if (dependency.Name != "0Harmony" || dependency.Version != new Version(2, 4, 3, 0))
        {
            throw new InvalidOperationException("The CoopAdmin startup hook requires the pinned Harmony 2.4.3 assembly beside it.");
        }
        // RageCoop loads resource DLLs into an isolated, in-memory context.
        // Share Harmony from Default so its generated wrappers have one type identity.
        Assembly harmony = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        if (harmony.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName != ".NETCoreApp,Version=v10.0")
        {
            // Older assets default to BinaryFormatter, removed from the installed runtime.
            throw new InvalidOperationException("The CoopAdmin startup hook requires the .NET 10 build of Harmony 2.4.3.");
        }
        InstallEarlyGuards();
    }

    // Resolve Harmony only after loading the checked assembly into Default.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallEarlyGuards() => EarlySettingsGuard.Install();
}
