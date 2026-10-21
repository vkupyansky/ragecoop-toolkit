using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using GTA;
using GTA.Input;
using GTA.Native;
using HarmonyLib;
using LemonUI;
using LemonUI.Menus;

internal static partial class Program
{
    private static readonly HashSet<(int group, int action)> InputHeld = new HashSet<(int, int)>();
    private static readonly HashSet<(int group, int action)> InputEdges = new HashSet<(int, int)>();
    private static readonly HashSet<(int group, int action)> InputReleases = new HashSet<(int, int)>();
    private static readonly HashSet<Keys> InputKeys = new HashSet<Keys>();
    private static readonly HashSet<(int group, int action)> InputDisabled = new HashSet<(int, int)>();
    private static bool _inputChat, _inputHostMenu, _inputPause, _inputKeyboardMode;
    private static bool _inputConnected = true;
    private static bool _inputWorldTick;
    private static bool _inputActivationPauses;
    private static int _inputActivationComponent;
    private static readonly List<Hash> InputWorldWrites = new List<Hash>();
    private static int _inputPauseActivations, _inputWorldUnpauses, _inputBoostFrames, _inputRepairs;
    private static float _inputBoostElapsed;
    private static IntPtr _inputTextLayout;
    private static readonly GTA.Player InputPlayer = (GTA.Player)FormatterServices.GetUninitializedObject(typeof(GTA.Player));
    private static readonly Dictionary<MethodInfo, DynamicMethod> InputStubs = new Dictionary<MethodInfo, DynamicMethod>();

    private static void CheckInterfaceInputComposition()
    {
        var boundary = new Harmony("friendslegacy.check.interfaceinputboundary");
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        Type guard = Resource.GetType("RageCoopAdmin.Client.HostMenuInputGuard", true)!;
        Type passenger = Resource.GetType("RageCoopAdmin.Client.PassengerSeatGuard", true)!;
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        MethodInfo hostKey = host.GetMethod("OnKeyDown", Instance)!;
        Type world = Engine.GetType("RageCoop.Client.WorldThread", true)!;
        MethodInfo worldTick = world.GetMethod("OnTick", Instance)!;
        MethodInfo Method(string name) => script.GetMethod(name, Instance)!;
        void Call(object instance, string name, params object[] args) => Method(name).Invoke(instance, args);
        void Guard(string name, params object[] args)
        {
            if (name == "Install" && args.Length == 2)
                args = args.Concat(new object[] { new Func<bool>(() => false) }).ToArray();
            guard.GetMethod(name, Static)!.Invoke(null, args);
        }
        try
        {
            Assert(PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Game), "IsControlPressed", new[] { typeof(GTA.Control) }))
                    .Count(code => code.LoadsConstant(unchecked((long)Hash.IS_DISABLED_CONTROL_PRESSED))) == 1 &&
                PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Controls), "IsControlPressed", new[] { typeof(ControlType), typeof(ControlAction) }))
                    .Count(code => code.LoadsConstant(unchecked((long)Hash.IS_CONTROL_PRESSED))) == 1 &&
                PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(typeof(Controls), "IsControlJustPressed", new[] { typeof(ControlType), typeof(ControlAction) }))
                    .Count(code => code.LoadsConstant(unchecked((long)Hash.IS_CONTROL_JUST_PRESSED))) == 1,
                "The isolated enabled/disabled input boundary matches the installed SHVDN native contracts.");
            // Keep the assembled resource, host and LemonUI control flow real.
            // Only graphics/native/device/account boundaries are isolated.
            foreach (string name in new[] { "ProcessQuickControls", "ProcessMenus", "SuppressMenuGameActions", "IsInputBlocked", "HandleMenuKey", "Pressed", "JustPressed", "ToggleMainMenu", "OwnsHostMenuCancel", "PromptUserInput", "NativeMenuCancelPressed" })
                boundary.Patch(script.GetMethod(name, Instance | Static)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)) { priority = Priority.Last });
            boundary.Patch(Method("OnTick"), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputTickBoundaries)));
            boundary.Patch(Resource.GetType("RageCoopAdmin.Client.UnicodeKeyboardTextReader", true)!.GetMethod("Read", Static)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(InputKeyboardTextBoundary)));
            boundary.Patch(script.GetMethod("IsMenuUsingController", Static)!, prefix: new HarmonyMethod(typeof(Program), nameof(InputMenuDeviceBoundary)));
            boundary.Patch(script.GetMethod("CreateMenuControlHint", Static)!, prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationHintBoundary)));
            boundary.Patch(hostKey, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)) { priority = Priority.Last });
            boundary.Patch(worldTick, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)) { priority = Priority.Last });
            foreach (string name in new[] { "ReadHostPause", "ProcessWorldPauseInput", "DisablePauseControls", "ReadFrontendHeld", "ReadFrontendPress", "SuppressTextControl" })
                boundary.Patch(guard.GetMethod(name, Static)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            Type lemon = typeof(NativeMenu).Assembly.GetType("LemonUI.Controls", true)!;
            foreach (MethodInfo method in lemon.GetMethods(Static).Where(method => method.Name is "IsPressed" or "IsJustPressed" or "DisableThisFrame" || method.Name == "get_IsUsingController"))
                boundary.Patch(method, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            boundary.Patch(typeof(NativeMenu).GetMethod("ProcessControls", Instance)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            boundary.Patch(typeof(NativeMenu).GetMethod("Process", Instance)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            foreach (string name in new[] { "Draw", "UpdateItems", "Recalculate", "UpdateDescription" })
                boundary.Patch(typeof(NativeMenu).GetMethod(name, Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            foreach (Type element in new[] { typeof(LemonUI.Elements.ScaledText), typeof(LemonUI.Elements.ScaledTexture), typeof(LemonUI.Elements.ScaledRectangle) })
                boundary.Patch(element.GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(LemonUI.Elements.ScaledTexture).GetMethod("Request", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(LemonUI.Elements.ScaledText).GetProperty("Width")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(IsolateTextWidth)));
            Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
            boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(System.Drawing.PointF) }, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(KeepLayoutPoint)));
            foreach (string name in new[] { "DetectResolutionChanges", "DetectSafezoneChanges" })
                boundary.Patch(typeof(ObjectPool).GetMethod(name, Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(LemonUI.Scaleform.BaseScaleform).GetMethod("Draw", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(Method("BoostOwnVehicle"), prefix: new HarmonyMethod(typeof(Program), nameof(CaptureInputBoost)));
            boundary.Patch(Method("RequestRepair"), prefix: new HarmonyMethod(typeof(Program), nameof(CaptureInputRepair)));
            boundary.Patch(Engine.GetType("RageCoop.Client.Scripting.API", true)!.GetProperty("IsOnServer")!.GetGetMethod()!,
                prefix: new HarmonyMethod(typeof(Program), nameof(InputConnectionState)));
            List<CodeInstruction> boost = PatchProcessor.GetOriginalInstructions(Method("BoostOwnVehicle"));
            Assert(boost.Any(code => code.Calls(typeof(Model).GetProperty(nameof(Model.IsCar))!.GetGetMethod()!)) &&
                !boost.Any(code => code.Calls(typeof(Vehicle).GetProperty(nameof(Vehicle.Type))!.GetGetMethod()!)),
                "The assembled boost classifies cars through the GTA model native without requiring SHVDN's vehicle-type memory offset.");

            object instance = FormatterServices.GetUninitializedObject(script);
            foreach (FieldInfo field in script.GetFields(Instance).Where(field => field.Name is "_shortcutLatch" or "_gamepadChord" or "_cancelReleaseGuard" or "_shortcutMenuInputGuard" or "_cancelMenuInputGuard" or "_interfaceSettings" or "_suspendedMenuInputs" or "_textInputActivationGuard"))
                field.SetValue(instance, Activator.CreateInstance(field.FieldType, true));
            script.GetField("_active", Instance)!.SetValue(instance, true);
            var pool = (ObjectPool)FormatterServices.GetUninitializedObject(typeof(ObjectPool));
            FieldInfo objects = typeof(ObjectPool).GetFields(Instance).Single(field => field.FieldType == typeof(List<IProcessable>));
            objects.SetValue(pool, new List<IProcessable>());
            NativeMenu main = BareInputMenu(), child = BareInputMenu();
            child.Parent = main;
            pool.Add(main); pool.Add(child);
            script.GetField("_pool", Instance)!.SetValue(instance, pool);
            script.GetField("_mainMenu", Instance)!.SetValue(instance, main);
            Type settings = Engine.GetType("RageCoop.Client.Settings", true)!;
            object config = FormatterServices.GetUninitializedObject(settings);
            settings.GetProperty("MenuKey")!.SetValue(config, Keys.F9);
            settings.GetProperty("DisableAlternatePause")!.SetValue(config, true);
            host.GetField("Settings", Static)!.SetValue(null, config);
            Type chat = Engine.GetType("RageCoop.Client.Chat", true)!;
            host.GetField("MainChat", Static)!.SetValue(null, FormatterServices.GetUninitializedObject(chat));
            object hostInstance = FormatterServices.GetUninitializedObject(host);
            object worldInstance = FormatterServices.GetUninitializedObject(world);
            void WorldFrame()
            {
                _inputWorldTick = true;
                try { worldTick.Invoke(worldInstance, new object[] { worldInstance, EventArgs.Empty }); }
                finally { _inputWorldTick = false; }
            }
            Func<bool> owns = () => (bool)Method("OwnsHostMenuCancel").Invoke(instance, Array.Empty<object>())!;
            Func<bool> processing = () => (bool)Method("IsProcessingResourceMenuInput").Invoke(instance, Array.Empty<object>())!;
            Func<bool> typing = () => (bool)Method("OwnsTextInput").Invoke(instance, Array.Empty<object>())!;

            // Execute the stock connected world + keyboard path first: Start
            // cannot reach GTA, while disabled Esc still forces the map open.
            InputHeld.Add((0, 199)); InputEdges.Add((0, 199));
            WorldFrame();
            Assert(InputDisabled.SetEquals(new[] { (0, 199), (0, 200) }) && !InputControlHeld(ControlType.PlayerControl, ControlAction.FrontendPause),
                "The original real connected WorldThread disables native Xbox Start and alternate pause every frame.");
            Hash[] originalWorldWrites = InputWorldWrites.ToArray();
            Assert(originalWorldWrites.Length == 8, "The original connected WorldThread still executes its density and traffic native writes.");
            InputHeld.Clear(); InputEdges.Clear(); InputHeld.Add((0, 200)); InputKeys.Add(Keys.Escape);
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(_inputPauseActivations == 1 && !_inputActivationPauses && _inputActivationComponent == 0,
                "The original real host forces a non-pausing map from a disabled Esc press reserved by phone/shop scripts.");
            _inputPause = false; _inputPauseActivations = 0;
            InputDisabled.Clear(); InputHeld.Clear(); InputEdges.Clear(); InputKeys.Clear(); InputWorldWrites.Clear();

            List<CodeInstruction> worldInstructions = PatchProcessor.GetOriginalInstructions(worldTick);
            Assert((bool)guard.GetMethod("MatchesWorldPauseDisables", Static)!.Invoke(null, new object[] { worldTick, worldInstructions })!,
                "The world pause guard accepts the exact installed WorldThread pause-disable call sites.");
            List<CodeInstruction> changedWorld = worldInstructions.Select(code => new CodeInstruction(code)).ToList();
            changedWorld.Single(code => code.LoadsConstant(200)).operand = 201;
            Assert(!(bool)guard.GetMethod("MatchesWorldPauseDisables", Static)!.Invoke(null, new object[] { worldTick, changedWorld })!,
                "A changed world pause control contract is rejected before installing any guard.");
            Guard("Install", owns, processing, typing);
            Guard("Install", owns, processing);
            Assert(!PatchProcessor.GetOriginalInstructions(guard.GetMethod("ProcessWorldPauseInput", Static)!)
                .Any(code => code.LoadsConstant(unchecked((long)Hash.ACTIVATE_FRONTEND_MENU)) ||
                    code.LoadsConstant(unchecked((long)Hash.SET_PAUSE_MENU_ACTIVE))),
                "The actual built pause guard contains no forced frontend activation or menu-state toggle.");
            Assert(Harmony.GetPatchInfo(hostKey)!.Transpilers.Count(patch => patch.owner == "friendslegacy.ragecoop.menuinput") == 1,
                "The assembled host menu guard installs once on the real earlier keyboard handler.");
            Assert(Harmony.GetPatchInfo(worldTick)!.Transpilers.Count(patch => patch.owner == "friendslegacy.ragecoop.menuinput") == 1,
                "The assembled world pause guard installs once alongside the keyboard guard.");

            void Frame()
            {
                InputDisabled.Clear();
                WorldFrame();
                // Keep the actual tick's input ownership and HideAll branches;
                // stop at its first timestamp, before unrelated gameplay work.
                bool reachedGameplay = false;
                try { Call(instance, "OnTick"); }
                catch (TargetInvocationException ex) when (ex.InnerException is InputTickBoundaryReached) { reachedGameplay = true; }
                if (!reachedGameplay) throw new InvalidOperationException("The real input Tick returned before its gameplay boundary.");
                bool consumed = (bool)Method("ProcessQuickControls").Invoke(instance, new object[] { Stopwatch.GetTimestamp() })!;
                Call(instance, "ProcessMenus", consumed);
                InputEdges.Clear();
            }
            void Release()
            {
                InputHeld.Clear(); InputEdges.Clear(); InputReleases.Clear(); InputKeys.Clear();
                Call(instance, "OnKeyUp", hostInstance, new KeyEventArgs(Keys.Escape));
                for (int frame = 0; frame < 5; frame++) Frame();
            }

            // The native frontend remains the input boundary: the actual host,
            // world and resource must leave every phase untouched. Model both
            // native-before-script and script-before-native orderings, and a
            // frontend which commits on press or release. The resource must
            // never invent a transition before or after that game-owned step.
            int stockTransitions = 0;
            void StockFrontendFrame(bool nativeFirst, bool transition, int group, int action, bool released = false)
            {
                InputDisabled.Clear();
                void NativeFrontend()
                {
                    if (!transition) return;
                    Assert(InputNativeBool(released ? Hash.IS_CONTROL_JUST_RELEASED : Hash.IS_CONTROL_JUST_PRESSED,
                        new[] { new InputArgument((ulong)group), new InputArgument((ulong)action) }),
                        "The isolated stock frontend receives its own native input edge.");
                    Assert(!InputDisabled.Contains((group, action)),
                        "WorldThread leaves the stock frontend's chosen press/release edge enabled.");
                    _inputPause = !_inputPause;
                    stockTransitions++;
                }
                if (nativeFirst) NativeFrontend();
                bool activeAtWorld = _inputPause;
                int unpauses = _inputWorldUnpauses;
                WorldFrame();
                Assert(_inputWorldUnpauses - unpauses == (activeAtWorld ? 1 : 0),
                    "The actual connected world unpauses simulation once only while GTA's native pause menu is active.");
                hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
                if (!nativeFirst) NativeFrontend();
                Assert(InputDisabled.Count == 0 && _inputPauseActivations == 0,
                    "The actual connected world and keyboard handlers neither replay nor disable GTA-owned pause input.");
            }
            foreach (bool alternateSetting in new[] { true, false })
                foreach (int action in new[] { 199, 200 })
                    foreach (bool nativeFirst in new[] { true, false })
                        foreach (bool commitOnRelease in new[] { true, false })
                            foreach (int heldFrames in new[] { 0, 4 })
                                foreach (int releaseGroup in new[] { 0, 2 })
                                {
                                    settings.GetProperty("DisableAlternatePause")!.SetValue(config, alternateSetting);
                                    _inputPause = false;
                                    stockTransitions = 0;
                                    InputHeld.Add((0, action)); InputEdges.Add((0, action));
                                    StockFrontendFrame(nativeFirst, !commitOnRelease, 0, action);
                                    Assert(_inputPause == !commitOnRelease,
                                        "A short or held Start/Esc opens only when the stock frontend commits its edge.");
                                    InputEdges.Clear();
                                    for (int heldFrame = 0; heldFrame < heldFrames; heldFrame++)
                                    {
                                        InputHeld.Clear(); InputHeld.Add((2, action));
                                        if (heldFrame == 1) InputEdges.Add((2, action));
                                        StockFrontendFrame(nativeFirst, false, 2, action);
                                        Assert(_inputPause == !commitOnRelease,
                                            "A group change or late frontend press never creates another resource toggle.");
                                        InputEdges.Clear();
                                    }
                                    InputHeld.Clear(); InputReleases.Add((releaseGroup, action));
                                    StockFrontendFrame(nativeFirst, commitOnRelease, releaseGroup, action, released: true);
                                    Assert(_inputPause && stockTransitions == 1 && InputReleases.Contains((releaseGroup, action)),
                                        "Both groups preserve the opening release; a short Start tap leaves the map open once.");
                                    InputReleases.Clear();
                                    for (int frame = 0; frame < 3; frame++) StockFrontendFrame(nativeFirst, false, 2, action);
                                    Assert(_inputPause && stockTransitions == 1,
                                        "Idle frames after release do not close the stock map.");

                                    InputHeld.Add((2, action)); InputEdges.Add((2, action));
                                    StockFrontendFrame(nativeFirst, !commitOnRelease, 2, action);
                                    InputEdges.Clear(); InputHeld.Clear(); InputReleases.Add((releaseGroup, action));
                                    StockFrontendFrame(nativeFirst, commitOnRelease, releaseGroup, action, released: true);
                                    Assert(!_inputPause && stockTransitions == 2,
                                        "The next separate Start/Esc press closes through GTA with no reopen from either script ordering.");
                                    InputReleases.Clear();
                                    Release();
                                }
            settings.GetProperty("DisableAlternatePause")!.SetValue(config, true);
            _inputPause = true; _inputConnected = false;
            int disconnectedUnpauses = _inputWorldUnpauses;
            InputDisabled.Clear(); WorldFrame();
            Assert(_inputWorldUnpauses == disconnectedUnpauses && InputDisabled.Count == 0,
                "The real disconnected WorldThread leaves ordinary single-player simulation pause untouched.");
            _inputConnected = true; _inputPause = false;
            Guard("Uninstall"); Guard("Install", owns, processing, typing);
            InputDisabled.Clear(); InputHeld.Clear(); InputEdges.Clear(); WorldFrame();
            Assert(InputDisabled.Count == 0 && _inputPauseActivations == 0,
                "Uninstall/reinstall leaves idle native pause input alone, with no press latch to carry between sessions.");
            Assert(InputWorldWrites.Take(8).SequenceEqual(originalWorldWrites),
                "Replacing only the original pause-disable call sites preserves the real WorldThread's other native effects and order.");

            // Phone and shop scripts disable pause while owning cancellation.
            // The stock host's disabled-control read used to force the map open
            // anyway, even with every resource menu closed.
            foreach (var context in new[] { (Name: "phone", Cancel: 177), (Name: "Los Santos Customs", Cancel: 202) })
            {
                foreach (int action in new[] { 199, 200 })
                {
                    InputHeld.Add((0, action)); InputKeys.Add(Keys.Escape);
                    InputEdges.Add((0, action)); InputEdges.Add((0, context.Cancel));
                    InputDisabled.Add((0, 199)); InputDisabled.Add((0, 200));
                    WorldFrame();
                    hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
                    Assert(_inputPauseActivations == 0 && !_inputPause && InputDisabled.Count == 2 &&
                        !InputDisabled.Contains((0, context.Cancel)) && InputEdges.Contains((0, context.Cancel)),
                        "Start/Esc respects " + context.Name + " cancellation: a disabled fresh pause edge cannot force the map or consume the game's Back action.");
                    Release();
                }
            }
            main.Visible = true; child.Visible = true; main.Visible = false;
            InputHeld.Add((0, 200)); InputEdges.Add((0, 200)); InputKeys.Add(Keys.Escape);
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Call(instance, "HandleMenuKey", new KeyEventArgs(Keys.Escape)); Frame();
            Assert(main.Visible && !child.Visible && !_inputPause && _inputPauseActivations == 0,
                "Esc traverses one actual LemonUI submenu and cannot activate the host's earlier map branch.");
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Z) }); Frame();
            Assert(main.Visible && _inputPauseActivations == 0, "A different Windows key during held Esc cannot bypass pause ownership or close a second level.");
            Release();
            InputHeld.Add((0, 200)); InputEdges.Add((0, 200)); InputKeys.Add(Keys.Escape);
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Call(instance, "HandleMenuKey", new KeyEventArgs(Keys.Escape)); Frame();
            Assert(!pool.AreAnyVisible && InputDisabled.Contains((0, 200)) && InputDisabled.Contains((2, 200)),
                "Closing the root consumes both input groups and the complete Esc press.");
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Z) }); Frame();
            Assert(_inputPauseActivations == 0, "The host stays suppressed after root closure until cancellation is released.");
            Release();
            InputHeld.Add((0, 199)); InputEdges.Add((0, 199)); WorldFrame();
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(_inputPauseActivations == 0 && !_inputPause && InputDisabled.Count == 0,
                "A fresh enabled pause edge returns to GTA after the resource's consumed cancellation is released.");
            _inputPause = true;
            InputHeld.Clear(); InputEdges.Clear(); InputReleases.Add((2, 199)); InputDisabled.Clear(); WorldFrame();
            Assert(_inputPause && _inputPauseActivations == 0 && InputDisabled.Count == 0,
                "After resource cancellation, a native-opened map's release cannot be replayed or suppressed by the host.");
            InputReleases.Clear();
            _inputPause = false; Release(); _inputPauseActivations = 0;

            // Frontend-only events used to be unreadable by both the resource
            // shortcut and LemonUI's group-zero-only controls.
            InputHeld.UnionWith(new[] { (2, 205), (2, 206), (2, 187) }); InputEdges.Add((2, 187)); Frame();
            Assert(main.Visible && main.SelectedIndex == 0, "An Xbox LB + RB + Down routed only through frontend group 2 opens the actual menu without navigating.");
            Frame(); Assert(main.SelectedIndex == 0, "Holding the opening Xbox direction cannot navigate the newly opened menu.");
            Release();
            InputHeld.Add((2, 187)); InputEdges.Add((2, 187)); Frame();
            Assert(main.SelectedIndex == 1, "The actual LemonUI ProcessControls reads frontend-only navigation during our pool processing.");
            Release();
            InputHeld.Add((2, 202)); InputEdges.Add((2, 202)); Frame();
            Assert(!main.Visible && _inputPauseActivations == 0, "Xbox B routed only through frontend group 2 closes one menu without opening pause.");
            Release();
            InputEdges.Add((2, 187));
            bool unowned = (bool)lemon.GetMethod("IsJustPressed", Static)!.Invoke(null, new object[] { (GTA.Control)173 })!;
            Assert(!unowned, "The LemonUI frontend extension is inactive outside the resource's own Process call.");
            main.Visible = true;
            script.GetField("_processingResourceMenus", Instance)!.SetValue(instance, true);
            script.GetField("_gettingUserInput", Instance)!.SetValue(instance, true);
            Assert(!(bool)lemon.GetMethod("IsJustPressed", Static)!.Invoke(null, new object[] { (GTA.Control)173 })!,
                "A native text prompt yielding during pool processing cannot extend controller input into another menu.");
            script.GetField("_gettingUserInput", Instance)!.SetValue(instance, false);
            script.GetField("_stopping", Instance)!.SetValue(instance, true);
            Assert(!(bool)lemon.GetMethod("IsJustPressed", Static)!.Invoke(null, new object[] { (GTA.Control)173 })!,
                "Resource stop disables frontend mapping even if an interrupted menu callback still owns the processing flag.");
            script.GetField("_stopping", Instance)!.SetValue(instance, false);
            _inputChat = true;
            Assert(!(bool)lemon.GetMethod("IsJustPressed", Static)!.Invoke(null, new object[] { (GTA.Control)173 })!,
                "Chat focus prevents the resource's frontend mapping during a yielded menu callback.");
            _inputChat = false;
            script.GetField("_processingResourceMenus", Instance)!.SetValue(instance, false);
            main.Visible = false;
            InputEdges.Clear();
            Release();
            InputHeld.UnionWith(new[] { (0, 37), (0, 44), (0, 20) }); InputEdges.Add((0, 20));
            _inputKeyboardMode = true; Frame();
            Assert(!main.Visible, "Keyboard Tab + Q + Z cannot impersonate the Xbox gameplay chord.");
            Release();
            _inputKeyboardMode = false;
            script.GetField("_lastMenuShortcut", Instance)!.SetValue(instance, 0L);
            InputHeld.UnionWith(new[] { (0, 37), (0, 44), (0, 20) }); InputEdges.Add((0, 20)); Frame();
            Assert(main.Visible && main.SelectedIndex == 0 && InputDisabled.Contains((0, 19)) && InputDisabled.Contains((0, 20)),
                "Xbox gameplay LB/RB/Down opens the actual menu without requiring inactive script or frontend actions and reserves the character wheel/list.");
            main.Visible = false; Release();
            foreach (var bumpers in new[] { (left: 68, right: 76), (left: 89, right: 90) })
            {
                script.GetField("_lastRepairShortcut", Instance)!.SetValue(instance, 0L);
                int beforeRepair = _inputRepairs;
                InputHeld.UnionWith(new[] { (0, bumpers.left), (0, bumpers.right), (0, 27) }); InputEdges.Add((0, 27));
                // Gameplay DPAD Up is Phone, whereas PhoneUp is its open-phone UI alias.
                Frame();
                Assert(_inputRepairs == beforeRepair + 1 && InputDisabled.Contains((0, 27)),
                    "Car and aircraft gameplay bumper bindings dispatch one repair and consume the Phone action.");
                Frame();
                Assert(_inputRepairs == beforeRepair + 1, "Holding the repair chord cannot repeat its action without a new directional press.");
                Release();
            }

            Type inputType = Resource.GetType("RageCoopAdmin.Client.ResourceTextInput", true)!;
            NativeMenu textMenu = BareInputMenu(0);
            object textInput = Activator.CreateInstance(inputType, Instance, null, new object[] { textMenu }, null)!;
            script.GetField("_textInput", Instance)!.SetValue(instance, textInput);
            pool.Add(textMenu);
            int submitted = 0;
            string? submittedText = null;
            void OpenText(Action<string>? accepted = null)
            {
                main.Visible = true;
                Call(instance, "PromptUserInput", 32, "Найти машину", new Action<string>(text => { submitted++; submittedText = text; accepted?.Invoke(text); }));
                Release();
            }
            void InputCall(string name, params object[] args) => inputType.GetMethod(name, Instance)!.Invoke(textInput, args);
            OpenText();
            Assert(textMenu.Visible && !main.Visible && typing(), "The actual prompt captures typing while keeping its parent hidden.");
            NativeItem TextRow(string title) => textMenu.Items.Single(item => item.Title == title);
            var alphabet = (NativeListItem<string>)TextRow("Язык ввода");
            var letterCase = (NativeListItem<string>)TextRow("Регистр");
            var letter = (NativeListItem<string>)TextRow("Буква");
            letter.SelectedIndex = 32;
            alphabet.SelectedIndex = 2;
            Assert(letter.SelectedIndex == 0 && letter.SelectedItem == "0" && !letterCase.Enabled,
                "The real LemonUI safely replaces the final Cyrillic letter with digits and disables irrelevant case selection.");
            alphabet.SelectedIndex = 1;
            letter.SelectedIndex = 25;
            letterCase.SelectedIndex = 1;
            Assert(letter.SelectedIndex == 0 && letter.SelectedItem == "A", "The real case selector replaces controller letters with capitals without stale selection.");
            letter.SelectedIndex = 25;
            alphabet.SelectedIndex = 2;
            Assert(letter.SelectedIndex == 0 && letter.SelectedItem == "0", "The real LemonUI resets the final Latin letter when changing to digits.");
            _inputKeyboardMode = true;
            Frame();
            Assert(textMenu.Items.Count == 4 && textMenu.SelectedIndex == 0 && textMenu.Items.Skip(1).Select(item => item.Title).SequenceEqual(new[] { "Очистить", "Сохранить", "Отмена" }),
                "A live device change uses the real LemonUI Remove operations to show direct text, Clear, Save and Cancel with no alphabet rows.");
            _inputTextLayout = InputLoadKeyboardLayout("00000419", 0x00000001);
            Assert(_inputTextLayout != IntPtr.Zero, "A real loaded Russian keyboard layout is available for the reported VK_P regression.");
            foreach (int group in new[] { 0, 2 })
            {
                foreach (bool nativeFirst in new[] { false, true })
                {
                    InputCall("Clear");
                    InputKeys.Add(Keys.P); InputHeld.Add((group, 200)); InputEdges.Add((group, 200));
                    if (nativeFirst) Frame();
                    hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.P) });
                    Call(instance, "HandleMenuKey", new KeyEventArgs(Keys.P)); Frame();
                    Assert(textMenu.Visible && typing() && textMenu.Items[0].Title == "з" && !main.Visible && !_inputPause && submitted == 0,
                        "Real Russian ToUnicodeEx VK_P plus native 200 in group " + group + " keeps the field and all parent state intact with " + (nativeFirst ? "native-first" : "key-first") + " delivery.");
                    for (int heldFrame = 0; heldFrame < 3; heldFrame++) Frame();
                    InputHeld.Clear(); InputKeys.Clear(); Frame();
                    InputEdges.Add((group, 200)); Frame();
                    Assert(textMenu.Visible && typing() && textMenu.Items[0].Title == "з" && !main.Visible && !_inputPause && submitted == 0,
                        "A held P, its release and a late native pause edge cannot cancel text, open GTA pause or trigger the actual Tick's HideAll path.");
                    Release();
                }
            }
            foreach (Keys deleteKey in new[] { Keys.Back, Keys.Delete })
            {
                foreach (int alias in new[] { 177, 202 })
                {
                    foreach (int group in new[] { 0, 2 })
                    {
                        InputCall("Clear");
                        InputCall("Append", "а\u0301\ud83d\ude97");
                        InputKeys.Add(deleteKey); InputHeld.Add((group, alias)); InputEdges.Add((group, alias));
                        Call(instance, "HandleMenuKey", new KeyEventArgs(deleteKey)); Frame();
                        Assert(textMenu.Visible && typing() && textMenu.Items[0].Title == "а\u0301" && submitted == 0,
                            "Physical " + deleteKey + " and native cancel " + alias + " in group " + group + " erase one Unicode element without real LemonUI Back.");
                        Call(instance, "HandleMenuKey", new KeyEventArgs(deleteKey)); Frame();
                        Assert(textMenu.Visible && typing() && textMenu.Items[0].AltTitle == "0/32",
                            "Held " + deleteKey + " keeps editing while its complete native cancel press cannot leave the dialog.");
                        Release();
                        InputCall("Append", "в");
                        Call(instance, "HandleMenuKey", new KeyEventArgs(deleteKey));
                        Frame();
                        InputEdges.Add((group, alias)); Frame();
                        Assert(textMenu.Visible && typing() && textMenu.Items[0].AltTitle == "0/32",
                            "A released " + deleteKey + " still consumes its late native cancel edge on the next Tick.");
                        Release();
                        InputCall("Append", "г");
                        InputKeys.Add(deleteKey); InputHeld.Add((group, alias)); InputEdges.Add((group, alias));
                        Frame();
                        Assert(textMenu.Visible && typing() && textMenu.Items[0].Title == "г",
                            "Physical delete polling reserves an alias which reaches Tick before the Windows key event.");
                        Call(instance, "HandleMenuKey", new KeyEventArgs(deleteKey)); Frame();
                        Assert(textMenu.Visible && textMenu.Items[0].AltTitle == "0/32", "The later Windows deletion event edits once after early native reservation.");
                        Release();
                    }
                }
            }
            InputCall("Append", "стереть всё");
            textMenu.SelectedIndex = textMenu.Items.IndexOf(TextRow("Очистить"));
            InputKeys.Add(Keys.Enter); InputHeld.Add((2, 201)); InputEdges.Add((2, 201));
            Call(instance, "HandleMenuKey", new KeyEventArgs(Keys.Enter)); Frame();
            Assert(textMenu.Visible && typing() && submitted == 0 && textMenu.Items[0].AltTitle == "0/32",
                "Keyboard Enter on Clear erases the field and consumes its native Accept without submitting or leaving.");
            Release();
            InputEdges.Add((0, 245));
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.T) });
            InputEdges.Clear();
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.F9) });
            InputHeld.Add((0, 200)); InputKeys.Add(Keys.Escape);
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(!_inputChat && !_inputHostMenu && _inputPauseActivations == 0 && textMenu.Visible,
                "Typing in the assembled resource blocks the host's earlier T, F9 and Esc branches without losing the resource prompt.");
            Call(instance, "OnKeyDown", hostInstance, new KeyEventArgs(Keys.Escape));
            Frame();
            Assert(main.Visible && !textMenu.Visible && !typing() && submitted == 0,
                "One actual Esc cancellation restores the parent without submitting text.");
            Frame();
            Assert(main.Visible && _inputPauseActivations == 0, "Holding cancelled Esc cannot close the restored parent or activate GTA pause.");
            Release();

            int pSubmissions = 0;
            foreach (int group in new[] { 0, 2 })
            {
                foreach (Keys exitKey in new[] { Keys.Enter, Keys.Escape })
                {
                    main.Visible = true;
                    Call(instance, "PromptUserInput", 32, "Имя машины", new Action<string>(text =>
                    {
                        Assert(text == "з", "Saving before P release preserves exactly the typed Russian letter.");
                        pSubmissions++;
                    }));
                    Release();
                    InputKeys.Add(Keys.P); InputHeld.Add((group, 200)); InputEdges.Add((group, 200));
                    Call(instance, "HandleMenuKey", new KeyEventArgs(Keys.P)); Frame();
                    InputKeys.Add(exitKey);
                    if (exitKey == Keys.Enter) { InputHeld.Add((group, 201)); InputEdges.Add((group, 201)); }
                    Frame();
                    Assert(textMenu.Visible && typing(), "A native-first keyboard accept/cancel alias while P is held waits for its actual Enter/Esc event.");
                    Call(instance, "OnKeyDown", hostInstance, new KeyEventArgs(exitKey)); Frame();
                    Assert(main.Visible && !textMenu.Visible && !typing() && !_inputPause &&
                        pSubmissions == (group == 0 ? 1 : 2),
                        "Keyboard " + exitKey + " completes once and restores the garage while Russian P remains physically held.");
                    for (int heldFrame = 0; heldFrame < 3; heldFrame++) Frame();
                    InputKeys.Remove(exitKey); InputHeld.Remove((group, 201));
                    InputEdges.Add((group, 200)); Frame();
                    Assert(main.Visible && !_inputPause && _inputPauseActivations == 0,
                        "The restored parent consumes held P and its late native 200 edge after Enter/Esc is released.");
                    InputKeys.Clear(); InputHeld.Clear(); Frame();
                    InputEdges.Add((group, 200)); Frame();
                    Release();
                    Assert(main.Visible && !_inputPause && _inputPauseActivations == 0,
                        "P release and a delayed native edge cannot back out of the restored garage or leak into GTA pause.");
                }
            }
            main.Visible = false; Release();

            OpenText();
            InputCall("Append", "мерседес");
            InputCall("Finish", true);
            InputCall("Finish", true);
            Assert(submitted == 1 && submittedText == "мерседес" && main.Visible && !typing(),
                "The real text completion returns Unicode once and restores the parent before invoking its callback.");
            Release();
            OpenText();
            InputCall("Append", "не сохранять");
            InputCall("Abandon");
            InputCall("Finish", true);
            Call(instance, "CancelTextInput");
            Assert(submitted == 1 && !textMenu.Visible && !typing(), "Managed stop abandonment prevents a late submitted callback and game-thread cancellation releases text ownership.");
            Release();
            OpenText();
            _inputConnected = false;
            InputCall("Finish", true);
            Assert(submitted == 1 && !main.Visible && !typing(), "Disconnect between opening and completion cannot invoke a stale action or restore its parent.");
            _inputConnected = true;
            Release();

            _inputKeyboardMode = false;
            OpenText();
            Assert(textMenu.Items.Count == 8 && textMenu.SelectedItem == letter,
                "The real prompt restores controller language, case and letter rows on the next controller opening.");
            textMenu.SelectedIndex = textMenu.Items.IndexOf(TextRow("Очистить"));
            InputCall("Append", "очистить");
            InputHeld.Add((2, 201)); InputEdges.Add((2, 201)); Frame();
            Assert(textMenu.Visible && typing() && textMenu.Items[0].AltTitle == "0/32" && submitted == 1,
                "The real controller Clear activation erases all text without submitting.");
            Release();
            InputHeld.Add((2, 202)); InputEdges.Add((2, 202)); Frame();
            Assert(main.Visible && !textMenu.Visible && submitted == 1 && !typing(),
                "Xbox B routed through frontend group 2 cancels the actual text dialog and restores its parent.");
            Frame();
            Assert(main.Visible, "Held Xbox cancellation cannot close the parent restored by the text dialog.");
            Release();

            NativeMenu laterResult = BareInputMenu(0);
            var resultAction = new NativeItem("Найденная машина");
            laterResult.Add(resultAction);
            pool.Add(laterResult);
            int resultActivations = 0;
            resultAction.Activated += (_, _) => resultActivations++;
            OpenText(_ => { main.Visible = false; laterResult.Visible = true; });
            InputCall("Append", "порше");
            Assert(textMenu.SelectedItem == letter && textMenu.Items[0].AltTitle == "5/32", "The actual controller prompt starts on its named letter row and displays its length limit.");
            textMenu.SelectedIndex = textMenu.Items.IndexOf(TextRow("Сохранить"));
            InputHeld.Add((2, 201)); InputEdges.Add((2, 201)); Frame();
            Assert(submitted == 2 && laterResult.Visible && resultActivations == 0,
                "Submitting by Xbox A inside the actual pool cannot immediately activate a result menu later in that same pool.");
            Frame();
            Assert(resultActivations == 0, "Holding the submit button cannot activate a freshly displayed result.");
            Release();
            InputHeld.Add((2, 201)); InputEdges.Add((2, 201)); Frame();
            Assert(resultActivations == 1, "The result menu accepts a distinct new Xbox A press after the text submission is released.");
            laterResult.Visible = false;
            Release();

            var openAction = new NativeItem("Найти машину");
            main.Items[0] = openAction;
            openAction.Activated += (_, _) => Call(instance, "PromptUserInput", 32, "Найти машину", new Action<string>(_ => submitted++));
            main.SelectedIndex = 0;
            main.Visible = true;
            Release();
            int beforeOpening = pool.Count();
            InputHeld.Add((2, 201)); InputEdges.Add((2, 201)); Frame();
            Assert(textMenu.Visible && !main.Visible && typing() && pool.Count() == beforeOpening,
                "Opening the actual text prompt from a menu activation during pool processing does not mutate the pool or consume its opening A.");
            Frame();
            Assert(submitted == 2 && textMenu.Visible, "The held opening A cannot immediately submit the new text field.");
            Release();
            InputCall("Finish", false);
            main.Visible = false;
            Release();

            InputKeys.Add(Keys.O); Frame(); Frame();
            Assert(_inputBoostFrames == 2 && _inputBoostElapsed > 0f, "The actual assembled quick-control path forwards held O every frame.");
            InputKeys.Add(Keys.ShiftKey); Frame();
            Assert(_inputBoostFrames == 3, "Holding Shift while driving does not block held O acceleration.");
            InputKeys.Clear(); Frame();
            _inputChat = true; InputKeys.Add(Keys.O); Frame();
            _inputChat = false; _inputPause = true; Frame(); _inputPause = false;
            _inputHostMenu = true; Frame(); _inputHostMenu = false;
            Assert(_inputBoostFrames == 3, "Releasing O, chat focus and GTA pause block the actual held-boost path.");
            Release();

            // Both independent transpilers target Main.OnKeyDown in production.
            passenger.GetMethod("Install", Static)!.Invoke(null, new object[] { new Func<bool>(() => true) });
            main.Visible = true; InputHeld.Add((0, 200));
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(_inputPauseActivations == 0 && PatchProcessor.GetCurrentInstructions(hostKey).Any(code =>
                code.operand is MethodInfo called && called.DeclaringType == passenger && called.Name == "IgnorePassengerKey"),
                "The real host executes with both menu and passenger transpilers while retaining the removed passenger-key branch.");
            Guard("Uninstall");
            Assert(Harmony.GetPatchInfo(hostKey)!.Owners.Contains("friendslegacy.ragecoop.passengerseat") &&
                !Harmony.GetPatchInfo(hostKey)!.Owners.Contains("friendslegacy.ragecoop.menuinput"),
                "Removing menu input preserves the real passenger-key transpiler on the same host method.");
            Guard("Install", owns, processing, typing);
            passenger.GetMethod("Uninstall", Static)!.Invoke(null, Array.Empty<object>());
            Assert(Harmony.GetPatchInfo(hostKey)!.Owners.Contains("friendslegacy.ragecoop.menuinput"),
                "Removing passenger entry preserves the host pause guard installed after it.");
            main.Visible = true;
            InputHeld.Add((0, 200));
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(_inputPauseActivations == 0, "Both installation orders preserve the resource's pause ownership.");
            Guard("Uninstall"); Guard("Uninstall");
            InputDisabled.Clear(); WorldFrame();
            hostKey.Invoke(hostInstance, new object[] { hostInstance, new KeyEventArgs(Keys.Escape) });
            Assert(_inputPauseActivations == 1 && InputDisabled.SetEquals(new[] { (0, 199), (0, 200) }),
                "Unloading the resource restores both the original world pause disable and the original keyboard force-open path.");
            Assert(!Harmony.GetPatchInfo(worldTick)!.Owners.Contains("friendslegacy.ragecoop.menuinput") &&
                Harmony.GetPatchInfo(worldTick)!.Owners.Contains(boundary.Id),
                "World pause cleanup removes only this resource's patch and preserves the independent boundary patch.");
            Console.WriteLine("Actual WorldThread/Main/input Tick checks passed: GTA owns pause press/hold/release and closing in both script/native boundary orderings; no resource activation or latch; Russian VK_P uses real ToUnicodeEx while native 200 cannot cancel text or its restored parent. Phone/shop cancellation, Enter/Esc and Xbox A/B preserved. GTA frontend processing, physical devices and runtime script ordering remain isolated.");
        }
        finally
        {
            guard.GetMethod("Uninstall", Static)!.Invoke(null, Array.Empty<object>());
            passenger.GetMethod("Uninstall", Static)!.Invoke(null, Array.Empty<object>());
            _inputWorldTick = false; _inputPause = false; _inputConnected = true; InputReleases.Clear();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
        }
    }

    private static NativeMenu BareInputMenu(int itemCount = 3)
    {
        var menu = (NativeMenu)FormatterServices.GetUninitializedObject(typeof(NativeMenu));
        var items = new List<NativeItem>();
        for (int index = 0; index < itemCount; index++) items.Add((NativeItem)FormatterServices.GetUninitializedObject(typeof(NativeItem)));
        typeof(NativeMenu).GetField("<Items>k__BackingField", Instance)!.SetValue(menu, items);
        typeof(NativeMenu).GetField("visibleItems", Instance)!.SetValue(menu, new List<NativeItem>());
        typeof(NativeMenu).GetField("descriptionText", Instance)!.SetValue(menu, new LemonUI.Elements.ScaledText(System.Drawing.PointF.Empty, string.Empty));
        typeof(NativeMenu).GetField("nameText", Instance)!.SetValue(menu, new LemonUI.Elements.ScaledText(System.Drawing.PointF.Empty, string.Empty));
        typeof(NativeMenu).GetField("<Buttons>k__BackingField", Instance)!.SetValue(menu, BareMenuNavigationButtons());
        typeof(NativeMenu).GetField("maxItems", Instance)!.SetValue(menu, 10);
        typeof(NativeMenu).GetField("<AcceptsInput>k__BackingField", Instance)!.SetValue(menu, true);
        typeof(NativeMenu).GetField("<DisableControls>k__BackingField", Instance)!.SetValue(menu, true);
        typeof(NativeMenu).GetField("<RequiredControls>k__BackingField", Instance)!.SetValue(menu, new List<GTA.Control>());
        return menu;
    }

    private sealed class InputTickBoundaryReached : Exception { }

    private static long StopBeforeInputTickGameplay() => throw new InputTickBoundaryReached();

    private static IEnumerable<CodeInstruction> IsolateInputTickBoundaries(IEnumerable<CodeInstruction> original)
    {
        bool stopped = false;
        foreach (CodeInstruction code in IsolateInputBoundaries(original))
        {
            if (!stopped && code.operand is MethodInfo method && method.DeclaringType == typeof(Stopwatch) && method.Name == nameof(Stopwatch.GetTimestamp))
            {
                code.operand = typeof(Program).GetMethod(nameof(StopBeforeInputTickGameplay), Static)!;
                stopped = true;
            }
            yield return code;
        }
    }

    // Isolate focus/window ownership only. Translation itself calls the real
    // Win32 ToUnicodeEx used by the built resource, with the loaded ru layout.
    private static bool InputKeyboardTextBoundary(KeyEventArgs __0, ref string __result)
    {
        if (__0.KeyCode != Keys.P) { __result = string.Empty; return false; }
        MethodInfo translate = Resource.GetType("RageCoopAdmin.Client.UnicodeKeyboardTextReader", true)!.GetMethod("Translate", Static)!;
        var state = new byte[256];
        state[(int)Keys.P] = 0x80;
        __result = (string)translate.Invoke(null, new object[] { (uint)Keys.P, state, _inputTextLayout })!;
        Assert(__result == "з", "The actual resource's Win32 ToUnicodeEx translates Russian VK_P to з.");
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr InputLoadKeyboardLayout(string layoutId, uint flags);

    private static IEnumerable<CodeInstruction> IsolateInputBoundaries(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method)
            {
                Type? type = method.DeclaringType;
                bool native = type == typeof(Function) && method.Name == "Call";
                bool boundary = native || (type == typeof(InputArgument) && method.Name == "op_Implicit" && method.GetParameters()[0].ParameterType == typeof(string)) ||
                    type == typeof(Controls) ||
                    (type == typeof(Game) && method.Name is "IsKeyPressed" or "IsControlPressed" or "IsControlJustPressed" or "DisableControlThisFrame" or "get_GameTime" or "get_Player" or "get_IsLoading") ||
                    (type == typeof(Player) && method.Name == "get_Character") ||
                    (type == typeof(Ped) && method.Name == "get_CurrentVehicle") ||
                    (type == typeof(Entity) && method.Name == "get_IsDead") ||
                    (type != null && typeof(Entity).IsAssignableFrom(type) && method.Name == "Exists") ||
                    (type == typeof(Model) && method.Name == "get_IsCar") ||
                    (type == typeof(GTA.UI.Screen) && method.Name == "get_IsFadedIn") ||
                    (type == typeof(Wanted) && method.Name == "get_WantedLevel") ||
                    (type?.FullName is "RageCoop.Client.Scripting.API" or "RageCoop.Client.Networking" && method.Name.StartsWith("get_Is", StringComparison.Ordinal)) ||
                    (type?.FullName == "RageCoop.Client.Chat" && method.Name == "get_Focused") ||
                    (type?.FullName == "RageCoop.Client.Voice" && method.Name == "WasInitialized");
                if (boundary) { code.opcode = OpCodes.Call; code.operand = InputStub(method); }
            }
            yield return code;
        }
    }

    private static DynamicMethod InputStub(MethodInfo target)
    {
        if (InputStubs.TryGetValue(target, out DynamicMethod? found)) return found;
        Type[] args = target.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (!target.IsStatic) args = new[] { target.DeclaringType! }.Concat(args).ToArray();
        var stub = new DynamicMethod("MenuInputBoundary" + InputStubs.Count, target.ReturnType, args, typeof(Program), true);
        ILGenerator il = stub.GetILGenerator();
        if (target.DeclaringType == typeof(Function))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, args.Length - 1); il.Emit(OpCodes.Newarr, typeof(InputArgument));
            for (int index = 1; index < args.Length; index++)
            { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, index - 1); il.Emit(OpCodes.Ldarg, index); il.Emit(OpCodes.Stelem_Ref); }
            string handler = target.ReturnType == typeof(bool) ? nameof(InputNativeBool) : target.ReturnType == typeof(int) ? nameof(InputNativeInt) :
                target.ReturnType == typeof(float) ? nameof(VehicleInputNativeFloat) : nameof(InputNativeWrite);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(handler, Static)!);
        }
        else if (target.Name == "IsKeyPressed")
        { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(InputKey), Static)!); }
        else if (target.Name is "IsControlPressed" or "IsControlJustPressed")
        {
            il.Emit(OpCodes.Ldarg_0);
            if (target.DeclaringType == typeof(Controls)) il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(target.DeclaringType == typeof(Controls) ?
                (target.Name == "IsControlPressed" ? nameof(InputControlHeld) : nameof(InputControlEdge)) :
                (target.Name == "IsControlPressed" ? nameof(InputLegacyHeld) : nameof(InputLegacyEdge)), Static)!);
        }
        else if (target.Name is "IsDisabledControlPressed" or "IsDisabledControlJustPressed" or "DisableControlActionThisFrame" or "EnableControlActionThisFrame")
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(target.Name == "DisableControlActionThisFrame" ? nameof(InputControlDisable) :
                target.Name == "EnableControlActionThisFrame" ? nameof(InputControlEnable) :
                target.Name == "IsDisabledControlPressed" ? nameof(InputDisabledControlHeld) : nameof(InputDisabledControlEdge), Static)!);
        }
        else if (target.Name == "DisableControlThisFrame")
        { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(InputControlDisable), Static)!); }
        else if (target.Name == "get_Player") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(InputPlayer), Static)!);
        else if (target.Name == "get_Character") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(VehicleInputPed), Static)!);
        else if (target.Name == "get_CurrentVehicle") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(VehicleInputCar), Static)!);
        else if (target.Name is "Exists" or "get_IsFadedIn") il.Emit(OpCodes.Ldc_I4_1);
        else if (target.Name == "get_IsCar") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_vehicleInputCarModel), Static)!);
        else if (target.Name is "get_IsChatFocused" or "get_Focused") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_inputChat), Static)!);
        else if (target.Name == "get_IsMenuVisible") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_inputHostMenu), Static)!);
        else if (target.Name == "get_IsOnServer") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_inputConnected), Static)!);
        else if (target.ReturnType == typeof(InputArgument))
        { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U8); il.Emit(OpCodes.Newobj, typeof(InputArgument).GetConstructor(new[] { typeof(ulong) })!); }
        else il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret); InputStubs.Add(target, stub); return stub;
    }

    private static int InputArgumentValue(InputArgument argument) => unchecked((int)(ulong)typeof(InputArgument).GetField("_data", Instance)!.GetValue(argument)!);
    private static bool InputKey(Keys key) => InputKeys.Contains(key);
    private static bool InputLegacyHeld(GTA.Control action) => InputHeld.Contains((0, (int)action));
    private static bool InputLegacyEdge(GTA.Control action) => InputEdges.Contains((0, (int)action));
    private static bool InputControlHeld(ControlType group, ControlAction action) => !InputDisabled.Contains(((int)group, (int)action)) && InputDisabledControlHeld(group, action);
    private static bool InputControlEdge(ControlType group, ControlAction action) => !InputDisabled.Contains(((int)group, (int)action)) && InputDisabledControlEdge(group, action);
    private static bool InputDisabledControlHeld(ControlType group, ControlAction action) => InputHeld.Contains(((int)group, (int)action));
    private static bool InputDisabledControlEdge(ControlType group, ControlAction action) => InputEdges.Contains(((int)group, (int)action));
    private static void InputControlDisable(ControlType group, ControlAction action) => InputDisabled.Add(((int)group, (int)action));
    private static void InputControlEnable(ControlType group, ControlAction action) => InputDisabled.Remove(((int)group, (int)action));
    private static bool InputNativeBool(Hash hash, InputArgument[] arguments)
    {
        if (hash == Hash.IS_PAUSE_MENU_ACTIVE) return _inputPause;
        if (hash == Hash.IS_USING_KEYBOARD_AND_MOUSE) return _inputKeyboardMode;
        if (hash is Hash.IS_CONTROL_PRESSED or Hash.IS_DISABLED_CONTROL_PRESSED or Hash.IS_CONTROL_JUST_PRESSED or
            Hash.IS_DISABLED_CONTROL_JUST_PRESSED or Hash.IS_CONTROL_JUST_RELEASED or Hash.IS_DISABLED_CONTROL_JUST_RELEASED)
        {
            var control = (InputArgumentValue(arguments[0]), InputArgumentValue(arguments[1]));
            if (hash is Hash.IS_CONTROL_PRESSED or Hash.IS_CONTROL_JUST_PRESSED or Hash.IS_CONTROL_JUST_RELEASED && InputDisabled.Contains(control)) return false;
            return (hash is Hash.IS_CONTROL_JUST_RELEASED or Hash.IS_DISABLED_CONTROL_JUST_RELEASED ? InputReleases :
                hash is Hash.IS_CONTROL_JUST_PRESSED or Hash.IS_DISABLED_CONTROL_JUST_PRESSED ? InputEdges : InputHeld).Contains(control);
        }
        return VehicleInputNativeBool(hash, arguments);
    }
    private static int InputNativeInt(Hash hash, InputArgument[] arguments) => hash switch
    {
        Hash.UPDATE_ONSCREEN_KEYBOARD => 1,
        Hash.GET_HASH_KEY => 0,
        _ => VehicleInputNativeInt(hash, arguments)
    };
    private static void InputNativeWrite(Hash hash, InputArgument[] arguments)
    {
        if (hash is Hash.DISABLE_CONTROL_ACTION or Hash.SET_INPUT_EXCLUSIVE) InputDisabled.Add((InputArgumentValue(arguments[0]), InputArgumentValue(arguments[1])));
        else if (hash == Hash.ENABLE_CONTROL_ACTION) InputDisabled.Remove((InputArgumentValue(arguments[0]), InputArgumentValue(arguments[1])));
        else if (hash == Hash.ACTIVATE_FRONTEND_MENU)
        {
            _inputPauseActivations++; _inputPause = true;
            _inputActivationPauses = InputArgumentValue(arguments[1]) != 0;
            _inputActivationComponent = InputArgumentValue(arguments[2]);
        }
        else if (hash == Hash.SET_GAME_PAUSED)
        {
            Assert(_inputWorldTick && _inputPause && InputArgumentValue(arguments[0]) == 0,
                "Keeping the connected simulation running never changes the frontend menu's active state.");
            _inputWorldUnpauses++;
        }
        else if (_inputWorldTick) InputWorldWrites.Add(hash);
        else if (hash == Hash.SET_VEHICLE_FORWARD_SPEED) VehicleInputNativeWrite(hash, arguments);
        else throw new InvalidOperationException("Unexpected input native write " + hash);
    }
    private static bool CaptureInputBoost(float __0) { _inputBoostFrames++; _inputBoostElapsed = __0; return false; }
    private static bool CaptureInputRepair() { _inputRepairs++; return false; }
    private static bool IsolateTextWidth(ref float __result) { __result = 0f; return false; }
    private static bool InputMenuDeviceBoundary(ref bool __result) { __result = !_inputKeyboardMode; return false; }
    private static bool InputConnectionState(ref bool __result) { __result = _inputConnected; return false; }
}
