using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private NativeCheckboxItem? _seatbeltItem;
    private NativeCheckboxItem? _superJumpItem;
    private NativeCheckboxItem? _staminaItem;
    private NativeCheckboxItem? _underwaterItem;
    private Ped? _seatbeltPed;
    private Ped? _underwaterPed;
    private bool _originalWindscreenEjection;
    private bool _originalDrownsInWater;
    private bool _originalDrownsInSinkingVehicle;

    private void BuildPlayerAbilitiesMenu()
    {
        if (_playerMenu == null)
        {
            return;
        }

        var parachute = new NativeItem("Взять парашют", "Добавить парашют. Можно раскрыть в свободном падении.");
        parachute.Activated += (_, _) => GiveParachute();
        _playerMenu.Add(parachute);
        _seatbeltItem = new NativeCheckboxItem("Ремень безопасности", "Не вылетать через лобовое стекло при аварии.");
        _seatbeltItem.CheckboxChanged += (_, _) => ApplySeatbelt();
        _playerMenu.Add(_seatbeltItem);
        _superJumpItem = new NativeCheckboxItem("Суперпрыжок", "Персонаж прыгает выше обычного.");
        _playerMenu.Add(_superJumpItem);
        _staminaItem = new NativeCheckboxItem("Бесконечная выносливость", "Бег не расходует выносливость.");
        _playerMenu.Add(_staminaItem);
        _underwaterItem = new NativeCheckboxItem("Дыхание под водой", "Плавать под водой без урона от нехватки воздуха.");
        _underwaterItem.CheckboxChanged += (_, _) => ApplyUnderwaterBreathing();
        _playerMenu.Add(_underwaterItem);
    }

    private void TickPlayerAbilities()
    {
        ApplySeatbelt();
        ApplyUnderwaterBreathing();
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            return;
        }

        if (_superJumpItem?.Checked == true)
        {
            Game.Player.SetSuperJumpThisFrame();
        }

        if (_staminaItem?.Checked == true)
        {
            Function.Call(Hash.RESTORE_PLAYER_STAMINA, Game.Player.Handle, 1f);
        }
    }

    private static Ped? AvailablePlayerForAbility()
    {
        Ped player = Game.Player.Character;
        if (!API.IsOnServer || player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Персонаж:~s~ дождитесь возрождения и повторите.", false);
            return null;
        }

        return player;
    }

    private static void RestoreHealthAndArmor()
    {
        Ped? player = AvailablePlayerForAbility();
        if (player == null) return;
        player.Health = player.MaxHealth;
        player.Armor = Game.Player.MaxArmor;
        Notification.PostTicker(player.Health >= player.MaxHealth && player.Armor >= Game.Player.MaxArmor
            ? "~g~Персонаж:~s~ здоровье и броня восстановлены."
            : "~o~Персонаж:~s~ сейчас не удалось полностью восстановить здоровье и броню.", false);
    }

    private static void GiveParachute()
    {
        Ped? player = AvailablePlayerForAbility();
        if (player == null) return;
        Weapon parachute = player.Weapons.Give(WeaponHash.Parachute, 1, false, true);
        Notification.PostTicker(parachute.IsPresent
            ? "~g~Персонаж:~s~ парашют у вас."
            : "~o~Персонаж:~s~ не удалось выдать парашют. Попробуйте после приземления.", false);
    }

    private void ApplyUnderwaterBreathing()
    {
        Ped player = Game.Player.Character;
        bool enabled = _underwaterItem?.Checked == true && player != null && player.Exists() && !player.IsDead;
        if (!enabled || (_underwaterPed != null && _underwaterPed.Handle != player!.Handle))
        {
            RestoreUnderwaterPed();
        }

        if (!enabled) return;
        if (_underwaterPed == null)
        {
            _underwaterPed = player;
            _originalDrownsInWater = player!.DrownsInWater;
            _originalDrownsInSinkingVehicle = player.DrownsInSinkingVehicle;
        }

        player!.DrownsInWater = false;
        player.DrownsInSinkingVehicle = false;
    }

    private void ApplySeatbelt()
    {
        Ped player = Game.Player.Character;
        bool enabled = _seatbeltItem?.Checked == true && player != null && player.Exists() && !player.IsDead;
        if (!enabled || (_seatbeltPed != null && _seatbeltPed.Handle != player!.Handle))
        {
            RestoreSeatbeltPed();
        }

        if (!enabled)
        {
            return;
        }

        if (_seatbeltPed == null)
        {
            _seatbeltPed = player;
            _originalWindscreenEjection = player!.CanFlyThroughWindscreen;
        }

        player!.CanFlyThroughWindscreen = false;
    }

    private void CleanupPlayerAbilities()
    {
        RestoreSeatbeltPed();
        RestoreUnderwaterPed();
        if (_seatbeltItem?.Checked == true) _seatbeltItem.Checked = false;
        if (_superJumpItem?.Checked == true) _superJumpItem.Checked = false;
        if (_staminaItem?.Checked == true) _staminaItem.Checked = false;
        if (_underwaterItem?.Checked == true) _underwaterItem.Checked = false;
    }

    private void RestoreSeatbeltPed()
    {
        if (_seatbeltPed != null && _seatbeltPed.Exists())
        {
            _seatbeltPed.CanFlyThroughWindscreen = _originalWindscreenEjection;
        }

        _seatbeltPed = null;
    }

    private void RestoreUnderwaterPed()
    {
        if (_underwaterPed != null && _underwaterPed.Exists())
        {
            _underwaterPed.DrownsInWater = _originalDrownsInWater;
            _underwaterPed.DrownsInSinkingVehicle = _originalDrownsInSinkingVehicle;
        }

        _underwaterPed = null;
    }
}
