using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoop.EarlyClient;

// The installed loader extracts every root DLL, including native ones, to a
// random temporary file. Keep its archive/script flow and clean only those
// exact files; byte loading prevents Windows from pinning managed files.
internal static class ResourceAssemblyLoadGuard
{
    private const string Owner = "friendslegacy.ragecoop.earlyresources";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Resources = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Scripting.Resources", true)!;
    private static readonly MethodInfo TemporaryFile = typeof(Path).GetMethod(nameof(Path.GetTempFileName), Type.EmptyTypes)!;
    private static readonly MethodInfo OpenWrite = typeof(File).GetMethod(nameof(File.OpenWrite), new[] { typeof(string) })!;
    private static readonly MethodInfo LoadFrom = typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), new[] { typeof(string) })!;
    private static Harmony? _harmony;
    [ThreadStatic] private static LoadScope? _current;

    internal static MethodInfo Target => Resources.GetMethod("LoadResource", Instance)!;

    internal static bool HasSupportedContract()
    {
        MethodInfo target = Target;
        MethodInfo caller = Resources.GetMethod("Load", Instance)!;
        return Resources.Assembly.GetName().Version == new Version(1, 5, 12, 15) &&
            !target.IsStatic && target.ReturnType == typeof(void) &&
            target.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(new[] { "ICSharpCode.SharpZipLib.Zip.ZipFile", "System.String" }) &&
            typeof(IDisposable).IsAssignableFrom(target.GetParameters()[0].ParameterType) &&
            !caller.IsStatic && caller.ReturnType == typeof(void) &&
            caller.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string), typeof(string[]) }) &&
            MatchesBody(caller, "a79dc6f2c097277e117c7c9fd40f5d51fef1ebbaefa4677f1e389a754b807677") &&
            MatchesBody(target, "9828fb9700579488d3dc326297626555bdd1a2c83d2e17abb692e271c314ace9") &&
            MatchesCalls(PatchProcessor.GetOriginalInstructions(target));
    }

    private static bool MatchesBody(MethodInfo method, string expected)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        using SHA256 hash = SHA256.Create();
        return body != null && BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
    }

    internal static void Install()
    {
        if (_harmony != null) return;
        if (!HasSupportedContract()) throw new InvalidOperationException("Unsupported RageCoop resource assembly loader contract.");
        var harmony = new Harmony(Owner);
        try
        {
            harmony.Patch(Target, prefix: new HarmonyMethod(typeof(ResourceAssemblyLoadGuard), nameof(BeginLoad)),
                transpiler: new HarmonyMethod(typeof(ResourceAssemblyLoadGuard), nameof(ProtectTemporaryAssemblies)),
                finalizer: new HarmonyMethod(typeof(ResourceAssemblyLoadGuard), nameof(EndLoad)));
        }
        catch
        {
            harmony.Unpatch(Target, HarmonyPatchType.All, Owner);
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        _harmony?.Unpatch(Target, HarmonyPatchType.All, Owner);
        _harmony = null;
    }

    internal static IEnumerable<CodeInstruction> ProtectTemporaryAssemblies(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        if (!MatchesCalls(codes)) throw new InvalidOperationException("The resource temporary-assembly calls changed.");
        foreach (CodeInstruction code in codes)
        {
            string? replacement = code.Calls(TemporaryFile) ? nameof(CreateTemporaryFile) :
                code.Calls(OpenWrite) ? nameof(OpenTemporaryFile) : code.Calls(LoadFrom) ? nameof(LoadTemporaryAssembly) : null;
            if (replacement != null)
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(ResourceAssemblyLoadGuard).GetMethod(replacement, Static)!;
            }
            yield return code;
        }
    }

    private static bool MatchesCalls(IReadOnlyCollection<CodeInstruction> codes) =>
        codes.Count(code => code.Calls(TemporaryFile)) == 1 && codes.Count(code => code.Calls(OpenWrite)) == 1 &&
        codes.Count(code => code.Calls(LoadFrom)) == 1;

    private static void BeginLoad(IDisposable __0, out LoadScope __state)
    {
        // Resources.Load creates this archive for this invocation and the
        // original LoadResource closes it only after a successful load.
        __state = new LoadScope(_current, __0);
        _current = __state;
    }

    private static string CreateTemporaryFile()
    {
        LoadScope scope = _current ?? throw new InvalidOperationException("No resource assembly load is active.");
        string path = Path.GetTempFileName();
        scope.Files.Add(path);
        return path;
    }

    private static LoadScope ScopeFor(string path)
    {
        LoadScope scope = _current ?? throw new InvalidOperationException("No resource assembly load is active.");
        if (!scope.Files.Contains(path)) throw new InvalidOperationException("The temporary file belongs to another resource load.");
        return scope;
    }

    private static FileStream OpenTemporaryFile(string path)
    {
        LoadScope scope = ScopeFor(path);
        FileStream stream = File.OpenWrite(path);
        // The original closes this after CopyTo. Retain it until finalization
        // as well, so a failed archive read cannot leave an open Windows file.
        scope.Writers.Add(stream);
        return stream;
    }

    private static Assembly LoadTemporaryAssembly(string path)
    {
        ScopeFor(path);
        AssemblyName identity = AssemblyName.GetAssemblyName(path);
        // LoadFrom reuses an existing full assembly identity. Preserve that
        // behavior, particularly the host's already loaded Harmony instance.
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.FullName, identity.FullName, StringComparison.OrdinalIgnoreCase));
        return loaded ?? Assembly.Load(File.ReadAllBytes(path));
    }

    private static Exception? EndLoad(Exception? __exception, LoadScope? __state)
    {
        if (__state == null) return __exception;
        var failures = new List<Exception>();
        try
        {
            foreach (FileStream writer in __state.Writers)
            {
                try { writer.Dispose(); }
                catch (IOException error) { failures.Add(error); }
            }
            try { __state.Archive.Dispose(); }
            catch (IOException error) { failures.Add(error); }
            foreach (string path in __state.Files)
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { failures.Add(error); }
            }
        }
        finally { _current = __state.Previous; }
        if (failures.Count == 0) return __exception;
        if (__exception != null) failures.Insert(0, __exception);
        return new AggregateException("Resource load cleanup failed.", failures);
    }

    private sealed class LoadScope
    {
        internal readonly LoadScope? Previous;
        internal readonly IDisposable Archive;
        internal readonly List<string> Files = new List<string>();
        internal readonly List<FileStream> Writers = new List<FileStream>();
        internal LoadScope(LoadScope? previous, IDisposable archive)
        {
            Previous = previous;
            Archive = archive;
        }
    }
}
