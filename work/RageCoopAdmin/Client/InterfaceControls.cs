using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Input;
using GTA.Native;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private InterfaceSettings _interfaceSettings = new InterfaceSettings();
    private InterfaceSettingsStore? _interfaceStore;
    private bool _interfaceSettingsUnavailable;
    private bool _updatingInterfaceSettings;
    private Action? _refreshInterfaceSettings;
    private int? _unsavedRepairShortcut;
    private bool? _unsavedGamepadShortcuts;
    private bool? _unsavedKeyboardBoost;
#if !PUBLIC_RELEASE
    private bool? _unsavedShowSpeedometer;
#endif
    private bool _gettingUserInput;
    private ResourceTextInput? _textInput;
    private readonly MenuInputReleaseGuard _textInputActivationGuard = new MenuInputReleaseGuard();
    private bool _textInputActivationPending;
    private long _lastMenuShortcut;
    private long _lastRepairShortcut;
    private long _lastBoostFrame;
    private readonly ShortcutLatch _shortcutLatch = new ShortcutLatch();
    private readonly BumperShortcutChord _gamepadChord = new BumperShortcutChord();
    private readonly MenuInputReleaseGuard _cancelReleaseGuard = new MenuInputReleaseGuard();
    private readonly MenuInputReleaseGuard _shortcutMenuInputGuard = new MenuInputReleaseGuard();
    private readonly MenuInputReleaseGuard _cancelMenuInputGuard = new MenuInputReleaseGuard();
    private readonly List<NativeMenu> _suspendedMenuInputs = new List<NativeMenu>();
    private bool _menuCancelRequested;
    private bool _reserveMenuCancel;
    private bool _cancelNavigationCaptured;
    private bool _processingResourceMenus;
    private bool _menuCancelHeld;

    private void InitializeInterface()
    {
        try
        {
            _interfaceStore = new InterfaceSettingsStore(CurrentResource.DataFolder);
            _interfaceSettings = _interfaceStore.Load();
        }
        catch (Exception ex) when (IsInterfaceStorageError(ex))
        {
            _interfaceSettingsUnavailable = true;
            Logger.Warning("Coop Control: interface settings could not be loaded: " + ex.GetType().Name);
            Notification.PostTicker("~o~Настройки:~s~ не удалось прочитать файл. Изменения будут действовать до выхода.", false);
        }
    }

    private void BuildInterfaceMenu()
    {
        if (_mainMenu == null || _pool == null)
        {
            return;
        }

        NativeMenu settings = CreateMenu("Совместная игра", "Настройки");
        _mainMenu.AddSubMenu(settings);
        _pool.Add(settings);
        var repairKey = new NativeCheckboxItem("Ремонт клавишей K",
            "Чинить транспорт, в котором вы сидите, или ближайший пешком клавишей K. Работает при закрытом меню.",
            _interfaceSettings.RepairShortcut == 0);
        repairKey.CheckboxChanged += (_, _) =>
        {
            if (!_updatingInterfaceSettings)
                PersistInterfaceSettings(value => value.RepairShortcut = repairKey.Checked ? 0 : 1);
        };
        settings.Add(repairKey);
        var gamepad = new NativeCheckboxItem("Сочетания на геймпаде",
            "Удерживайте LB + RB: ↓ — меню, ↑ — ремонт. В меню: крестовина, A — выбрать, B — назад. На PlayStation: L1 + R1.",
            _interfaceSettings.GamepadShortcuts);
        gamepad.CheckboxChanged += (_, _) =>
        {
            if (!_updatingInterfaceSettings)
                PersistInterfaceSettings(value => value.GamepadShortcuts = gamepad.Checked);
        };
        settings.Add(gamepad);
        var boost = new NativeCheckboxItem("Разгон клавишей O",
            "Удерживайте O для ускорения. Отпустите, чтобы прекратить разгон. Только за рулём автомобиля.",
            _interfaceSettings.KeyboardBoost);
        boost.CheckboxChanged += (_, _) =>
        {
            if (!_updatingInterfaceSettings)
                PersistInterfaceSettings(value => value.KeyboardBoost = boost.Checked);
        };
        settings.Add(boost);
#if !PUBLIC_RELEASE
        var speedometer = new NativeCheckboxItem("Спидометр",
            "Показывать скорость, когда вы в транспорте.", _interfaceSettings.ShowSpeedometer);
        speedometer.CheckboxChanged += (_, _) =>
        {
            if (!_updatingInterfaceSettings)
                PersistInterfaceSettings(value => value.ShowSpeedometer = speedometer.Checked);
        };
        settings.Add(speedometer);
#endif
        _refreshInterfaceSettings = () =>
        {
            _updatingInterfaceSettings = true;
            try
            {
                repairKey.Checked = _interfaceSettings.RepairShortcut == 0;
                gamepad.Checked = _interfaceSettings.GamepadShortcuts;
                boost.Checked = _interfaceSettings.KeyboardBoost;
#if !PUBLIC_RELEASE
                speedometer.Checked = _interfaceSettings.ShowSpeedometer;
#endif
            }
            finally { _updatingInterfaceSettings = false; }
        };
    }

    private void PersistInterfaceSettings(Action<InterfaceSettings> change)
    {
        int repair = _interfaceSettings.RepairShortcut;
        bool gamepad = _interfaceSettings.GamepadShortcuts;
        bool boost = _interfaceSettings.KeyboardBoost;
#if !PUBLIC_RELEASE
        bool speedometer = _interfaceSettings.ShowSpeedometer;
#endif
        change(_interfaceSettings);
        if (repair != _interfaceSettings.RepairShortcut) _unsavedRepairShortcut = _interfaceSettings.RepairShortcut;
        if (gamepad != _interfaceSettings.GamepadShortcuts) _unsavedGamepadShortcuts = _interfaceSettings.GamepadShortcuts;
        if (boost != _interfaceSettings.KeyboardBoost) _unsavedKeyboardBoost = _interfaceSettings.KeyboardBoost;
#if !PUBLIC_RELEASE
        if (speedometer != _interfaceSettings.ShowSpeedometer) _unsavedShowSpeedometer = _interfaceSettings.ShowSpeedometer;
#endif
        if (_interfaceSettingsUnavailable || _interfaceStore == null)
        {
            return;
        }

        try
        {
            // Keep temporary choices after a failed write. Retrying another
            // checkbox must not undo them when it reads the latest disk state.
            // Only locally edited settings replace values from another writer.
            _interfaceSettings = _interfaceStore.Update(value =>
            {
                if (_unsavedRepairShortcut.HasValue) value.RepairShortcut = _unsavedRepairShortcut.Value;
                if (_unsavedGamepadShortcuts.HasValue) value.GamepadShortcuts = _unsavedGamepadShortcuts.Value;
                if (_unsavedKeyboardBoost.HasValue) value.KeyboardBoost = _unsavedKeyboardBoost.Value;
#if !PUBLIC_RELEASE
                if (_unsavedShowSpeedometer.HasValue) value.ShowSpeedometer = _unsavedShowSpeedometer.Value;
#endif
            });
            _unsavedRepairShortcut = null;
            _unsavedGamepadShortcuts = null;
            _unsavedKeyboardBoost = null;
#if !PUBLIC_RELEASE
            _unsavedShowSpeedometer = null;
#endif
            _refreshInterfaceSettings?.Invoke();
        }
        catch (Exception ex) when (IsInterfaceStorageError(ex))
        {
            Logger.Warning("Coop Control: interface settings could not be saved: " + ex.GetType().Name);
            Notification.PostTicker("~o~Настройки:~s~ не удалось сохранить. Изменения действуют до выхода.", false);
        }
    }

    private static bool IsInterfaceStorageError(Exception ex) =>
        ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
        ex is ArgumentException || ex is System.Xml.XmlException;

    private bool IsInputBlocked() =>
        _gettingUserInput || API.IsChatFocused || API.IsMenuVisible ||
        Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE) ||
        Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 0;

    private void HandleMenuKey(KeyEventArgs e)
    {
        if (_gettingUserInput)
        {
            if (QuickShortcutKeys.Contains(e.KeyCode)) _shortcutLatch.TryPress((int)e.KeyCode);
            // Mac Delete can arrive as Backspace or forward Delete. Either
            // may also emit a native cancel edge; consume the complete press.
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Enter)
                _textInputActivationPending = true;
            _textInput?.UpdateInputMode(controller: false);
            _textInput?.HandleKey(e);
            return;
        }
        if (e.KeyCode == Keys.Escape && _pool?.AreAnyVisible == true)
        {
            // The host guard prevents the earlier pause branch; process one Back
            // on Tick so keyboard and controller cancellation share one path.
            if (_shortcutLatch.TryPress((int)Keys.Escape)) _menuCancelRequested = true;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (IsInputBlocked())
        {
            return;
        }

        InterfaceShortcut shortcut = InterfaceShortcutMatcher.Match(e.KeyCode, e.Modifiers, _interfaceSettings);
        bool menu = shortcut == InterfaceShortcut.Menu;
        bool repair = shortcut == InterfaceShortcut.Repair;
        bool boost = shortcut == InterfaceShortcut.Boost;
        long now = Stopwatch.GetTimestamp();
        if ((menu || repair || boost) && !_shortcutLatch.TryPress((int)e.KeyCode))
        {
            return;
        }

        if (menu && ShortcutReady(ref _lastMenuShortcut, now, 0.35))
        {
            ToggleMainMenu();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (repair && _pool?.AreAnyVisible != true && API.IsOnServer &&
                 ShortcutReady(ref _lastRepairShortcut, now, 1.0))
        {
            RequestRepair();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (boost && _pool?.AreAnyVisible != true)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e) => _shortcutLatch.Release((int)e.KeyCode);

    private void ToggleMainMenu()
    {
        if (!API.IsOnServer)
        {
            Notification.PostTicker("~o~Совместная игра:~s~ сначала подключитесь к серверу.", false);
            return;
        }

        if (_pool == null || _mainMenu == null)
        {
            return;
        }

        if (_pool.AreAnyVisible)
        {
            _pool.HideAll();
            return;
        }

        SetWantedItem(Game.Player.Wanted.WantedLevel);
        if (_currentTimeItem != null)
        {
            _currentTimeItem.AltTitle = FormatTime(GTA.Chrono.GameClock.Hour, GTA.Chrono.GameClock.Minute);
        }

        _mainMenu.SelectedIndex = 0;
        _mainMenu.Visible = true;
    }

    private static bool IsPhoneInUse(Ped player) =>
        Function.Call<bool>(Hash.IS_PED_RUNNING_MOBILE_PHONE_TASK, player.Handle) ||
        Function.Call<bool>(Hash.IS_MOBILE_PHONE_CALL_ONGOING);

    private void BoostOwnVehicle(float elapsedSeconds)
    {
        long now = Stopwatch.GetTimestamp();
        Ped player = Game.Player.Character;
        if (!API.IsOnServer || player == null || !player.Exists() || player.IsDead)
        {
            TraceKeyboardBoost(now, "unavailable-player-or-session");
            return;
        }
        if (!Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player.Handle))
        {
            TraceKeyboardBoost(now, "disabled-player-control");
            return;
        }
        if (IsPhoneInUse(player))
        {
            TraceKeyboardBoost(now, "phone");
            return;
        }

        int vehicle = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_IN, player.Handle, false);
        if (vehicle == 0 || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, vehicle))
        {
            TraceKeyboardBoost(now, "no-current-vehicle", vehicle);
            return;
        }
        if (Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, (int)VehicleSeat.Driver, false) != player.Handle)
        {
            TraceKeyboardBoost(now, "not-driver", vehicle);
            return;
        }

        // Driving is the game authority for this local physics action. The
        // engine can publish its vehicle record later or transfer it separately;
        // neither is a prerequisite for accelerating the actual driver vehicle.
        Model model = new Model(Function.Call<int>(Hash.GET_ENTITY_MODEL, vehicle));
        if (!model.IsCar)
        {
            TraceKeyboardBoost(now, "not-car", vehicle, model.Hash);
            return;
        }

        float currentSpeed = Function.Call<float>(Hash.GET_ENTITY_SPEED, vehicle);
        if (!Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, vehicle))
        {
            TraceKeyboardBoost(now, "not-on-all-wheels", vehicle, model.Hash, currentSpeed);
            return;
        }
        if (Function.Call<bool>(Hash.IS_ENTITY_IN_WATER, vehicle))
        {
            TraceKeyboardBoost(now, "in-water", vehicle, model.Hash, currentSpeed);
            return;
        }
        if (KeyboardVehicleBoost.TryGetForwardSpeed(true, currentSpeed, elapsedSeconds, out float forwardSpeed))
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, vehicle, forwardSpeed);
            TraceKeyboardBoost(now, "applied", vehicle, model.Hash, currentSpeed, forwardSpeed);
        }
        else TraceKeyboardBoost(now, "invalid-step", vehicle, model.Hash, currentSpeed);
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool ProcessQuickControls(long now)
    {
        SuppressMenuGameActions();
        bool boostKeyHeld = Game.IsKeyPressed(Keys.O);
        if (!boostKeyHeld) _boostDiagnosticReasons?.Clear();

        foreach (Keys key in QuickShortcutKeys)
        {
            if (!Game.IsKeyPressed(key))
            {
                _shortcutLatch.Release((int)key);
            }
        }

        if (IsInputBlocked())
        {
            if (boostKeyHeld)
                TraceKeyboardBoost(now, _gettingUserInput ? "text-input" : API.IsChatFocused ? "chat" :
                    API.IsMenuVisible ? "host-menu" : Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE) ? "pause-menu" : "onscreen-keyboard");
            _lastBoostFrame = now;
            _gamepadChord.CancelUntilRelease();
            return false;
        }

        float elapsedSeconds = _lastBoostFrame == 0 || now <= _lastBoostFrame ? 0f :
            (float)((now - _lastBoostFrame) / (double)Stopwatch.Frequency);
        _lastBoostFrame = now;
        bool boostHeld = boostKeyHeld && !Game.IsKeyPressed(Keys.ControlKey) &&
            !Game.IsKeyPressed(Keys.Menu);
        if (_interfaceSettings.KeyboardBoost && _pool?.AreAnyVisible != true && boostHeld)
        {
            BoostOwnVehicle(elapsedSeconds);
        }
        else if (boostKeyHeld)
            TraceKeyboardBoost(now, !_interfaceSettings.KeyboardBoost ? "disabled-setting" :
                _pool?.AreAnyVisible == true ? "resource-menu" : "ctrl-or-alt");

        if (!_interfaceSettings.GamepadShortcuts)
        {
            _gamepadChord.CancelUntilRelease();
            return false;
        }

        if (Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, 0))
        {
            _gamepadChord.Update(false, false);
            return false;
        }

        // Script/frontend actions are not guaranteed to be active in gameplay.
        // Read the gameplay bindings too, before reserving their normal meaning.
        bool leftBumper = Pressed(ControlAction.ScriptLB) || Pressed(ControlAction.FrontendLb) ||
            Pressed(ControlAction.SelectWeapon) || Pressed(ControlAction.VehicleAim) ||
            Pressed(ControlAction.VehicleFlyYawLeft);
        bool rightBumper = Pressed(ControlAction.ScriptRB) || Pressed(ControlAction.FrontendRb) ||
            Pressed(ControlAction.Cover) || Pressed(ControlAction.VehicleHandbrake) ||
            Pressed(ControlAction.VehicleFlyYawRight);
        _gamepadChord.Update(leftBumper, rightBumper);
        if (!_gamepadChord.IsActive) return false;

        // Reserve the gameplay meanings only while both bumpers are held.
        foreach (ControlAction action in GamepadConflictingActions)
        {
            Controls.DisableControlActionThisFrame(ControlType.PlayerControl, action);
            Controls.DisableControlActionThisFrame(ControlType.FrontendControl, action);
        }

        if ((JustPressed(ControlAction.ScriptPadDown) || JustPressed(ControlAction.PhoneDown) || JustPressed(ControlAction.FrontendDown) ||
             JustPressed(ControlAction.CharacterWheel) || JustPressed(ControlAction.MultiplayerInfo)) &&
            ShortcutReady(ref _lastMenuShortcut, now, 0.35))
        {
            ToggleMainMenu();
            return true;
        }

        if ((JustPressed(ControlAction.ScriptPadUp) || JustPressed(ControlAction.PhoneUp) || JustPressed(ControlAction.FrontendUp) ||
             JustPressed(ControlAction.Phone)) && _pool?.AreAnyVisible != true &&
            ShortcutReady(ref _lastRepairShortcut, now, 1.0))
        {
            RequestRepair();
            return true;
        }

        return true;
    }

    private void ProcessMenus(bool shortcutConsumed)
    {
        if (_pool == null) return;
        if (_textInput?.Active == true) _textInput.UpdateInputMode(IsMenuUsingController());
        RefreshMenuControlHints();
        if (_textInput?.Active == true && (Game.IsKeyPressed(Keys.Back) || Game.IsKeyPressed(Keys.Delete)))
            _textInputActivationPending = true;
        if (!_pool.AreAnyVisible && !shortcutConsumed && !_shortcutMenuInputGuard.IsCaptured &&
            !_textInputActivationPending && !_textInputActivationGuard.IsCaptured &&
            !_cancelNavigationCaptured && !_cancelMenuInputGuard.IsCaptured)
        {
            _menuCancelRequested = false;
            return;
        }
        bool directionHeld = !shortcutConsumed && _shortcutMenuInputGuard.IsCaptured &&
            (Pressed(ControlAction.ScriptPadDown) || Pressed(ControlAction.ScriptPadUp) ||
            Pressed(ControlAction.PhoneDown) || Pressed(ControlAction.PhoneUp) ||
            Pressed(ControlAction.FrontendDown) || Pressed(ControlAction.FrontendUp));
        bool suppressInput = _shortcutMenuInputGuard.Update(shortcutConsumed, directionHeld);
        bool acceptOrCancelHeld = !_textInputActivationPending && _textInputActivationGuard.IsCaptured &&
            (Game.IsKeyPressed(Keys.Enter) || Game.IsKeyPressed(Keys.Back) || Game.IsKeyPressed(Keys.Delete) ||
             _menuCancelHeld || NativeMenuCancelPressed() || Pressed(ControlAction.FrontendAccept));
        suppressInput |= _textInputActivationGuard.Update(_textInputActivationPending, acceptOrCancelHeld);
        _textInputActivationPending = false;
        if (_textInput != null) _textInput.Menu.AcceptsInput = !suppressInput;
        bool cancelHeld = _menuCancelHeld;
        bool cancelPressed = _pool.AreAnyVisible && (_menuCancelRequested || NativeMenuCancelPressed());
        _menuCancelRequested = false;
        if (_pool.AreAnyVisible && (!IsInputBlocked() || _textInput?.Active == true) && cancelPressed && !_cancelNavigationCaptured && !suppressInput)
        {
            _pool.OfType<NativeMenu>().First(menu => menu.Visible).Back();
            _cancelNavigationCaptured = true;
        }
        suppressInput |= _cancelMenuInputGuard.Update(_cancelNavigationCaptured, cancelHeld);
        if (!cancelHeld) _cancelNavigationCaptured = false;
        _suspendedMenuInputs.Clear();
        if (suppressInput)
        {
            foreach (var processable in _pool)
            {
                if (processable is NativeMenu menu && menu.AcceptsInput)
                {
                    menu.AcceptsInput = false;
                    _suspendedMenuInputs.Add(menu);
                }
            }
        }

        try
        {
            // LemonUI.Process also draws. Suppress navigation while the opening chord
            // is held, then consume its direction until release, without hiding the menu.
            _processingResourceMenus = true;
            _pool.Process();
        }
        finally
        {
            _processingResourceMenus = false;
            foreach (NativeMenu menu in _suspendedMenuInputs) menu.AcceptsInput = true;
            _suspendedMenuInputs.Clear();
        }
    }

    private void SuppressMenuGameActions()
    {
        bool visible = _pool?.AreAnyVisible == true;
        if (!visible && !_cancelReleaseGuard.IsCaptured)
        {
            _menuCancelHeld = false;
            _reserveMenuCancel = false;
            return;
        }
        _menuCancelHeld = Game.IsKeyPressed(Keys.Escape) || (_textInput?.Active == true
            ? IsMenuUsingController() && TextCancelControls.Any(Pressed)
            : MenuCancelControls.Any(Pressed));
        _reserveMenuCancel = _cancelReleaseGuard.Update(visible, _menuCancelHeld);
        if (_reserveMenuCancel)
        {
            foreach (ControlAction action in MenuCancelControls)
            {
                Controls.DisableControlActionThisFrame(ControlType.PlayerControl, action);
                Controls.DisableControlActionThisFrame(ControlType.FrontendControl, action);
            }
        }

        if (!visible) return;
        foreach (ControlAction action in MenuGameplayControls)
        {
            Controls.DisableControlActionThisFrame(ControlType.PlayerControl, action);
        }
    }

    // LemonUI consumes only PhoneCancel/FrontendPause after closing. Esc also
    // binds FrontendPauseAlternate: consume every cancel alias until release.
    private static readonly ControlAction[] MenuCancelControls =
    {
        ControlAction.PhoneCancel, ControlAction.FrontendCancel,
        ControlAction.FrontendPause, ControlAction.FrontendPauseAlternate, ControlAction.Map
    };

    private static readonly ControlAction[] TextCancelControls =
    {
        ControlAction.PhoneCancel, ControlAction.FrontendCancel
    };

    private bool NativeMenuCancelPressed() => _textInput?.Active == true
        ? IsMenuUsingController() && TextCancelControls.Any(JustPressed)
        : MenuCancelControls.Any(JustPressed);

    private static readonly ControlAction[] MenuGameplayControls =
    {
        ControlAction.Attack, ControlAction.Aim, ControlAction.Attack2,
        ControlAction.MoveLeftRight, ControlAction.MoveUpDown,
        ControlAction.Sprint, ControlAction.Jump, ControlAction.Enter, ControlAction.Cover,
        ControlAction.SelectWeapon, ControlAction.VehicleAccelerate, ControlAction.VehicleBrake,
        ControlAction.VehicleMoveLeftRight, ControlAction.VehicleMoveUpDown,
        ControlAction.VehicleExit, ControlAction.VehicleAttack, ControlAction.VehicleAttack2,
        ControlAction.VehicleHandbrake
    };

    private static readonly ControlAction[] GamepadConflictingActions =
    {
        ControlAction.SelectWeapon, ControlAction.Cover,
        ControlAction.VehicleAim, ControlAction.VehicleHandbrake,
        ControlAction.VehicleDuck, ControlAction.VehicleAttack, ControlAction.VehicleAttack2,
        ControlAction.VehicleFlyYawLeft, ControlAction.VehicleFlyYawRight,
        ControlAction.VehicleFlySelectTargetLeft, ControlAction.VehicleFlySelectTargetRight,
        ControlAction.ParachuteBrakeLeft, ControlAction.ParachuteBrakeRight,
        ControlAction.Phone, ControlAction.PhoneUp, ControlAction.PhoneDown,
        ControlAction.CharacterWheel, ControlAction.MultiplayerInfo,
        ControlAction.FrontendUp, ControlAction.FrontendDown,
        ControlAction.ScriptPadUp, ControlAction.ScriptPadDown
    };

    private static readonly Keys[] QuickShortcutKeys = { Keys.J, Keys.K, Keys.O, Keys.Escape };

    private static bool Pressed(ControlAction action) =>
        Controls.IsControlPressed(ControlType.PlayerControl, action) ||
        Controls.IsDisabledControlPressed(ControlType.PlayerControl, action) ||
        Controls.IsControlPressed(ControlType.FrontendControl, action) ||
        Controls.IsDisabledControlPressed(ControlType.FrontendControl, action);

    private static bool JustPressed(ControlAction action) =>
        Controls.IsControlJustPressed(ControlType.PlayerControl, action) ||
        Controls.IsDisabledControlJustPressed(ControlType.PlayerControl, action) ||
        Controls.IsControlJustPressed(ControlType.FrontendControl, action) ||
        Controls.IsDisabledControlJustPressed(ControlType.FrontendControl, action);

    private bool OwnsHostMenuCancel() => _active && !_stopping && (!_gettingUserInput || _textInput?.Active == true) &&
        !API.IsChatFocused && !API.IsMenuVisible && (_pool?.AreAnyVisible == true || _reserveMenuCancel);

    private bool OwnsTextInput() => _active && !_stopping && _gettingUserInput && _textInput?.Active == true;

    private bool IsProcessingResourceMenuInput() => _processingResourceMenus &&
        _pool?.AreAnyVisible == true && OwnsHostMenuCancel();

    private static bool ShortcutReady(ref long previousTick, long now, double delay)
    {
        if (previousTick != 0 && (now - previousTick) / (double)Stopwatch.Frequency < delay)
        {
            return false;
        }

        previousTick = now;
        return true;
    }

    private void InitializeTextInput()
    {
        if (_pool == null || _textInput != null) return;
        NativeMenu dialog = CreateMenu("Совместная игра", "Ввод текста");
        _textInput = new ResourceTextInput(dialog);
        _pool.Add(dialog);
    }

    private void PromptUserInput(int maximumLength, string title, Action<string> onSubmitted)
    {
        if (!_active || _stopping || !API.IsOnServer || _pool == null || _textInput == null || _gettingUserInput) return;
        if (!HostMenuInputGuard.TextInputAvailable)
        {
            Notification.PostTicker("~r~Не удалось открыть поле ввода.~s~ Подключитесь к серверу заново.", false);
            return;
        }
        NativeMenu? visible = _pool?.OfType<NativeMenu>().FirstOrDefault(menu => menu.Visible);
        _pool?.HideAll();
        _gettingUserInput = true;
        _textInputActivationPending = true;
        _textInput.Menu.AcceptsInput = false;
        _textInput.UpdateInputMode(IsMenuUsingController());
        _textInput.Begin(maximumLength, title, result =>
        {
            _gettingUserInput = false;
            _textInputActivationPending = true;
            if (!_active || _stopping || !API.IsOnServer) return;
            if (visible != null) visible.Visible = true;
            if (result != null) onSubmitted(result);
        });
    }

    private void CancelTextInput()
    {
        _gettingUserInput = false;
        _textInput?.CloseSilently();
    }
}
