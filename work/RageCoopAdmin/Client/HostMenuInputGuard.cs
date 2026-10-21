using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GTA;
using GTA.Input;
using GTA.Native;
using HarmonyLib;
using LemonUI.Menus;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// RageCoop disables GTA's pause actions and replays them on keyboard events.
// Give their complete press/hold/release lifecycle back to GTA. Only reserve
// cancellation while our own interface owns it; never replay native input.
internal static class HostMenuInputGuard
{
    private const string PatchId = "friendslegacy.ragecoop.menuinput";
    private static Harmony? _harmony;
    private static MethodInfo? _target;
    private static MethodInfo? _worldTarget;
    private static Func<bool>? _ownsCancel;
    private static Func<bool>? _processingResourceMenus;
    private static Func<bool>? _ownsTextInput;
    private static MethodInfo? _lemonPressed;
    private static MethodInfo? _lemonJustPressed;

    internal static bool TextInputAvailable => _harmony != null && _ownsTextInput != null;

    internal static bool Install(Func<bool> ownsCancel, Func<bool> processingResourceMenus, Func<bool>? ownsTextInput = null)
    {
        if (_harmony != null) return true;
        if (ownsCancel == null) throw new ArgumentNullException(nameof(ownsCancel));
        if (processingResourceMenus == null) throw new ArgumentNullException(nameof(processingResourceMenus));
        Type? main = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Main", false);
        MethodInfo? target = main == null ? null : AccessTools.DeclaredMethod(main, "OnKeyDown", new[] { typeof(object), typeof(KeyEventArgs) });
        if (target == null || !MatchesHostPauseReads(target, PatchProcessor.GetOriginalInstructions(target))) return false;
        Type? world = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.WorldThread", false);
        MethodInfo? worldTarget = world == null ? null : AccessTools.DeclaredMethod(world, "OnTick", new[] { typeof(object), typeof(EventArgs) });
        if (worldTarget == null || !MatchesWorldPauseDisables(worldTarget, PatchProcessor.GetOriginalInstructions(worldTarget))) return false;
        Type? lemonControls = typeof(NativeMenu).Assembly.GetType("LemonUI.Controls", false);
        MethodInfo? pressed = lemonControls == null ? null : AccessTools.DeclaredMethod(lemonControls, "IsPressed", new[] { typeof(GTA.Control) });
        MethodInfo? justPressed = lemonControls == null ? null : AccessTools.DeclaredMethod(lemonControls, "IsJustPressed", new[] { typeof(GTA.Control) });
        if (!MatchesLemonRead(pressed, Hash.IS_DISABLED_CONTROL_PRESSED) ||
            !MatchesLemonRead(justPressed, Hash.IS_DISABLED_CONTROL_JUST_PRESSED)) return false;
        var harmony = new Harmony(PatchId);
        _ownsCancel = ownsCancel;
        _processingResourceMenus = processingResourceMenus;
        _ownsTextInput = ownsTextInput;
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HostMenuInputGuard), nameof(AllowHostKey)),
                transpiler: new HarmonyMethod(typeof(HostMenuInputGuard), nameof(ReplacePauseReads)));
            harmony.Patch(worldTarget, transpiler: new HarmonyMethod(typeof(HostMenuInputGuard), nameof(ReplaceWorldPauseDisables)));
            harmony.Patch(pressed, postfix: new HarmonyMethod(typeof(HostMenuInputGuard), nameof(ReadFrontendHeld)));
            harmony.Patch(justPressed, postfix: new HarmonyMethod(typeof(HostMenuInputGuard), nameof(ReadFrontendPress)));
        }
        catch
        {
            harmony.Unpatch(target, HarmonyPatchType.Transpiler, PatchId);
            harmony.Unpatch(target, HarmonyPatchType.Prefix, PatchId);
            harmony.Unpatch(worldTarget, HarmonyPatchType.Transpiler, PatchId);
            harmony.Unpatch(pressed, HarmonyPatchType.Postfix, PatchId);
            harmony.Unpatch(justPressed, HarmonyPatchType.Postfix, PatchId);
            _ownsCancel = null;
            _processingResourceMenus = null;
            _ownsTextInput = null;
            throw;
        }
        _lemonPressed = pressed;
        _lemonJustPressed = justPressed;
        _target = target;
        _worldTarget = worldTarget;
        _harmony = harmony;
        return true;
    }

    internal static void Uninstall()
    {
        if (_harmony != null && _target != null) _harmony.Unpatch(_target, HarmonyPatchType.Transpiler, PatchId);
        if (_harmony != null && _target != null) _harmony.Unpatch(_target, HarmonyPatchType.Prefix, PatchId);
        if (_harmony != null && _worldTarget != null) _harmony.Unpatch(_worldTarget, HarmonyPatchType.Transpiler, PatchId);
        if (_harmony != null && _lemonPressed != null) _harmony.Unpatch(_lemonPressed, HarmonyPatchType.Postfix, PatchId);
        if (_harmony != null && _lemonJustPressed != null) _harmony.Unpatch(_lemonJustPressed, HarmonyPatchType.Postfix, PatchId);
        _harmony = null;
        _target = null;
        _worldTarget = null;
        _ownsCancel = null;
        _processingResourceMenus = null;
        _ownsTextInput = null;
        _lemonPressed = null;
        _lemonJustPressed = null;
    }

    // Main forwards resource key events through a separate SHVDN subscriber.
    // While our field owns typing, T/F9/PTT/pause must not run in the earlier host handler.
    private static bool AllowHostKey() => _ownsTextInput?.Invoke() != true;

    private static bool MatchesLemonRead(MethodInfo? method, Hash hash) => method?.ReturnType == typeof(bool) &&
        PatchProcessor.GetOriginalInstructions(method).Count(code => code.LoadsConstant(unchecked((long)hash))) == 1;

    internal static bool MatchesHostPauseReads(MethodInfo target, IList<CodeInstruction> instructions)
    {
        if (target.DeclaringType?.FullName != "RageCoop.Client.Main" || target.Name != "OnKeyDown" || target.ReturnType != typeof(void)) return false;
        MethodInfo? read = AccessTools.DeclaredMethod(typeof(Game), "IsControlPressed", new[] { typeof(GTA.Control) });
        if (read == null) return false;
        int[] controls = instructions.Select((code, index) => code.Calls(read) && index > 0 &&
            (instructions[index - 1].LoadsConstant(199) || instructions[index - 1].LoadsConstant(200))
                ? (instructions[index - 1].LoadsConstant(199) ? 199 : 200) : -1).Where(value => value >= 0).ToArray();
        return controls.SequenceEqual(new[] { 199, 200 });
    }

    private static IEnumerable<CodeInstruction> ReplacePauseReads(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        var instructions = original.Select(code => new CodeInstruction(code)).ToList();
        if (__originalMethod is not MethodInfo method || !MatchesHostPauseReads(method, instructions))
            throw new InvalidOperationException("Unsupported RageCoop host pause input contract.");
        MethodInfo read = AccessTools.DeclaredMethod(typeof(Game), "IsControlPressed", new[] { typeof(GTA.Control) });
        for (int index = 1; index < instructions.Count; index++)
        {
            if (instructions[index].Calls(read) &&
                (instructions[index - 1].LoadsConstant(199) || instructions[index - 1].LoadsConstant(200)))
                instructions[index].operand = AccessTools.DeclaredMethod(typeof(HostMenuInputGuard), nameof(ReadHostPause));
        }
        return instructions;
    }

    internal static bool MatchesWorldPauseDisables(MethodInfo target, IList<CodeInstruction> instructions)
    {
        if (target.DeclaringType?.FullName != "RageCoop.Client.WorldThread" || target.Name != "OnTick" || target.ReturnType != typeof(void)) return false;
        MethodInfo? disable = AccessTools.DeclaredMethod(typeof(Game), "DisableControlThisFrame", new[] { typeof(GTA.Control) });
        if (disable == null) return false;
        int[] controls = instructions.Select((code, index) => code.Calls(disable) && index > 0 &&
            (instructions[index - 1].LoadsConstant(199) || instructions[index - 1].LoadsConstant(200))
                ? (instructions[index - 1].LoadsConstant(199) ? 199 : 200) : -1).Where(value => value >= 0).ToArray();
        return controls.SequenceEqual(new[] { 199, 200 }) && instructions.Count(code => code.Calls(disable)) == 2;
    }

    private static IEnumerable<CodeInstruction> ReplaceWorldPauseDisables(IEnumerable<CodeInstruction> original, MethodBase __originalMethod)
    {
        var instructions = original.Select(code => new CodeInstruction(code)).ToList();
        if (__originalMethod is not MethodInfo method || !MatchesWorldPauseDisables(method, instructions))
            throw new InvalidOperationException("Unsupported RageCoop world pause input contract.");
        MethodInfo disable = AccessTools.DeclaredMethod(typeof(Game), "DisableControlThisFrame", new[] { typeof(GTA.Control) });
        for (int index = 1; index < instructions.Count; index++)
        {
            if (instructions[index].Calls(disable) &&
                (instructions[index - 1].LoadsConstant(199) || instructions[index - 1].LoadsConstant(200)))
                instructions[index].operand = AccessTools.DeclaredMethod(typeof(HostMenuInputGuard), nameof(ProcessWorldPauseInput));
        }
        return instructions;
    }

    // The native frontend is the only owner outside our interface. Main's old
    // disabled-control reads must not bypass phone/shop cancellation or replay
    // a keyboard event which GTA has already processed.
    private static bool ReadHostPause(GTA.Control _) => false;

    private static void ProcessWorldPauseInput(GTA.Control action)
    {
        // The 199 call site is unconditional; the 200 call site depends on the
        // host's DisableAlternatePause setting. Dispatch both from the first.
        if ((int)action != 199) return;
        if (_ownsCancel?.Invoke() == true || _ownsTextInput?.Invoke() == true)
            DisablePauseControls();
        // Keep the connected world running as RageCoop's previous non-pausing
        // frontend did. This native changes simulation pause, not menu state.
        if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE))
            Function.Call(Hash.SET_GAME_PAUSED, false);
    }

    private static void DisablePauseControls()
    {
        Controls.DisableControlActionThisFrame(ControlType.PlayerControl, ControlAction.FrontendPause);
        Controls.DisableControlActionThisFrame(ControlType.PlayerControl, ControlAction.FrontendPauseAlternate);
        Controls.DisableControlActionThisFrame(ControlType.FrontendControl, ControlAction.FrontendPause);
        Controls.DisableControlActionThisFrame(ControlType.FrontendControl, ControlAction.FrontendPauseAlternate);
    }

    private static void ReadFrontendHeld(GTA.Control __0, ref bool __result)
    {
        if (SuppressTextControl(__0)) { __result = false; return; }
        if (!__result && _processingResourceMenus?.Invoke() == true)
        {
            ControlAction action = (ControlAction)(int)__0;
            ControlAction equivalent = FrontendEquivalent(action);
            __result = Controls.IsControlPressed(ControlType.FrontendControl, action) ||
                Controls.IsDisabledControlPressed(ControlType.FrontendControl, action) ||
                (equivalent != action && (Controls.IsControlPressed(ControlType.FrontendControl, equivalent) ||
                    Controls.IsDisabledControlPressed(ControlType.FrontendControl, equivalent)));
        }
    }

    private static void ReadFrontendPress(GTA.Control __0, ref bool __result)
    {
        if (SuppressTextControl(__0)) { __result = false; return; }
        if (!__result && _processingResourceMenus?.Invoke() == true)
        {
            ControlAction action = (ControlAction)(int)__0;
            ControlAction equivalent = FrontendEquivalent(action);
            __result = Controls.IsControlJustPressed(ControlType.FrontendControl, action) ||
                Controls.IsDisabledControlJustPressed(ControlType.FrontendControl, action) ||
                (equivalent != action && (Controls.IsControlJustPressed(ControlType.FrontendControl, equivalent) ||
                    Controls.IsDisabledControlJustPressed(ControlType.FrontendControl, equivalent)));
        }
    }

    private static bool SuppressTextControl(GTA.Control action)
    {
        if (_processingResourceMenus?.Invoke() != true || _ownsTextInput?.Invoke() != true) return false;
        // P (Russian з) and other gameplay bindings remain text. Keyboard
        // Enter/Esc are handled by KeyDown, regardless of native event order;
        // controller A/B still use LemonUI's ordinary accept/cancel path.
        if ((int)action is 156 or 199 or 200) return true;
        return (int)action is 176 or 177 or 201 or 202 &&
            Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 2);
    }

    private static ControlAction FrontendEquivalent(ControlAction action) => action switch
    {
        ControlAction.PhoneUp => ControlAction.FrontendUp,
        ControlAction.PhoneDown => ControlAction.FrontendDown,
        ControlAction.PhoneLeft => ControlAction.FrontendLeft,
        ControlAction.PhoneRight => ControlAction.FrontendRight,
        ControlAction.PhoneCancel => ControlAction.FrontendCancel,
        _ => action
    };
}
