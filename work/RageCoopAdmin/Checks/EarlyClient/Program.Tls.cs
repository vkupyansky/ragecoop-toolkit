using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GTA;
using HarmonyLib;
using LemonUI.Elements;
using LemonUI.Menus;
using RageCoop.Client;
using RageCoop.EarlyClient;

internal static partial class EarlyClientLifecycle
{
    private const string TlsOwner = "friendslegacy.ragecoop.earlytls";
    private const string BrowserBaseline = "COOP_CHECK_BROWSER_ORIGINAL_JSON";
    private static ManualResetEventSlim? _browserWorkerEntered;
    private static ManualResetEventSlim? _browserWorkerRelease;
    private static int _delayNextBrowserWorker;
    private static int _browserUiThread;
    private static int _browserNotifications;

    private static void CheckServerBrowserTlsOwners(bool installed)
    {
        Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.ServerBrowserTlsGuard", true)!;
        MethodInfo opening = (MethodInfo)guard.GetProperty("OpeningTarget", Static)!.GetValue(null)!;
        MethodInfo download = (MethodInfo)guard.GetProperty("Target", Static)!.GetValue(null)!;
        MethodInfo list = (MethodInfo)guard.GetProperty("ListTarget", Static)!.GetValue(null)!;
        MethodBase[] owned = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(TlsOwner) == true).ToArray();
        Assert(owned.Length == (installed ? (owned.Contains(download) ? 3 : 1) : 0) &&
            owned.All(method => method == opening || method == download || method == list) &&
            (Harmony.GetPatchInfo(opening)?.Prefixes.Count(patch => patch.owner == TlsOwner) ?? 0) == (installed ? 1 : 0) &&
            (Harmony.GetPatchInfo(opening)?.Transpilers.Count(patch => patch.owner == TlsOwner) ?? 0) == (installed ? 1 : 0) &&
            (!owned.Contains(download) || (owned.Contains(list) &&
                Harmony.GetPatchInfo(download)!.Transpilers.Count(patch => patch.owner == TlsOwner) == 1 &&
                Harmony.GetPatchInfo(list)!.Prefixes.Count(patch => patch.owner == TlsOwner) == 1 &&
                Harmony.GetPatchInfo(list)!.Transpilers.Count(patch => patch.owner == TlsOwner) == 1 &&
                Harmony.GetPatchInfo(list)!.Finalizers.Count(patch => patch.owner == TlsOwner) == 1)) &&
            owned.All(method => Harmony.GetPatchInfo(method)!.Prefixes.Concat(Harmony.GetPatchInfo(method)!.Transpilers).Concat(Harmony.GetPatchInfo(method)!.Finalizers)
                .Where(patch => patch.owner == TlsOwner).All(patch => patch.PatchMethod.DeclaringType == guard)),
            "The browser guard owns only Opening prefix/transpiler and, after normal opening, DownloadString transpiler plus GetAllServers prefix/transpiler/finalizer; abort removes all three targets.");
    }

    private static void CheckServerBrowserTls(string binaries)
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.ServerBrowserTlsGuard", true)!;
        MethodInfo target = (MethodInfo)guard.GetProperty("Target", Static)!.GetValue(null)!;
        MethodInfo opening = (MethodInfo)guard.GetProperty("OpeningTarget", Static)!.GetValue(null)!;
        MethodInfo list = (MethodInfo)guard.GetProperty("ListTarget", Static)!.GetValue(null)!;
        Type browser = target.DeclaringType!;
        Type main = typeof(Settings).Assembly.GetType("RageCoop.Client.Main", true)!;
        FieldInfo settings = main.GetField("Settings", Static)!;
        object? priorSettings = settings.GetValue(null);
        MethodInfo clearQueue = main.GetMethod("ClearQueuedActions", Static)!;
        RemoteCertificateValidationCallback? previousCallback = ServicePointManager.ServerCertificateValidationCallback;
        SecurityProtocolType previousProtocol = ServicePointManager.SecurityProtocol;
        bool previousContinue = ServicePointManager.Expect100Continue;
        IWebProxy? previousProxy = WebRequest.DefaultWebProxy;
        var boundary = new Harmony("friendslegacy.check.earlytls.boundary");
        var foreign = new Harmony("friendslegacy.check.earlytls.foreign");
        void Install() => guard.GetMethod("Install", Static)!.Invoke(null, null);
        string Download(string url) => (string)target.Invoke(null, new object[] { url })!;
        try
        {
            Assert((bool)guard.GetMethod("HasSupportedContract", Static)!.Invoke(null, null)!,
                "The browser guard matches the exact installed version, download IL, opening handler and harmless closure initializer.");
            Assert(Harmony.GetPatchInfo(opening)?.Owners.Contains(TlsOwner) == true && Harmony.GetPatchInfo(target)?.Owners.Contains(TlsOwner) != true,
                "The real early constructor patches only the nested opening handler, without initializing the menu or download target.");
            settings.SetValue(null, new Settings());
            WebRequest.DefaultWebProxy = null;
            // Keep actual menu construction and events; only layout, textures,
            // instruction movies and notifications cross into the native host.
            boundary.Patch(typeof(NativeMenu).GetMethod("UpdateItems", instance)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            boundary.Patch(typeof(NativeMenu).GetMethod("Recalculate", instance, null, Type.EmptyTypes, null)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            foreach (Type type in new[] { typeof(ScaledText), typeof(ScaledTexture), typeof(ScaledRectangle) })
                boundary.Patch(type.GetMethod("Recalculate", instance, null, Type.EmptyTypes, null)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            boundary.Patch(typeof(ScaledTexture).GetMethod("Request", instance)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            boundary.Patch(typeof(ScaledText).GetProperty("Width")!.GetGetMethod()!, prefix: TlsBoundary(nameof(TlsTextWidth)));
            Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
            boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(PointF) }, null)!, prefix: TlsBoundary(nameof(TlsLayoutPoint)));
            boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButton).GetConstructor(new[] { typeof(string), typeof(Control) })!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            boundary.Patch(typeof(LemonUI.Scaleform.InstructionalButtons).GetMethod("Add", instance)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            boundary.Patch(typeof(LemonUI.Scaleform.BaseScaleform).GetConstructor(instance, null, new[] { typeof(string) }, null)!, prefix: TlsBoundary(nameof(SkipTlsNative)));
            _browserUiThread = Thread.CurrentThread.ManagedThreadId;
            boundary.Patch(typeof(GTA.UI.Notification).GetMethod("Show", Static, null, new[] { typeof(string), typeof(bool) }, null)!, prefix: TlsBoundary(nameof(CaptureBrowserNotification)));

            if (Environment.GetEnvironmentVariable(BrowserBaseline) == "1") RunOriginalBrowserJsonFailure(guard, browser, settings);
            CheckOriginalBrowserJsonFailure(binaries);

            RemoteCertificateValidationCallback strict = (_, _, _, errors) => errors == SslPolicyErrors.None;
            ServicePointManager.ServerCertificateValidationCallback = strict;
            using (var fixture = new BrowserDownloadFixture())
            {
                Assert(Download(fixture.Url) == BrowserDownloadFixture.Body, "The actual original browser method downloads a loopback fixture with its real WebClient.");
                fixture.Complete();
                Assert(fixture.UserAgent == "Mozilla/5.0 RageCoop/1.5.12.15", "The original browser User-Agent remains observable on the loopback server.");
            }
            RemoteCertificateValidationCallback originalBypass = ServicePointManager.ServerCertificateValidationCallback!;
            Assert(!ReferenceEquals(originalBypass, strict) && originalBypass(null!, null!, null!, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch),
                "The actual unpatched browser reproduces the global callback overwrite and acceptance of certificate errors.");

            // The actual Visible setter raises the subscribed Opening event,
            // creates its original Thread and makes the menu visible normally.
            ServicePointManager.ServerCertificateValidationCallback = null;
            var menu = (NativeMenu)browser.GetField("Menu", Static)!.GetValue(null)!;
            menu.ResetCursorWhenOpened = false;
            menu.SoundOpened = null;
            menu.SoundClose = null;
            using (var fixture = new BrowserDownloadFixture())
            {
                ((Settings)settings.GetValue(null)!).MasterServer = fixture.Url;
                var handler = (CancelEventHandler)typeof(NativeMenu).GetField("Opening", instance)!.GetValue(menu)!;
                Assert(handler.GetInvocationList().Any(callback => callback.Method == opening), "The actual initialized menu subscribes the exact guarded opening handler.");
                menu.Visible = true;
                Thread worker = (Thread)browser.GetField("GetServersThread", Static)!.GetValue(null)!;
                Assert(worker.Join(7000), "The actual browser opening worker finishes against a bounded loopback fixture.");
                fixture.Complete();
                Assert(ServicePointManager.ServerCertificateValidationCallback == null,
                    "The actual opening event installs TLS protection before its worker; normal default certificate validation remains null.");
            }
            clearQueue.Invoke(null, null);
            menu.Visible = false;
            CheckBrowserRequests(browser, settings, clearQueue, menu, boundary, list);
            CheckServerBrowserTlsOwners(true);
            Assert(Harmony.GetPatchInfo(target)?.Transpilers.Count(patch => patch.owner == TlsOwner) == 1,
                "Normal opening installs exactly one download transpiler.");
            Install(); Install();
            foreign.Patch(target, postfix: TlsBoundary(nameof(TlsForeignPostfix)));
            ServicePointManager.ServerCertificateValidationCallback = strict;
            using (var fixture = new BrowserDownloadFixture())
            {
                Assert(Download(fixture.Url) == BrowserDownloadFixture.Body, "The guarded actual browser retains successful download results.");
                fixture.Complete();
                Assert(ReferenceEquals(ServicePointManager.ServerCertificateValidationCallback, strict) && fixture.UserAgent == "Mozilla/5.0 RageCoop/1.5.12.15",
                    "The guarded browser preserves an existing certificate callback and its original User-Agent.");
            }
            clearQueue.Invoke(null, null);
            Assert(ServicePointManager.Expect100Continue && ServicePointManager.SecurityProtocol == (SecurityProtocolType)15360,
                "The narrow guard preserves the installed Expect100Continue and TLS protocol selection.");
            MethodInfo setter = typeof(ServicePointManager).GetProperty(nameof(ServicePointManager.ServerCertificateValidationCallback))!.GetSetMethod()!;
            Assert(!PatchProcessor.GetCurrentInstructions(target).Any(code => code.Calls(setter)), "The patched real download body no longer assigns the process certificate callback.");
            bool incompatible = false;
            try
            {
                ((System.Collections.Generic.IEnumerable<CodeInstruction>)guard.GetMethod("PreserveCertificateValidation", Static)!.Invoke(null,
                    new object[] { new[] { new CodeInstruction(OpCodes.Ret) } })!).ToArray();
            }
            catch (InvalidOperationException) { incompatible = true; }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { incompatible = true; }
            Assert(incompatible, "An incompatible browser body is rejected instead of receiving a guessed TLS patch.");
            guard.GetMethod("Uninstall", Static)!.Invoke(null, null);
            guard.GetMethod("Uninstall", Static)!.Invoke(null, null);
            CheckServerBrowserTlsOwners(false);
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains(foreign.Id) == true,
                "Repeated TLS cleanup preserves another owner's download patch.");
            Install();
            CheckServerBrowserTlsOwners(true);
            Console.WriteLine("Actual .NET Framework browser passed: bounded child reproduces original raw-thread unhandled JSON failure; actual Opening/Thread, valid/empty lists, malformed/null rows, HTTP/connection failure, closed/reopened stale replies and delayed worker origin are checked. Eight rapid openings retain one active request and only the latest pending original Thread; latest Loading settles and closed pending requests are discarded. Browser UI is queued and request-scoped; TLS callbacks/headers/protocols and scoped unload/reinstall are retained. Only loopback HTTP sockets were used; native UI calls were isolated. Actual invalid-certificate HTTPS rejection was not tested: the CrossOver Schannel server cannot acquire the generated certificate key (0x8009030d).");
        }
        finally
        {
            RemoveOwner(foreign); RemoveOwner(boundary);
            clearQueue.Invoke(null, null);
            settings.SetValue(null, priorSettings);
            ServicePointManager.ServerCertificateValidationCallback = previousCallback;
            ServicePointManager.SecurityProtocol = previousProtocol;
            ServicePointManager.Expect100Continue = previousContinue;
            WebRequest.DefaultWebProxy = previousProxy;
        }
    }

    private static void CheckBrowserRequests(Type browser, FieldInfo settings, MethodInfo clearQueue, NativeMenu menu, Harmony boundary, MethodInfo list)
    {
        Type main = settings.DeclaringType!;
        var queue = (List<Func<bool>>)main.GetField("QueuedActions", Static)!.GetValue(null)!;
        Thread Worker() => (Thread)browser.GetField("GetServersThread", Static)!.GetValue(null)!;
        void Open(BrowserDownloadFixture fixture)
        {
            ((Settings)settings.GetValue(null)!).MasterServer = fixture.Url;
            menu.Visible = true;
        }
        void Finish(BrowserDownloadFixture fixture, Thread worker)
        {
            Assert(worker.Join(7000), "The real browser worker ends within the loopback fixture bound.");
            fixture.Complete();
        }
        void Drain()
        {
            // Execute the real queued callbacks directly: an unexpected UI
            // exception must fail this check rather than be logged and hidden.
            Func<bool>[] actions;
            lock (queue) { actions = queue.ToArray(); queue.Clear(); }
            foreach (Func<bool> action in actions) Assert(action(), "A browser UI callback completes in one game-thread queue pass.");
        }
        string[] Titles() => menu.Items.Select(item => item.Title).ToArray();
        void One(string body, string expected, string description, string status = "200 OK")
        {
            using var fixture = new BrowserDownloadFixture(body, status);
            Open(fixture);
            Finish(fixture, Worker());
            Assert(Titles().SequenceEqual(new[] { "Loading..." }), "The browser worker never mutates the loading menu before the UI queue runs.");
            Drain();
            Assert(Titles().SequenceEqual(new[] { expected }) && menu.Items[0].Description.Contains(description),
                "The actual Opening worker and original result renderer handle " + description + ".");
            menu.Visible = false;
        }

        One(BrowserDownloadFixture.ServerBody("Newest"), "[RU] Newest", "127.0.0.1:4499");
        Assert(!menu.Visible && menu.Items.Count == 0, "The actual Closing handler clears the completed server rows.");
        One("[]", "No server was found!", "");
        One("{broken", "Список серверов недоступен", "повреждённый");
        One("null", "Список серверов недоступен", "неверный");
        One("[null]", "Список серверов недоступен", "неверный");
        One("[]", "Список серверов недоступен", "Не удалось загрузить", "503 Service Unavailable");
        One("[]", "Список серверов недоступен", "Не удалось загрузить", "disconnect");

        foreach (var response in new[] { (BrowserDownloadFixture.ServerBody("Old"), "200 OK"), ("{broken", "200 OK"), ("[]", "503 Service Unavailable") })
        {
            using var old = new BrowserDownloadFixture(response.Item1, response.Item2, delayed: true);
            Open(old);
            Thread oldWorker = Worker();
            old.WaitForRequest();
            menu.Visible = false;
            using var current = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Newest"));
            Open(current);
            Thread currentWorker = Worker();
            Assert((currentWorker.ThreadState & System.Threading.ThreadState.Unstarted) != 0 && !current.HasRequest,
                "A reopened menu keeps its latest original Thread unstarted while the prior HTTP request is active.");
            old.Release();
            Finish(old, oldWorker);
            int notifications = _browserNotifications;
            Drain();
            Assert(Titles().SequenceEqual(new[] { "Loading..." }) && _browserNotifications == notifications,
                "A stale success, JSON failure or HTTP failure cannot mutate or notify over the newest loading row.");
            Finish(current, currentWorker);
            Drain();
            Assert(Titles().SequenceEqual(new[] { "[RU] Newest" }), "The newest pending request starts after completion and settles the reopened list.");
            menu.Visible = false;
        }

        using (var closed = new BrowserDownloadFixture("{broken", delayed: true))
        {
            Open(closed);
            Thread worker = Worker();
            closed.WaitForRequest();
            menu.Visible = false;
            closed.Release();
            Finish(closed, worker);
            Drain();
            Assert(!menu.Visible && menu.Items.Count == 0, "A failed request completed after Closing does not revive the closed menu or its removed row.");
        }

        _browserWorkerEntered = new ManualResetEventSlim();
        _browserWorkerRelease = new ManualResetEventSlim();
        boundary.Patch(list, prefix: new HarmonyMethod(typeof(EarlyClientLifecycle), nameof(DelayBrowserWorker)) { priority = Priority.First });
        try
        {
            using var old = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Old"));
            Interlocked.Exchange(ref _delayNextBrowserWorker, 1);
            Open(old);
            Thread oldWorker = Worker();
            Assert(_browserWorkerEntered.Wait(7000), "The old original worker is held before entering GetAllServers.");
            menu.Visible = false;
            using var current = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Newest"));
            Open(current);
            Thread currentWorker = Worker();
            _browserWorkerRelease.Set();
            Finish(old, oldWorker);
            Drain();
            Finish(current, currentWorker);
            Drain();
            Assert(old.UserAgent == "Mozilla/5.0 RageCoop/1.5.12.15" && Titles().SequenceEqual(new[] { "[RU] Newest" }),
                "A worker delayed until after reopen retains its Opening URL and request identity, and cannot replace the newest list.");
            menu.Visible = false;
        }
        finally
        {
            _browserWorkerRelease.Set();
            boundary.Unpatch(list, HarmonyPatchType.Prefix, boundary.Id);
            _browserWorkerEntered.Dispose();
            _browserWorkerRelease.Dispose();
            _browserWorkerEntered = null;
            _browserWorkerRelease = null;
            clearQueue.Invoke(null, null);
        }
        CheckBrowserRequestBudget(menu, Open, Finish, Drain, Worker);
    }

    private static void CheckBrowserRequestBudget(NativeMenu menu, Action<BrowserDownloadFixture> open,
        Action<BrowserDownloadFixture, Thread> finish, Action drain, Func<Thread> worker)
    {
        var pendingFixtures = new List<BrowserDownloadFixture>();
        var pendingWorkers = new List<Thread>();
        try
        {
            using var busy = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Busy"), delayed: true);
            open(busy);
            Thread active = worker();
            busy.WaitForRequest();
            for (int index = 0; index < 8; index++)
            {
                menu.Visible = false;
                var fixture = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Latest" + index));
                pendingFixtures.Add(fixture);
                open(fixture);
                pendingWorkers.Add(worker());
            }
            Type guard = typeof(EarlyClientScript).Assembly.GetType("RageCoop.EarlyClient.ServerBrowserTlsGuard", true)!;
            Assert(active.IsAlive && pendingWorkers.All(thread => (thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) &&
                pendingFixtures.All(fixture => !fixture.HasRequest) &&
                ReferenceEquals(guard.GetField("_active", Static)!.GetValue(null), active) &&
                ReferenceEquals(guard.GetField("_pending", Static)!.GetValue(null), pendingWorkers.Last()),
                "Eight rapid actual Opening events retain one active HTTP worker and only the latest pending unstarted request.");
            busy.Release();
            finish(busy, active);
            drain();
            finish(pendingFixtures.Last(), pendingWorkers.Last());
            drain();
            Assert(menu.Items.Select(item => item.Title).SequenceEqual(new[] { "[RU] Latest7" }) &&
                pendingFixtures.Take(7).All(fixture => !fixture.HasRequest) &&
                pendingWorkers.Take(7).All(thread => (thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0),
                "Only the newest coalesced Opening downloads and replaces Loading after the active response ends.");
            menu.Visible = false;
        }
        finally { foreach (BrowserDownloadFixture fixture in pendingFixtures) fixture.Dispose(); }

        using var old = new BrowserDownloadFixture("{broken", delayed: true);
        using var closedPending = new BrowserDownloadFixture(BrowserDownloadFixture.ServerBody("Closed"));
        open(old);
        Thread oldWorker = worker();
        old.WaitForRequest();
        menu.Visible = false;
        open(closedPending);
        Thread pending = worker();
        menu.Visible = false;
        old.Release();
        finish(old, oldWorker);
        drain();
        Assert(!menu.Visible && menu.Items.Count == 0 && !closedPending.HasRequest &&
            (pending.ThreadState & System.Threading.ThreadState.Unstarted) != 0,
            "Closing a pending request discards it after the active worker ends, without downloading or reviving the closed menu.");
    }

    private static void DelayBrowserWorker()
    {
        if (Interlocked.CompareExchange(ref _delayNextBrowserWorker, 0, 1) != 1) return;
        _browserWorkerEntered!.Set();
        if (!_browserWorkerRelease!.Wait(7000)) throw new TimeoutException("The isolated delayed browser worker was not released.");
    }

    private static void CheckOriginalBrowserJsonFailure(string binaries)
    {
        var start = new ProcessStartInfo(Path.Combine(binaries, "EarlyClientCheck.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.EnvironmentVariables[BrowserBaseline] = "1";
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated original browser JSON check.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(25000))
        {
            child.Kill();
            child.WaitForExit(5000);
            throw new TimeoutException("The isolated original browser JSON child did not exit.");
        }
        string stdout = output.GetAwaiter().GetResult();
        string stderr = errors.GetAwaiter().GetResult();
        Assert(child.ExitCode == 73 && stdout.Contains("ORIGINAL BROWSER RAW THREAD UNHANDLED Newtonsoft.Json.JsonReaderException") &&
            stdout.Contains("GetAllServers") && !stdout.Contains("UNEXPECTED ORIGINAL BROWSER SURVIVAL"),
            "The bounded child reproduces malformed HTTP-200 JSON escaping the actual original Opening worker; its unhandled observer exits before crash dialogs or dumps. " + stderr);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static void RunOriginalBrowserJsonFailure(Type guard, Type browser, FieldInfo settings)
    {
        SetErrorMode(3); // Child only: suppress critical-error/GP-fault dialogs.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var error = args.ExceptionObject as Exception;
            Console.WriteLine("ORIGINAL BROWSER RAW THREAD UNHANDLED " + error?.GetType().FullName);
            Console.WriteLine(error?.StackTrace);
            Console.Out.Flush();
            // Observe the real raw-thread failure, then exit before the CLR's
            // default crash UI/reporting; never crash the parent or live game.
            Environment.Exit(error?.GetType().FullName == "Newtonsoft.Json.JsonReaderException" ? 73 : 74);
        };
        guard.GetMethod("Uninstall", Static)!.Invoke(null, null);
        using var fixture = new BrowserDownloadFixture("{broken");
        ((Settings)settings.GetValue(null)!).MasterServer = fixture.Url;
        var menu = (NativeMenu)browser.GetField("Menu", Static)!.GetValue(null)!;
        menu.ResetCursorWhenOpened = false;
        menu.SoundOpened = null;
        menu.SoundClose = null;
        menu.Visible = true;
        Thread worker = (Thread)browser.GetField("GetServersThread", Static)!.GetValue(null)!;
        if (!worker.Join(7000)) throw new TimeoutException("The original browser child worker exceeded its loopback bound.");
        Console.WriteLine("UNEXPECTED ORIGINAL BROWSER SURVIVAL");
        throw new InvalidOperationException("The original malformed-JSON worker unexpectedly survived.");
    }

    private static HarmonyMethod TlsBoundary(string name) => new HarmonyMethod(typeof(EarlyClientLifecycle), name);
    private static bool CaptureBrowserNotification()
    {
        if (Environment.GetEnvironmentVariable(BrowserBaseline) == "1") return false;
        Assert(Thread.CurrentThread.ManagedThreadId == _browserUiThread, "Browser notifications run only through the game-thread UI queue.");
        _browserNotifications++;
        return false;
    }
    private static bool SkipTlsNative() => false;
    private static bool TlsTextWidth(ref float __result) { __result = 0; return false; }
    private static bool TlsLayoutPoint(PointF __0, ref PointF __result) { __result = __0; return false; }
    private static void TlsForeignPostfix() { }
}

internal sealed class BrowserDownloadFixture : IDisposable
{
    internal const string Body = "[]";
    private readonly TcpListener _listener;
    private readonly Task _worker;
    private readonly string _body;
    private readonly string _status;
    private readonly ManualResetEventSlim _received = new ManualResetEventSlim();
    private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
    internal string Url { get; }
    internal string? UserAgent { get; private set; }
    internal bool HasRequest => _received.IsSet;

    internal BrowserDownloadFixture(string body = Body, string status = "200 OK", bool delayed = false)
    {
        _body = body;
        _status = status;
        if (!delayed) _release.Set();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = "http://localhost:" + port + "/servers";
        _worker = Task.Run(() =>
        {
            try { Serve(); }
            finally { _listener.Stop(); }
        });
    }

    private void Serve()
    {
        using TcpClient client = _listener.AcceptTcpClient();
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        using NetworkStream stream = client.GetStream();
        var request = new StringBuilder();
        while (request.Length < 16384 && !request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int next = stream.ReadByte();
            if (next < 0) throw new IOException("The loopback browser request closed before its headers.");
            request.Append((char)next);
        }
        if (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) throw new InvalidDataException("The loopback browser fixture header exceeded its limit.");
        UserAgent = request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)
            .FirstOrDefault(line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))?.Substring("User-Agent:".Length).Trim();
        _received.Set();
        if (!_release.Wait(7000)) throw new TimeoutException("The isolated browser response was not released.");
        if (_status == "disconnect") return;
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + _status + "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.ASCII.GetByteCount(_body) + "\r\nConnection: close\r\n\r\n" + _body);
        stream.Write(response, 0, response.Length);
        stream.Flush();
    }

    internal void Complete()
    {
        if (!_worker.Wait(7000)) throw new TimeoutException("The loopback browser fixture did not finish.");
    }

    internal static string ServerBody(string name) => "[{\"address\":\"127.0.0.1\",\"port\":4499,\"country\":\"RU\",\"name\":\"" + name +
        "\",\"version\":\"1.5.12.15\",\"players\":1,\"maxPlayers\":8,\"useZT\":false}]";

    internal void WaitForRequest()
    {
        if (!_received.Wait(7000)) throw new TimeoutException("The isolated browser did not request its loopback response.");
    }

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _release.Set();
        _listener.Stop();
        try
        {
            if (!_worker.Wait(7000)) throw new TimeoutException("The loopback browser fixture did not stop.");
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1 &&
            (error.InnerException is SocketException || error.InnerException is IOException))
        { }
        finally { _received.Dispose(); _release.Dispose(); }
    }
}
