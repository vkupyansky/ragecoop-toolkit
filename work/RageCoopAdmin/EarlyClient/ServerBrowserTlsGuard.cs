using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Core;

namespace RageCoop.EarlyClient;

// Harmony initializes the declaring type of a patched static method. Wait for
// the existing opening handler: its menu has then completed normal startup,
// and its worker has not started the download yet.
internal static class ServerBrowserTlsGuard
{
    private const string Owner = "friendslegacy.ragecoop.earlytls";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Browser = typeof(Settings).Assembly.GetType("RageCoop.Client.Menus.ServersMenu", true)!;
    private static readonly Type Closure = Browser.GetNestedType("<>c", BindingFlags.NonPublic)!;
    private static readonly Type Main = typeof(Settings).Assembly.GetType("RageCoop.Client.Main", true)!;
    private static readonly FieldInfo Menu = Browser.GetField("Menu", Static)!;
    private static readonly FieldInfo ResultItem = Browser.GetField("ResultItem", Static)!;
    private static readonly MethodInfo Setter = typeof(ServicePointManager).GetProperty(nameof(ServicePointManager.ServerCertificateValidationCallback))!.GetSetMethod()!;
    private static readonly MethodInfo ThreadStart = typeof(Thread).GetMethod(nameof(Thread.Start), Type.EmptyTypes)!;
    private static readonly MethodInfo Queue = Main.GetMethod("QueueAction", Static, null, new[] { typeof(Action) }, null)!;
    private static readonly MethodInfo MasterServer = typeof(Settings).GetProperty(nameof(Settings.MasterServer))!.GetGetMethod()!;
    private static readonly MethodInfo Deserialize = PatchProcessor.GetOriginalInstructions(ListTarget).Select(code => code.operand).OfType<MethodInfo>()
        .Single(method => method.DeclaringType?.FullName == "Newtonsoft.Json.JsonConvert" && method.Name == "DeserializeObject");
    private static readonly MethodInfo Notify = PatchProcessor.GetOriginalInstructions(ListTarget).Select(code => code.operand).OfType<MethodInfo>()
        .Single(method => method.DeclaringType?.FullName == "GTA.UI.Notification" && method.Name == "Show");
    private static readonly ConditionalWeakTable<Thread, Request> Requests = new ConditionalWeakTable<Thread, Request>();
    private static readonly object WorkerGate = new object();
    private static Thread? _active;
    private static Thread? _pending;
    [ThreadStatic] private static Request? _request;
    private static Func<string, List<ServerInfo>?>? _deserialize;
    private static Harmony? _harmony;
    private static bool _downloadPatched;

    internal static MethodInfo Target => Browser.GetMethod("DownloadString", Static)!;
    internal static MethodInfo ListTarget => Browser.GetMethod("GetAllServers", Static)!;
    internal static MethodInfo OpeningTarget => Closure.GetMethod("<.cctor>b__3_0", Instance)!;

    internal static bool HasSupportedContract() => Browser.Assembly.GetName().Name == "RageCoop.Client" &&
        Browser.Assembly.GetName().Version == new Version(1, 5, 12, 15) &&
        Target.IsStatic && Target.ReturnType == typeof(string) &&
        Target.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string) }) &&
        ListTarget.IsStatic && ListTarget.ReturnType == typeof(void) && ListTarget.GetParameters().Length == 0 &&
        !OpeningTarget.IsStatic && OpeningTarget.ReturnType == typeof(void) &&
        OpeningTarget.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(object), typeof(CancelEventArgs) }) &&
        Matches(Target, "690d4d48e400d88ddc643fb841b83664f22c86ba7618df12ee9752b4c714d829") &&
        Matches(ListTarget, "8b6fe3b85696765cf9abd1e915be647be1a44d574b218ad6841c0337cb660b7d") &&
        Matches(OpeningTarget, "dd581d550d45b09d2851491405dc64df3f8d2d4ce71f842696fc2a5951970ff4") &&
        Matches(Closure.TypeInitializer!, "edfbeb0890d31193a7fc44792e879528fa7543f7053c2b42892d83d91ea59a3b") &&
        Menu.FieldType.GetProperty("Visible")?.PropertyType == typeof(bool) &&
        ResultItem.FieldType.GetProperty("Title")?.PropertyType == typeof(string) &&
        ResultItem.FieldType.GetProperty("Description")?.PropertyType == typeof(string) &&
        Deserialize.ReturnType == typeof(List<ServerInfo>) && Deserialize.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string) }) &&
        PatchProcessor.GetOriginalInstructions(OpeningTarget).Count(code => code.Calls(ThreadStart)) == 1 &&
        PatchProcessor.GetOriginalInstructions(Target).Count(code => code.Calls(Setter)) == 1 &&
        PatchProcessor.GetOriginalInstructions(Target).Count(code => code.Calls(Queue)) == 1 &&
        PatchProcessor.GetOriginalInstructions(ListTarget).Count(code => code.Calls(Queue)) == 1;

    internal static void Install()
    {
        if (_harmony != null) return;
        if (!HasSupportedContract()) throw new InvalidOperationException("Unsupported RageCoop server-browser contract.");
        var harmony = new Harmony(Owner);
        try
        {
            harmony.Patch(OpeningTarget,
                prefix: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(BeforeOpening)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(CaptureOpeningRequest)));
        }
        catch
        {
            harmony.Unpatch(OpeningTarget, HarmonyPatchType.All, Owner);
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
        {
            if (_downloadPatched)
            {
                _harmony.Unpatch(Target, HarmonyPatchType.All, Owner);
                _harmony.Unpatch(ListTarget, HarmonyPatchType.All, Owner);
            }
            _harmony.Unpatch(OpeningTarget, HarmonyPatchType.All, Owner);
        }
        _downloadPatched = false;
        _harmony = null;
        lock (WorkerGate)
        {
            if (_pending != null) Requests.Remove(_pending);
            _pending = null;
        }
    }

    private static void BeforeOpening()
    {
        if (_downloadPatched) return;
        Harmony harmony = _harmony ?? throw new InvalidOperationException("The server-browser guard is unavailable.");
        try
        {
            _deserialize = (Func<string, List<ServerInfo>?>)Delegate.CreateDelegate(typeof(Func<string, List<ServerInfo>?>), Deserialize);
            harmony.Patch(Target, transpiler: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(PreserveCertificateValidation)));
            harmony.Patch(ListTarget,
                prefix: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(BeginWorker)),
                transpiler: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(ProtectServerList)),
                finalizer: new HarmonyMethod(typeof(ServerBrowserTlsGuard), nameof(EndWorker)));
        }
        catch
        {
            harmony.Unpatch(Target, HarmonyPatchType.All, Owner);
            harmony.Unpatch(ListTarget, HarmonyPatchType.All, Owner);
            throw;
        }
        _downloadPatched = true;
    }

    private static void StartRequest(Thread thread)
    {
        // Capture on Opening's thread, before Start: a delayed old worker must
        // never acquire a row or URL belonging to a later close/reopen.
        var settings = (Settings)Main.GetField("Settings", Static)!.GetValue(null)!;
        var request = new Request(_harmony!, Menu.GetValue(null)!, ResultItem.GetValue(null)!, settings.MasterServer);
        Requests.Add(thread, request);
        bool start;
        lock (WorkerGate)
        {
            // Retain only the newest unopened request while the installed
            // WebClient is busy; rapid reopen must not create more workers.
            if (_pending != null) Requests.Remove(_pending);
            _pending = null;
            start = _active == null;
            if (start) _active = thread;
            else _pending = thread;
        }
        if (start) StartWorker(thread);
    }

    private static void BeginWorker()
    {
        Requests.TryGetValue(Thread.CurrentThread, out _request);
    }

    private static Exception? EndWorker(Exception? __exception)
    {
        Requests.Remove(Thread.CurrentThread);
        _request = null;
        bool pending;
        lock (WorkerGate)
        {
            if (ReferenceEquals(_active, Thread.CurrentThread)) _active = null;
            pending = _pending != null;
        }
        // Promotion runs in the main queue so visibility and current row are
        // checked on the same thread that opens and closes the menu.
        if (pending) Queue.Invoke(null, new object[] { (Action)StartPending });
        return __exception;
    }

    private static void StartPending()
    {
        Thread? thread;
        lock (WorkerGate)
        {
            if (_active != null) return;
            thread = _pending;
            _pending = null;
            if (thread == null) return;
            if (!Requests.TryGetValue(thread, out Request request) || !IsCurrent(request))
            {
                Requests.Remove(thread);
                return;
            }
            _active = thread;
        }
        StartWorker(thread);
    }

    private static void StartWorker(Thread thread)
    {
        try { thread.Start(); }
        catch
        {
            Requests.Remove(thread);
            lock (WorkerGate) { if (ReferenceEquals(_active, thread)) _active = null; }
            throw;
        }
    }

    private static string RequestAddress(Settings settings) => _request?.Address ?? settings.MasterServer;

    private static List<ServerInfo>? ReadServerList(string json)
    {
        // The original downloader already queued a failure; do not replace it
        // with the original generic null-list result after its empty return.
        if (_request?.Failed == true) return null;
        try
        {
            List<ServerInfo>? result = _deserialize!(json);
            if (result == null || result.Any(server => server == null)) QueueFailure("Сервер вернул неверный список.");
            return result;
        }
        catch (Exception error) when (IsJsonFailure(error))
        {
            QueueFailure("Сервер вернул повреждённый список.");
            return null;
        }
    }

    private static bool IsJsonFailure(Exception error)
    {
        for (Type? type = error.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "Newtonsoft.Json.JsonException") return true;
        return false;
    }

    private static void QueueDownloadFailure(Action original) => QueueFailure("Не удалось загрузить список серверов. Попробуйте открыть его ещё раз.");

    private static void QueueFailure(string description)
    {
        Request? request = _request;
        if (request == null) return;
        request.Failed = true;
        QueueCurrent(request, () =>
        {
            ResultItem.FieldType.GetProperty("Title")!.SetValue(request.Item, "Список серверов недоступен");
            ResultItem.FieldType.GetProperty("Description")!.SetValue(request.Item, description);
        });
    }

    private static void QueueResults(Action action)
    {
        Request? request = _request;
        if (request != null && !request.Failed) QueueCurrent(request, action);
    }

    private static int QueueFetching(string text, bool blinking)
    {
        Request? request = _request;
        if (request != null) QueueCurrent(request, () => Notify.Invoke(null, new object[] { "Загрузка списка серверов…", blinking }));
        return 0;
    }

    private static void QueueCurrent(Request request, Action action) => Queue.Invoke(null, new object[] { (Action)(() =>
    {
        if (IsCurrent(request)) action();
    }) });

    private static bool IsCurrent(Request request) => ReferenceEquals(request.Installation, _harmony) &&
        ReferenceEquals(request.Menu, Menu.GetValue(null)) && ReferenceEquals(request.Item, ResultItem.GetValue(null)) &&
        (bool)Menu.FieldType.GetProperty("Visible")!.GetValue(request.Menu)!;

    internal static IEnumerable<CodeInstruction> CaptureOpeningRequest(IEnumerable<CodeInstruction> original) =>
        ReplaceCalls(original, new[] { (ThreadStart, nameof(StartRequest)) });

    internal static IEnumerable<CodeInstruction> ProtectServerList(IEnumerable<CodeInstruction> original) =>
        ReplaceCalls(original, new[] { (Deserialize, nameof(ReadServerList)), (Queue, nameof(QueueResults)),
            (MasterServer, nameof(RequestAddress)), (Notify, nameof(QueueFetching)) });

    internal static IEnumerable<CodeInstruction> PreserveCertificateValidation(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        if (codes.Count(code => code.Calls(Setter)) != 1)
            throw new InvalidOperationException("The server-browser certificate callback assignment changed.");
        foreach (CodeInstruction code in codes)
        {
            // Consume exactly the delegate that the installed setter consumed.
            // Retain labels, exception regions, protocols and headers.
            if (code.Calls(Setter)) { code.opcode = OpCodes.Pop; code.operand = null; }
        }
        return ReplaceCalls(codes, new[] { (Queue, nameof(QueueDownloadFailure)) });
    }

    private static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> original, (MethodInfo Target, string Replacement)[] replacements)
    {
        List<CodeInstruction> codes = original.Select(code => new CodeInstruction(code)).ToList();
        foreach (var replacement in replacements)
        {
            if (codes.Count(code => code.Calls(replacement.Target)) != 1)
                throw new InvalidOperationException("The server-browser call contract changed: " + replacement.Target.Name);
            CodeInstruction code = codes.Single(candidate => candidate.Calls(replacement.Target));
            code.opcode = OpCodes.Call;
            code.operand = typeof(ServerBrowserTlsGuard).GetMethod(replacement.Replacement, Static)!;
        }
        return codes;
    }

    private static bool Matches(MethodBase method, string expected)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return false;
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
    }

    private sealed class Request
    {
        internal Request(Harmony installation, object menu, object item, string address)
        { Installation = installation; Menu = menu; Item = item; Address = address; }
        internal Harmony Installation { get; }
        internal object Menu { get; }
        internal object Item { get; }
        internal string Address { get; }
        internal bool Failed { get; set; }
    }
}
