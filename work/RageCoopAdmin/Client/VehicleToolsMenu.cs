using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using CoopControl.Shared;
using GTA;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private const int FlipVehicleRequest = 20774124;
    private const int VehicleAppearanceRequest = 20774125;
    private const int VehicleAppearanceState = 20774126;
    private const int SpawnVehiclePresetRequest = 20774127;
    private const int VehicleAppearanceRemoved = 20774135;
    private readonly Dictionary<int, LocalAppearanceState> _appearanceStates = new Dictionary<int, LocalAppearanceState>();
    private VehiclePresetStore? _presetStore;
    private List<VehiclePreset> _presets = new List<VehiclePreset>();
    private NativeMenu? _presetsMenu;
    private NativeMenu? _presetDeleteMenu;
    private VehicleMenuItem? _presetDeleteItem;
    private PresetDeletionRequest? _pendingPresetDeletion;
    private bool _presetStorageUnavailable;
    private bool _presetsMenuDirty;
    private long _lastAppearanceTick;
    private long _lastOwnerAppearanceCapture;
    private readonly AppearanceEditQueue _appearanceEdits = new AppearanceEditQueue();
    private readonly Dictionary<int, string> _failedAppearanceChanges = new Dictionary<int, string>();

    private void RegisterVehicleToolsEvents()
    {
        _eventSubscriptions.Register(VehicleAppearanceState, OnVehicleAppearanceState);
        _eventSubscriptions.Register(VehicleAppearanceRemoved, OnVehicleAppearanceRemoved);
        RegisterPassengerTeleportEvents();
    }

    private void InitializeVehicleTools()
    {
        try
        {
            if (!VehicleAppearanceRuntimeGuard.Install(FindAppearanceSnapshot, TryApplyVehicleAppearance))
            {
                Logger.Warning("Coop Control: installed RageCoop cosmetic cache signatures are unsupported.");
                Notification.PostTicker("~o~Общий тюнинг недоступен для этой версии RageCoop.", false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: vehicle appearance guard failed: " + ex.GetType().Name);
            Notification.PostTicker("~o~Не удалось включить общий тюнинг.", false);
        }

        if (!RuntimeEntityLookup.IsAvailable)
        {
            Logger.Warning("Coop Control: installed RageCoop entity lookup signatures are unsupported.");
            Notification.PostTicker("~o~Эта версия RageCoop не поддерживает общий тюнинг и посадку через меню.", false);
        }
        try
        {
            _presetStore = new VehiclePresetStore(CurrentResource.DataFolder);
            _presets = _presetStore.Load();
        }
        catch (Exception ex) when (IsVehicleStorageError(ex))
        {
            _presetStorageUnavailable = true;
            Logger.Warning("Coop Control: vehicle presets could not be loaded: " + ex.GetType().Name);
            Notification.PostTicker("~o~Сохранённые машины недоступны.~s~ Исходный файл не изменён.", false);
        }
    }

    private void BuildVehicleToolsMenu()
    {
        if (_vehicleMenu == null || _pool == null)
        {
            return;
        }

        _presetsMenu = CreateMenu("Совместная игра", "Гараж");
        _vehicleMenu.AddSubMenu(_presetsMenu);
        _pool.Add(_presetsMenu);
        _presetDeleteMenu = CreateMenu("Совместная игра", "Удалить сохранённую машину?");
        _presetDeleteMenu.Parent = _presetsMenu;
        var cancel = new NativeItem("Отмена", "Вернуться к сохранённым машинам.");
        cancel.Activated += (_, _) => ClosePresetDeletion();
        _presetDeleteMenu.Add(cancel);
        _presetDeleteItem = new VehicleMenuItem("Удалить машину", string.Empty);
        _presetDeleteItem.Activated += (_, _) => ConfirmPresetDeletion();
        _presetDeleteMenu.Add(_presetDeleteItem);
        _presetDeleteMenu.Closed += (_, _) => _pendingPresetDeletion = null;
        _pool.Add(_presetDeleteMenu);
        RefreshVehiclePresets();
    }

    private bool TryGetOwnedVehicle(out WatchedVehicle watched)
    {
        watched = null!;
        Ped player = Game.Player.Character;
        if (!API.IsOnServer)
        {
            return false;
        }

        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Машина:~s~ дождитесь возрождения и повторите.", false);
            return false;
        }

        Vehicle? current = player.CurrentVehicle;
        if (current == null || !current.Exists() || current.Driver?.Handle != player.Handle)
        {
            Notification.PostTicker("~o~Сначала сядьте за руль своей машины.", false);
            return false;
        }

        RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(current.Handle);
        if (synced?.IsLocal != true || synced.ID == 0)
        {
            Notification.PostTicker("~o~Машина ещё не готова.~s~ Подождите секунду и повторите.", false);
            return false;
        }

        if (!_vehicles.TryGetValue(synced.ID, out WatchedVehicle? candidate) ||
            !ReferenceEquals(candidate.Synced, synced))
        {
            candidate = new WatchedVehicle(synced);
            _vehicles[synced.ID] = candidate;
        }
        watched = candidate;
        return true;
    }

    private void RequestFlipVehicle()
    {
        Ped player = Game.Player.Character;
        if (!API.IsOnServer) { return; }
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Машина:~s~ дождитесь возрождения и повторите.", false);
            return;
        }
        Vehicle? current = player.CurrentVehicle;
        if (current != null && current.Exists() && current.Driver?.Handle != player.Handle)
        {
            Notification.PostTicker("~o~Попросите водителя перевернуть транспорт.", false);
            return;
        }

        WatchedVehicle? watched = _vehicles.Values.Where(value => value.Synced.IsLocal &&
            value.Synced.MainVehicle != null && value.Synced.MainVehicle.Exists() &&
            (current != null ? current.Handle == value.Synced.MainVehicle.Handle :
                GTA.Math.Vector3.Distance(player.Position, value.Synced.MainVehicle.Position) <= 8f))
            .OrderBy(value => GTA.Math.Vector3.Distance(player.Position, value.Synced.MainVehicle.Position)).FirstOrDefault();
        if (watched == null)
        {
            Notification.PostTicker("~o~Подойдите к своей машине или сядьте за руль.", false);
            return;
        }

        API.SendCustomEvent(FlipVehicleRequest, watched.Synced.ID);
    }

    private void StageVehicleAppearance(RageCoop.Client.SyncedVehicle synced, VehicleAppearance value)
    {
        if (!_appearanceStates.ContainsKey(synced.ID) && _appearanceStates.Count >= 256)
        {
            return;
        }
        string xml = VehicleAppearanceCodec.Encode(value);
        _failedAppearanceChanges.Remove(synced.ID);
        _appearanceEdits.Stage(synced.ID, xml);
        int revision = _appearanceStates.TryGetValue(synced.ID, out LocalAppearanceState? existing) ? existing.Revision : 0;
        var state = new LocalAppearanceState(value, revision, xml);
        _appearanceStates[synced.ID] = state;
        state.AppliedHandle = synced.MainVehicle.Handle;
        synced.SendNextFrame = true;
        synced.SendFullNextFrame = true;
    }

    private void RefreshVehiclePresets()
    {
        if (_presetsMenu == null) { return; }
        int selectedIndex = _presetsMenu.SelectedIndex;
        string? selectedName = selectedIndex > 0 ? _presetsMenu.SelectedItem?.Tag as string : null;
        _presetsMenu.Clear();
        _presetsMenuDirty = false;
        if (_presetStorageUnavailable)
        {
            _presetsMenu.Add(new NativeItem("Список недоступен", "Файл не удалось прочитать. Исходные данные сохранены без изменений.") { Enabled = false });
            return;
        }

        var save = new NativeItem("Сохранить текущую машину", "Модель вместе с цветом, дисками и тюнингом. Избранное хранит только модель.");
        save.Activated += (_, _) => SaveCurrentVehiclePreset();
        _presetsMenu.Add(save);
        foreach (VehiclePreset preset in _presets)
        {
            var row = new VehicleMenuListItem(preset.Name,
                "~h~" + preset.Name + "~h~~n~Сохранены цвет, диски и тюнинг.",
                VehicleMenuText.Create, VehicleMenuText.Delete)
            { Tag = preset.Name };
            row.Selected += (_, _) => PreloadSpawnModel(preset.Appearance.ModelHash);
            row.Activated += (_, _) =>
            {
                if (row.SelectedIndex == 0) { SpawnVehiclePreset(preset); }
                else { RequestPresetDeletion(preset); }
                row.SelectedIndex = 0;
            };
            _presetsMenu.Add(row);
            if (string.Equals(selectedName, preset.Name, StringComparison.Ordinal))
                _presetsMenu.SelectedItem = row;
        }

        if (_presets.Count == 0)
        {
            _presetsMenu.Add(new NativeItem("Здесь будут ваши машины", "Настройте машину, сядьте за руль и сохраните её под своим названием.") { Enabled = false });
        }
        if (selectedName != null && !_presets.Any(value => string.Equals(value.Name, selectedName, StringComparison.Ordinal)))
            _presetsMenu.SelectedIndex = Math.Min(selectedIndex, _presetsMenu.Items.Count - 1);
    }

    private void RequestPresetDeletion(VehiclePreset preset)
    {
        if (!_active || _stopping || !API.IsOnServer || _presetStorageUnavailable || _presetStore == null ||
            _presetsMenu == null || _presetDeleteMenu == null || _presetDeleteItem == null ||
            !_presets.Any(value => ReferenceEquals(value, preset))) { return; }

        _pendingPresetDeletion = new PresetDeletionRequest(preset, PresetSnapshot(_presets));
        _presetDeleteItem.FullTitle = "Удалить «" + preset.Name + "»";
        _presetDeleteItem.Description = "Сохранённая машина «" + preset.Name + "» будет удалена из гаража. Транспорт в игре останется на месте.";
        _presetsMenu.Visible = false;
        _presetDeleteMenu.SelectedIndex = 0;
        _presetDeleteMenu.Visible = true;
    }

    private void ClosePresetDeletion()
    {
        _pendingPresetDeletion = null;
        if (_presetDeleteMenu != null) _presetDeleteMenu.Visible = false;
        if (_presetsMenu != null && _active && !_stopping && API.IsOnServer) _presetsMenu.Visible = true;
    }

    private void ConfirmPresetDeletion()
    {
        PresetDeletionRequest? pending = _pendingPresetDeletion;
        // Revoke the request before any storage operation or notification so a
        // repeated activation cannot delete a second entry.
        ClosePresetDeletion();
        if (pending == null || !_active || _stopping || !API.IsOnServer ||
            _presetStore == null || _presetStorageUnavailable) { return; }

        bool current = false;
        bool checkedStorage = UpdatePresetList(stored =>
        {
            current = _presets.Any(value => ReferenceEquals(value, pending.Preset)) &&
                pending.Snapshot.SequenceEqual(PresetSnapshot(_presets), StringComparer.Ordinal) &&
                pending.Snapshot.SequenceEqual(PresetSnapshot(stored), StringComparer.Ordinal);
            if (current) stored.RemoveAll(value => string.Equals(value.Name, pending.Preset.Name, StringComparison.Ordinal));
            return current;
        }, "Машина удалена из списка");
        if (checkedStorage && !current)
        {
            Notification.PostTicker("~o~Список машин изменился.~s~ Выберите машину заново.", false);
        }
    }

    private static string[] PresetSnapshot(IEnumerable<VehiclePreset> presets) => presets
        .Select(value => value.Name + "\0" + VehicleAppearanceCodec.Encode(value.Appearance)).ToArray();

    private sealed class PresetDeletionRequest
    {
        internal PresetDeletionRequest(VehiclePreset preset, string[] snapshot)
        { Preset = preset; Snapshot = snapshot; }
        internal VehiclePreset Preset { get; }
        internal string[] Snapshot { get; }
    }

    private void SaveCurrentVehiclePreset()
    {
        if (!CanSaveVehiclePreset() || !TryGetOwnedVehicle(out WatchedVehicle? watched)) { return; }
        int vehicleId = watched.Synced.ID;
        PromptUserInput(40, "Название машины", name => SaveNamedVehiclePreset(vehicleId, name));
    }

    private void SaveNamedVehiclePreset(int vehicleId, string name)
    {
        if (!_active || _stopping || !API.IsOnServer || !CanSaveVehiclePreset()) { return; }

        name = name.Trim();
        if (name.Length == 0) { return; }
        if (name.Any(c => char.IsControl(c) || c == '~') || _presets.Any(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Notification.PostTicker("~o~Такое название уже есть или оно содержит недопустимый символ.", false);
            return;
        }

        if (!TryGetOwnedVehicle(out WatchedVehicle? current))
        {
            return;
        }

        if (current.Synced.ID != vehicleId)
        {
            Notification.PostTicker("~o~Вы сменили машину.~s~ Повторите сохранение.", false);
            return;
        }

        if (!TryCaptureVehicleAppearance(current.Synced.MainVehicle, out VehicleAppearance? appearance)) { return; }
        string? rejection = null;
        bool checkedStorage = UpdatePresetList(values =>
        {
            if (values.Count >= VehiclePresetStore.MaximumPresets)
                rejection = "Список заполнен. Удалите одну из 64 сохранённых машин.";
            else if (values.Any(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)))
                rejection = "Такое название уже есть. Выберите другое.";
            if (rejection != null) return false;
            values.Add(new VehiclePreset { Name = name, Appearance = appearance });
            return true;
        }, "Машина сохранена с тюнингом");
        if (checkedStorage && rejection != null) Notification.PostTicker("~o~" + rejection, false);
    }

    private bool CanSaveVehiclePreset()
    {
        if (_presetStorageUnavailable || _presetStore == null) { return false; }
        try
        {
            _presets = _presetStore.Load();
            _presetsMenuDirty = true;
        }
        catch (Exception ex) when (IsVehicleStorageError(ex))
        {
            Logger.Warning("Coop Control: vehicle presets could not be checked before saving: " + ex.GetType().Name);
            Notification.PostTicker("~o~Не удалось прочитать гараж.~s~ Исходный файл не изменён.", false);
            return false;
        }
        if (_presets.Count >= VehiclePresetStore.MaximumPresets)
        {
            Notification.PostTicker("~o~Список заполнен.~s~ Удалите одну из 64 сохранённых машин.", false);
            return false;
        }

        return true;
    }

    private bool UpdatePresetList(Func<List<VehiclePreset>, bool> change, string successMessage)
    {
        if (_presetStore == null || _presetStorageUnavailable) { return false; }
        try
        {
            bool changed = false;
            _presets = _presetStore.Update(values => changed = change(values));
            _presetsMenuDirty = true;
            if (changed) Notification.PostTicker("~g~" + successMessage, false);
            return true;
        }
        catch (Exception ex) when (IsVehicleStorageError(ex))
        {
            Logger.Warning("Coop Control: vehicle presets could not be saved: " + ex.GetType().Name);
            Notification.PostTicker("~o~Не удалось сохранить список.~s~ Предыдущие данные не изменены.", false);
            return false;
        }
    }

    private void SpawnVehiclePreset(VehiclePreset preset) =>
        RequestVehicleSpawn(preset.Appearance.ModelHash, VehicleAppearanceCodec.Encode(preset.Appearance));

    private void OnVehicleAppearanceState(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args is not { Length: 5 } ||
            e.Args[0] is not int id || id == 0 || e.Args[1] is not int revision || revision <= 0 || e.Args[2] is not string xml ||
            e.Args[3] is not int requesterId || e.Args[4] is not int nonce)
        {
            return;
        }

        try
        {
            VehicleAppearance value = VehicleAppearanceCodec.Decode(xml);
            RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(id);
            bool locallyOwned = requesterId == API.LocalPlayerID || synced?.IsLocal == true;
            if ((locallyOwned && _appearanceEdits.IsStaleEcho(id, nonce)) ||
                (_appearanceStates.TryGetValue(id, out LocalAppearanceState? existing) && existing.Revision >= revision))
            {
                return;
            }
            if (_appearanceStates.Count < 256 || _appearanceStates.ContainsKey(id))
            {
                if (_appearanceStates.TryGetValue(id, out LocalAppearanceState? same) && same.Xml == xml)
                {
                    // An acknowledgement of our already applied edit must not repeat
                    // the full native customization or schedule another full snapshot.
                    same.Revision = revision;
                }
                else
                {
                    var state = new LocalAppearanceState(value, revision, xml);
                    // A delayed acknowledgement can outlive bounded retries and
                    // their retained state. Its snapshot was captured from this
                    // owner's car; replaying it could undo a newer LSC preview
                    // before the next capture. Initial garage spawns use nonce 0.
                    if (requesterId == API.LocalPlayerID && nonce != 0 && synced?.IsLocal == true && synced.MainVehicle != null)
                    {
                        state.AppliedHandle = synced.MainVehicle.Handle;
                    }
                    _appearanceStates[id] = state;
                }
                _failedAppearanceChanges.Remove(id);
                if (requesterId == API.LocalPlayerID) { _appearanceEdits.Acknowledge(id, nonce); }
            }
        }
        catch (Exception ex) when (IsVehicleStorageError(ex))
        {
            Logger.Warning("Coop Control: rejected vehicle appearance: " + ex.GetType().Name);
        }
    }

    private void TickVehicleTools()
    {
        TickPassengerTeleport();
        if (_presetsMenuDirty) { RefreshVehiclePresets(); }
        long now = Stopwatch.GetTimestamp();
        if (_appearanceEdits.TryTake((long)(now / (double)Stopwatch.Frequency * 1000), out PendingAppearanceEdit? edit))
        {
            if (edit.Expired)
            {
                if (_failedAppearanceChanges.Count >= 256 && !_failedAppearanceChanges.ContainsKey(edit.VehicleId))
                {
                    _failedAppearanceChanges.Remove(_failedAppearanceChanges.Keys.First());
                }
                _failedAppearanceChanges[edit.VehicleId] = edit.Xml;
                if (_appearanceStates.TryGetValue(edit.VehicleId, out LocalAppearanceState? state) && state.Xml == edit.Xml)
                {
                    _appearanceStates.Remove(edit.VehicleId);
                }
                Notification.PostTicker("~o~Не удалось передать тюнинг другим игрокам.~s~ Повторите изменение.", false);
            }
            else
            {
                RageCoop.Client.SyncedVehicle? editedVehicle = RuntimeEntityLookup.GetVehicleByID(edit.VehicleId);
                if (editedVehicle?.IsLocal == true)
                {
                    API.SendCustomEvent(VehicleAppearanceRequest, edit.VehicleId, edit.Xml, edit.Nonce);
                }
                else { _appearanceEdits.Forget(edit.VehicleId); }
            }
        }
        if ((now - _lastAppearanceTick) / (double)Stopwatch.Frequency < 0.1) { return; }
        _lastAppearanceTick = now;
        foreach (KeyValuePair<int, LocalAppearanceState> pair in _appearanceStates)
        {
            RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByID(pair.Key);
            if (synced == null)
            {
                // A remote entity can leave the client's stream while the car still
                // exists. Retain its bounded cosmetic state until the server deletes it.
                continue;
            }

            if (TryApplyVehicleAppearanceCore(synced)) { break; }
        }

        CaptureOwnerAppearance(now);
    }

    private AppearanceSyncSnapshot? FindAppearanceSnapshot(RageCoop.Client.SyncedVehicle synced)
    {
        if (_stopping || !API.IsOnServer || !_appearanceStates.TryGetValue(synced.ID, out LocalAppearanceState? state)) { return null; }
        Vehicle? vehicle = synced.MainVehicle;
        if (vehicle == null) { return null; }
        return state.AppliedHandle == vehicle.Handle || (vehicle.Exists() && vehicle.Model.Hash == state.Appearance.ModelHash) ? state.Snapshot : null;
    }

    private void TryApplyVehicleAppearance(RageCoop.Client.SyncedVehicle synced, bool force) => TryApplyVehicleAppearanceCore(synced, force);

    private bool TryApplyVehicleAppearanceCore(RageCoop.Client.SyncedVehicle synced, bool force = false)
    {
        if (_stopping || !API.IsOnServer || !_appearanceStates.TryGetValue(synced.ID, out LocalAppearanceState? state)) { return false; }
        Vehicle? vehicle = synced.MainVehicle;
        if (vehicle == null || (!force && state.AppliedHandle == vehicle.Handle) || !vehicle.Exists() || vehicle.Model.Hash != state.Appearance.ModelHash) { return false; }
        NativeVehicleAppearance.Apply(vehicle, state.Appearance);
        state.AppliedHandle = vehicle.Handle;
        if (synced.IsLocal)
        {
            synced.SendNextFrame = true;
            synced.SendFullNextFrame = true;
        }
        return true;
    }

    private void CaptureOwnerAppearance(long now)
    {
        if ((now - _lastOwnerAppearanceCapture) / (double)Stopwatch.Frequency < 0.5) { return; }
        _lastOwnerAppearanceCapture = now;
        Ped? player = Game.Player.Character;
        Vehicle? vehicle = player?.CurrentVehicle;
        if (player == null || player.IsDead || vehicle == null || !vehicle.Exists() || vehicle.Driver?.Handle != player.Handle) { return; }
        RageCoop.Client.SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(vehicle.Handle);
        if (synced?.IsLocal != true || synced.ID == 0 || !TryCaptureVehicleAppearance(vehicle, out VehicleAppearance? value, notifyOnError: false)) { return; }
        string xml = VehicleAppearanceCodec.Encode(value);
        if (_failedAppearanceChanges.TryGetValue(synced.ID, out string? failed) && failed == xml) { return; }
        if (_appearanceStates.TryGetValue(synced.ID, out LocalAppearanceState? state) && state.Xml == xml) { return; }
        StageVehicleAppearance(synced, value);
    }

    private void CleanupVehicleTools()
    {
        _appearanceStates.Clear();
        _appearanceEdits.Clear();
        _failedAppearanceChanges.Clear();
        _lastOwnerAppearanceCapture = 0;
        if (_stopping) { VehicleAppearanceRuntimeGuard.Uninstall(); }
        CancelPassengerTeleport(null);
    }

    private void OnVehicleAppearanceRemoved(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args is not { Length: 1 } || e.Args[0] is not int id)
        {
            return;
        }

        _appearanceStates.Remove(id);
        _appearanceEdits.Forget(id);
        _failedAppearanceChanges.Remove(id);
    }

    private bool TryCaptureVehicleAppearance(Vehicle vehicle, out VehicleAppearance appearance, bool notifyOnError = true)
    {
        try
        {
            appearance = NativeVehicleAppearance.Capture(vehicle);
            return true;
        }
        catch (InvalidDataException)
        {
            appearance = null!;
            if (notifyOnError) { Notification.PostTicker("~o~Эту конфигурацию пока нельзя сохранить.", false); }
            return false;
        }
    }

    private static bool IsVehicleStorageError(Exception ex) =>
        ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException || ex is XmlException;

    private sealed class LocalAppearanceState
    {
        internal LocalAppearanceState(VehicleAppearance appearance, int revision, string xml)
        { Appearance = appearance; Revision = revision; Xml = xml; Snapshot = new AppearanceSyncSnapshot(appearance); }
        internal VehicleAppearance Appearance { get; }
        internal int Revision { get; set; }
        internal string Xml { get; }
        internal AppearanceSyncSnapshot Snapshot { get; }
        internal int AppliedHandle { get; set; }
    }
}
