using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using GTA;
using HarmonyLib;
using RageCoop.EarlyClient;

internal static class Program
{
    private static int Main()
    {
        string isolated = Path.Combine(Path.GetTempPath(), "CoopEarlyClient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        AppDomain? domain = null;
        try
        {
            // Keep the server-only SHVDN stub outside this domain's probing
            // path. Neither that incomplete host nor the native ASI is run.
            domain = AppDomain.CreateDomain("EarlyClientComposition", null,
                new AppDomainSetup { ApplicationBase = isolated });
            var boundary = (EarlyClientHostBoundary)domain.CreateInstanceFromAndUnwrap(
                Assembly.GetExecutingAssembly().Location, typeof(EarlyClientHostBoundary).FullName);
            return boundary.Run(AppDomain.CurrentDomain.BaseDirectory);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (domain != null) AppDomain.Unload(domain);
            Directory.Delete(isolated);
        }
    }
}

public sealed class EarlyClientHostBoundary : MarshalByRefObject
{
    public int Run(string binaries)
    {
        ResolveEventHandler resolver = (_, args) =>
        {
            var name = new AssemblyName(args.Name);
            Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.FullName == args.Name);
            if (loaded != null) return loaded;
            // LemonUI was built against weak-named SDK 3.6. The supported
            // installed facade is 3.7; normal host binding reuses that facade.
            // Reproduce only this checked mapping in the byte-loaded domain.
            if (name.Name == "ScriptHookVDotNet3" && name.Version == new Version(3, 6, 0, 0) &&
                name.GetPublicKeyToken().Length == 0)
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly =>
                    assembly.GetName().Name == name.Name && assembly.GetName().Version == new Version(3, 7, 0, 188) &&
                    assembly.GetName().GetPublicKeyToken().Length == 0);
                if (loaded != null) return loaded;
            }
            if (name.Name == "ScriptHookVDotNet")
                throw new FileLoadException("The early-client check must not bind to the native ASI or server-only stub.", args.Name);
            string path = Path.Combine(binaries, name.Name + ".dll");
            return File.Exists(path) ? Assembly.Load(File.ReadAllBytes(path)) : null;
        };
        ResolveEventHandler reflectionResolver = (_, args) => Assembly.ReflectionOnlyLoad(args.Name);
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += reflectionResolver;
        try
        {
            DefineHostBoundary(Path.Combine(binaries, "ScriptHookVDotNet.dll"));
            // A byte-loaded copy has no codebase beside the incomplete server
            // stub. Its actual DLL references resolve only inside this domain.
            Assembly check = Assembly.Load(File.ReadAllBytes(GetType().Assembly.Location));
            return (int)check.GetType("EarlyClientLifecycle", true)!.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { binaries })!;
        }
        finally
        {
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= reflectionResolver;
            AppDomain.CurrentDomain.AssemblyResolve -= resolver;
        }
    }

    private static void DefineHostBoundary(string stub)
    {
        // Retain the SHVDN type declarations needed by the real GTA facade,
        // then declare only the host members referenced by the two patched
        // members. Every synthetic host method throws if unexpectedly called.
        Assembly declarations = Assembly.ReflectionOnlyLoad(File.ReadAllBytes(stub));
        ModuleBuilder module = AppDomain.CurrentDomain.DefineDynamicAssembly(declarations.GetName(), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("EarlyClientManagedHostBoundary");
        var types = new Dictionary<string, TypeBuilder>();
        foreach (Type source in declarations.GetTypes().OrderBy(type => type.FullName!.Count(character => character == '+')))
        {
            Type? parent = source.IsValueType ? (source.IsEnum ? typeof(Enum) : typeof(ValueType)) :
                source.IsInterface ? null : source.FullName == "SHVDN.ScriptDomain" ? typeof(MarshalByRefObject) : typeof(object);
            TypeBuilder type = source.DeclaringType == null ? module.DefineType(source.FullName!, source.Attributes, parent) :
                types[source.DeclaringType.FullName!].DefineNestedType(source.Name, source.Attributes, parent);
            types.Add(source.FullName!, type);
            foreach (FieldInfo field in source.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // The installed server stub contains only primitive fields;
                // preserve their layout rather than inventing game behavior.
                Type fieldType = field.FieldType;
                if (!fieldType.IsPrimitive) throw new InvalidDataException("Unexpected SHVDN declaration field: " + field.Name);
                type.DefineField(field.Name, fieldType, field.Attributes);
            }
        }
        TypeBuilder script = types["SHVDN.Script"], domain = types["SHVDN.ScriptDomain"];
        DefineThrowingMethod(script, "get_Name", typeof(string), Type.EmptyTypes, false);
        DefineThrowingMethod(script, "add_Aborted", typeof(void), new[] { typeof(EventHandler) }, false);
        DefineThrowingMethod(domain, "get_CurrentDomain", domain, Type.EmptyTypes, true);
        DefineThrowingMethod(domain, "LookupScript", script, new[] { typeof(object) }, false);
        DefineThrowingMethod(domain, "LookupScriptFilename", typeof(string), new[] { typeof(Type) }, false);
        foreach (TypeBuilder type in types.Values.Reverse()) type.CreateType();
    }

    private static void DefineThrowingMethod(TypeBuilder type, string name, Type returns, Type[] parameters, bool isStatic)
    {
        MethodBuilder method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.HideBySig |
            (isStatic ? MethodAttributes.Static : 0), returns, parameters);
        ILGenerator body = method.GetILGenerator();
        body.Emit(OpCodes.Ldstr, "Unexpected native-host execution at " + type.FullName + "." + name);
        body.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
        body.Emit(OpCodes.Throw);
    }
}

internal static partial class EarlyClientLifecycle
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string EarlyOwner = "friendslegacy.ragecoop.earlyingress";
    private const string ResourceOwner = "friendslegacy.ragecoop.engineingress";
    private static Assembly Resource = null!;
    private static readonly Guard Early = new Guard(typeof(EarlyClientScript).Assembly, EarlyOwner);
    private static Guard Downloaded = null!;
    private static readonly Dictionary<Script, EventHandler> Aborted = new Dictionary<Script, EventHandler>();
    private static int _baseConstructors;
    private static int _hostEvents;

    internal static int Run(string binaries)
    {
        Resource = Assembly.Load(File.ReadAllBytes(Path.Combine(binaries, "RageCoopAdmin.Client.dll")));
        Downloaded = new Guard(Resource, ResourceOwner);
        var boundary = new Harmony("friendslegacy.check.earlyclient.hostboundary");
        var foreign = new Harmony("friendslegacy.check.earlyclient.foreign");
        try
        {
            CheckAssemblyContract();

            // The installed managed SHVDN facade forwards these two members to
            // its native ScriptDomain. Only that host boundary is substituted;
            // the actual derived constructor and both ingress copies run here.
            boundary.Patch(typeof(Script).GetConstructor(Type.EmptyTypes)!,
                prefix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(BeforeBaseConstructor)));
            boundary.Patch(typeof(Script).GetEvent(nameof(Script.Aborted))!.GetAddMethod()!,
                prefix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(CaptureAbortHandler)));
            foreign.Patch(Early.Message, postfix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(ForeignPostfix)));
            MethodBase[] hostTargets = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray();
            Assert(hostTargets.Length == 2 && hostTargets.Contains(typeof(Script).GetConstructor(Type.EmptyTypes)!) &&
                hostTargets.Contains(typeof(Script).GetEvent(nameof(Script.Aborted))!.GetAddMethod()!),
                "Only the GTA.Script constructor and Aborted subscription are replaced at the isolated host boundary.");

            AssertOwners(false, false, foreign.Id);
            EarlyClientScript first = Construct();
            Assert(_hostEvents == 0, "The actual early constructor installs both ingress patches before the synthetic host delivers any Tick or connection event.");
            CheckClientSettings(binaries);
            CheckResourceAssemblyLoad(binaries);
            CheckServerBrowserTls(binaries);
            CheckLoggerFileGuard();
            DispatchHostEvent();

            Downloaded.Install();
            Downloaded.Install();
            AssertOwners(true, true, foreign.Id);
            Downloaded.Uninstall();
            Downloaded.Uninstall();
            AssertOwners(true, false, foreign.Id);
            Assert(true, "The actual downloaded resource can install, unload and repeat cleanup while the early ingress copy remains active.");

            Downloaded.Install();
            Abort(first);
            CheckEarlyLocalGuardOwners(false);
            AssertOwners(false, true, foreign.Id);
            Assert(true, "The actual early Aborted handler removes only its owner and preserves downloaded-resource and foreign patches.");
            Downloaded.Uninstall();
            AssertOwners(false, false, foreign.Id);

            _hostEvents = 0;
            EarlyClientScript reloaded = Construct();
            Assert(_baseConstructors == 2, "A script reload runs the actual early constructor again through the isolated host boundary.");
            AssertOwners(true, false, foreign.Id);
            DispatchHostEvent();
            Abort(reloaded);
            CheckEarlyLocalGuardOwners(false);
            AssertOwners(false, false, foreign.Id);
            Assert(Aborted.Count == 0, "The synthetic host releases each captured abort subscription after delivery.");

            foreach (Assembly assembly in new[] { typeof(EarlyClientScript).Assembly, Resource, Early.Message.DeclaringType!.Assembly, typeof(Script).Assembly })
                PrintHash(Path.Combine(binaries, assembly.GetName().Name + ".dll"));
            Console.WriteLine("Actual .NET Framework early-client lifecycle passed: constructor timing, independent patch ownership, resource unload, abort and reload. Native ASI startup, GTA and live networking were not executed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Downloaded.Uninstall();
            Early.Uninstall();
            UninstallEarlyLocalGuards();
            Aborted.Clear();
            RemoveOwner(foreign);
            RemoveOwner(boundary);
        }
    }

    private static void CheckAssemblyContract()
    {
        Type type = typeof(EarlyClientScript);
        Assembly core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "ScriptHookVDotNet");
        Assert(core.IsDynamic && Environment.Version.Major == 4,
            "The check runs on .NET Framework with a synthetic managed core, without loading the native ASI or running the server stub.");
        ScriptAttributes attributes = type.GetCustomAttribute<ScriptAttributes>()!;
        Assert(type.IsPublic && !type.IsAbstract && type.BaseType == typeof(Script) && type.GetConstructor(Type.EmptyTypes) != null &&
            attributes != null && attributes.NoScriptThread && !attributes.NoDefaultInstance,
            "SHVDN can discover the public default early script with NoScriptThread enabled.");
        AssemblyName harmony = typeof(Harmony).Assembly.GetName();
        foreach (Assembly assembly in new[] { type.Assembly, Resource })
        {
            Assert(assembly.GetReferencedAssemblies().Any(name => name.Name == harmony.Name && name.Version == harmony.Version),
                assembly.GetName().Name + " uses the exact Harmony assembly identity loaded by this .NET Framework host.");
        }
        Assert(Early.Type != Downloaded.Type && Early.Type.FullName == Downloaded.Type.FullName && Early.Message == Downloaded.Message && Early.Download == Downloaded.Download,
            "The early and downloaded ingress guards are separate actual assemblies targeting the same installed client methods.");
        ConstructorInfo constructor = type.GetConstructor(Type.EmptyTypes)!;
        List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(constructor);
        MethodInfo loggerInstall = type.Assembly.GetType("RageCoop.EarlyClient.LoggerFileGuard", true)!.GetMethod("Install", Static)!;
        Assert(body.Count(code => code.Calls(Early.Method("Install"))) == 1 &&
            body.Count(code => code.Calls(loggerInstall)) == 1 &&
            body.Count(code => code.Calls(typeof(Script).GetEvent(nameof(Script.Aborted))!.GetAddMethod()!)) == 1 &&
            !body.Any(code => code.operand is MethodInfo method && method.DeclaringType == typeof(Script) &&
                method.Name is "add_Tick" or "add_KeyDown" or "add_KeyUp"),
            "The actual early constructor installs ingress and logger protection directly and registers only cleanup, without waiting for an input or Tick handler.");
    }

    private static EarlyClientScript Construct()
    {
        int subscriptions = Aborted.Count;
        var script = new EarlyClientScript();
        Assert(Aborted.Count == subscriptions + 1 && Aborted.ContainsKey(script),
            "The real early constructor subscribes one Aborted handler through the SHVDN facade.");
        return script;
    }

    private static bool BeforeBaseConstructor()
    {
        _baseConstructors++;
        return false;
    }

    private static bool CaptureAbortHandler(Script __instance, EventHandler __0)
    {
        Assert(_hostEvents == 0, "No host event has run while the constructor registers cleanup.");
        CheckOwner(Early, true);
        CheckEarlyLocalGuardOwners(true);
        Assert(!Aborted.ContainsKey(__instance), "The constructor does not register duplicate abort handlers.");
        Aborted.Add(__instance, __0);
        return false;
    }

    private static void DispatchHostEvent()
    {
        CheckOwner(Early, true);
        _hostEvents++;
    }

    private static void Abort(Script script)
    {
        EventHandler handler = Aborted[script];
        Aborted.Remove(script);
        handler(script, EventArgs.Empty);
    }

    private static void AssertOwners(bool early, bool downloaded, string foreign)
    {
        CheckOwner(Early, early);
        CheckOwner(Downloaded, downloaded);
        Assert(Harmony.GetPatchInfo(Early.Message)?.Postfixes.Count(patch => patch.owner == foreign) == 1,
            "Lifecycle operations preserve the other component's patch on the same ingress target.");
    }

    private static void CheckOwner(Guard guard, bool installed)
    {
        foreach (MethodInfo target in new[] { guard.Message, guard.Download })
        {
            Patch[] prefixes = (Harmony.GetPatchInfo(target)?.Prefixes ?? Enumerable.Empty<Patch>())
                .Where(patch => patch.owner == guard.Owner).ToArray();
            Assert(prefixes.Length == (installed ? 1 : 0) && prefixes.All(patch => patch.PatchMethod.DeclaringType == guard.Type),
                guard.Owner + " has the expected actual-DLL prefix on " + target.Name + ".");
        }
        MethodBase[] targets = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(guard.Owner) == true).ToArray();
        Assert(targets.Length == (installed ? 2 : 0) && targets.All(method => method == guard.Message || method == guard.Download),
            guard.Owner + " owns exactly its two incoming-message/download targets when installed.");
    }

    private static void ForeignPostfix() { }

    private static void RemoveOwner(Harmony harmony)
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) == true).ToArray())
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
    }

    private static void PrintHash(string path)
    {
        using SHA256 hash = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        Console.WriteLine(Path.GetFileNameWithoutExtension(path) + " SHA256: " + BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Guard
    {
        internal readonly Type Type;
        internal readonly string Owner;
        internal MethodInfo Message => (MethodInfo)Method("FindMessageTarget").Invoke(null, null)!;
        internal MethodInfo Download => (MethodInfo)Method("FindDownloadTarget").Invoke(null, null)!;

        internal Guard(Assembly assembly, string owner)
        {
            Type = assembly.GetType("RageCoopAdmin.Client.EngineIngressGuard", true)!;
            Owner = owner;
        }

        internal MethodInfo Method(string name) => Type.GetMethod(name, Static)!;
        internal void Install()
        {
            MethodInfo install = Method("Install");
            object[] defaults = install.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
            Assert(defaults.Length == 1 && Equals(defaults[0], Owner), "The downloaded resource retains its default ingress owner.");
            install.Invoke(null, defaults);
        }
        internal void Uninstall() => Method("Uninstall").Invoke(null, null);
    }
}
