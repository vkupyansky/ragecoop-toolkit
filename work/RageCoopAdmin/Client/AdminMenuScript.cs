using System;
using static CoopControl.Shared.WorldOptions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Scaleform;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript : ClientScript
{
    private const int SpawnRequest = 20774111;
    private const int WeatherRequest = 20774112;
    private const int RepairRequest = 20774113;
    private const int StatusResponse = 20774114;
    private const int WeatherState = 20774115;
    private const int TimeRequest = 20774116;
    private const int TimeState = 20774117;
    private const int ShareWaypointRequest = 20774118;
    private const int WaypointState = 20774119;
    private const int TeleportToPlayerRequest = 20774120;
    private const int TeleportToPlayerState = 20774121;
    private const int RecallCarRequest = 20774122;
    private const int RecallCarState = 20774123;

    private static readonly string[] WantedOptions =
    {
        "0 звёзд", "1 звезда", "2 звезды", "3 звезды",
        "4 звезды", "5 звёзд"
    };

    private static readonly string[] ClassNames =
    {
        "Компактные машины", "Седаны", "Внедорожники", "Купе", "Маслкары",
        "Классические спорткары", "Спорткары", "Суперкары", "Мотоциклы",
        "Внедорожная техника", "Промышленная техника", "Служебная техника", "Фургоны",
        "Велосипеды", "Лодки", "Вертолёты", "Самолёты", "Городские службы",
        "Экстренные службы", "Военная техника", "Коммерческий транспорт",
        "Поезда", "Гоночные машины"
    };

    private static readonly GTA.Math.Vector2[] RecallOffsets =
    {
        new GTA.Math.Vector2(5f, 0f), new GTA.Math.Vector2(-5f, 0f),
        new GTA.Math.Vector2(0f, 5f), new GTA.Math.Vector2(0f, -5f),
        new GTA.Math.Vector2(5f, 5f), new GTA.Math.Vector2(-5f, 5f),
        new GTA.Math.Vector2(5f, -5f), new GTA.Math.Vector2(-5f, -5f),
        new GTA.Math.Vector2(8f, 0f), new GTA.Math.Vector2(-8f, 0f),
        new GTA.Math.Vector2(0f, 8f), new GTA.Math.Vector2(0f, -8f)
    };

    private readonly Dictionary<int, WatchedVehicle> _vehicles = new Dictionary<int, WatchedVehicle>();
    private readonly List<VehicleEntry> _catalog = new List<VehicleEntry>();
    private readonly Dictionary<int, NativeMenu> _classMenus = new Dictionary<int, NativeMenu>();
    private readonly List<NativeItem> _weatherItems = new List<NativeItem>();
    private readonly Queue<int> _pendingModels = new Queue<int>();
    private readonly RecallRequestCorrelation _recallRequests = new RecallRequestCorrelation();
    private readonly WorldTimeSelection _timeSelection = new WorldTimeSelection();
    private readonly ResourceEventSubscriptions _eventSubscriptions = new ResourceEventSubscriptions();

    private ObjectPool? _pool;
    private NativeMenu? _mainMenu;
    private NativeMenu? _playerMenu;
    private NativeMenu? _vehicleMenu;
    private NativeMenu? _searchMenu;
    private NativeItem? _catalogState;
    private NativeListItem<string>? _wantedItem;
    private NativeCheckboxItem? _noWantedItem;
    private NativeCheckboxItem? _invincibleItem;
    private NativeListItem<string>? _hourItem;
    private NativeListItem<string>? _minuteItem;
    private NativeItem? _currentTimeItem;
    private PendingTeleport? _pendingTeleport;
    private PendingRecall? _pendingRecall;
    private bool _active;
    private bool _cleaned;
    private bool _menuResourcesReleased;
    private volatile bool _stopping;
    private bool _updatingWantedItem;
    private bool _updatingTimeItems;
    private bool _noWanted;
    private bool _invincible;
    private Ped? _protectedPed;
    private bool _originalInvincible;
    private bool _policeBackOffBeforeNoWanted;
    private long _lastMenuRefreshTick;
    private long _lastSampleTick;
    private long _lastNoWantedTick;
    private long _lastRecallRequestTick;
    private int _nextPlayerTeleportToken = unchecked((int)Stopwatch.GetTimestamp());
    private int _pendingPlayerTeleportToken;
    private long _playerTeleportRequestStarted;

    public override void OnStart()
    {
        try
        {
            CoopControl.Shared.EngineLoggerGuard.Install();
            CoopControl.Shared.EngineWorkerGuard.Install();
            CoopControl.Shared.EngineTransportGuard.Install();
            EngineRosterGuard.Install();
            EngineIngressGuard.Install();
            EnginePacketGuard.Install();
            EngineNetworkGuard.Install();
            if (!EngineTrafficGuard.Install())
            {
                throw new InvalidOperationException("The installed RageCoop traffic engine does not match the supported contract.");
            }
            if (!EngineDamageGuard.Install())
            {
                throw new InvalidOperationException("The installed RageCoop damage engine does not match the supported contract.");
            }
            if (!EngineMotionGuard.Install())
            {
                throw new InvalidOperationException("The installed RageCoop motion engine does not match the supported contract.");
            }
            EngineSendGuard.Install();
            EngineGameplayGuard.Install();
        }
        catch
        {
            UninstallGameplayGuard();
            EngineSendGuard.Uninstall();
            EngineMotionGuard.Uninstall();
            EngineDamageGuard.Uninstall();
            EngineTrafficGuard.Uninstall();
            EngineNetworkGuard.Uninstall();
            EnginePacketGuard.Uninstall();
            EngineIngressGuard.Uninstall();
            EngineRosterGuard.Uninstall();
            CoopControl.Shared.EngineTransportGuard.Uninstall();
            CoopControl.Shared.EngineWorkerGuard.Uninstall();
            CoopControl.Shared.EngineLoggerGuard.Uninstall();
            throw;
        }

        try
        {
            if (!DeathMessageGuard.Install())
            {
                Logger.Warning("Coop Control: death message guard unavailable; RageCoop KillMessage signature changed.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: death message guard failed: " + ex.GetType().Name);
        }

        try
        {
            if (!PassengerSeatGuard.Install(() => !_active || _stopping || _pool?.AreAnyVisible != false || IsInputBlocked()))
            {
                Logger.Warning("Coop Control: passenger seat guard unavailable; RageCoop signatures changed.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: passenger seat guard failed: " + ex.GetType().Name);
        }

        try
        {
            if (!VehicleBulletReplayGuard.Install())
            {
                Logger.Warning("Coop Control: vehicle bullet range guard unavailable; RageCoop contract changed.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: vehicle bullet range guard failed: " + ex.GetType().Name);
        }

        try
        {
            _eventSubscriptions.Register(StatusResponse, OnStatusReceived);
            _eventSubscriptions.Register(WeatherState, OnWeatherReceived);
            _eventSubscriptions.Register(TimeState, OnTimeReceived);
            _eventSubscriptions.Register(WaypointState, OnWaypointReceived);
            _eventSubscriptions.Register(TeleportToPlayerState, OnTeleportToPlayerReceived);
            _eventSubscriptions.Register(RecallCarState, OnRecallCarReceived);
            RegisterVehicleToolsEvents();
            RegisterVehicleSpawnEvents();
            RepairSync.Start(Logger.Warning);
            if (!VehicleAudioSync.Install())
            {
                Logger.Warning("Coop Control: radio synchronization contract unavailable.");
            }
            API.QueueAction(Initialize);
        }
        catch
        {
            _eventSubscriptions.Dispose();
            throw;
        }
    }

    public override void OnStop()
    {
        _stopping = true;
        _eventSubscriptions.Dispose();
        _textInput?.Abandon();
        try
        {
            VehicleBulletReplayGuard.Uninstall();
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: vehicle bullet range guard cleanup failed: " + ex.GetType().Name);
        }

        try
        {
            DeathMessageGuard.Uninstall();
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: death message guard cleanup failed: " + ex.GetType().Name);
        }

        try
        {
            PassengerSeatGuard.Uninstall();
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: passenger seat guard cleanup failed: " + ex.GetType().Name);
        }

        if (_cleaned)
        {
            HostAbortCleanupGuard.Uninstall();
        }
        else
        {
            API.QueueAction(() =>
            {
                Cleanup();
                HostAbortCleanupGuard.Uninstall();
            });
        }
    }

    private void Initialize()
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            if (!HostAbortCleanupGuard.Install(CleanupBeforeHostAbort))
                Logger.Warning("Coop Control: host cleanup guard unavailable; SHVDN lifecycle signature changed.");
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: host cleanup guard failed: " + ex.GetType().Name);
        }

        ResourceIdentity.Normalize(CurrentResource);
        InitializeFavorites();
        InitializeInterface();
        InitializeVehicleTools();
        BuildMenus();
        InitializeTextInput();
        try
        {
            if (!HostMenuInputGuard.Install(OwnsHostMenuCancel, IsProcessingResourceMenuInput, OwnsTextInput))
                Logger.Warning("Coop Control: host pause input guard unavailable; RageCoop pause input contract changed.");
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: host pause input guard failed: " + ex.GetType().Name);
        }
        try
        {
            if (!PassengerSeatGuard.HideOriginalSetting())
                Logger.Warning("Coop Control: original passenger setting does not match the supported menu contract.");
        }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: original passenger setting could not be hidden: " + ex.GetType().Name);
        }
        API.Events.OnKeyDown += OnKeyDown;
        API.Events.OnKeyUp += OnKeyUp;
        API.Events.OnTick += OnTick;
        API.Events.OnVehicleSpawned += OnVehicleSpawned;
        API.Events.OnVehicleDeleted += OnVehicleDeleted;
        _active = true;
        Logger.Info("Coop Control initialized: module=" + typeof(AdminMenuScript).Assembly.ManifestModule.ModuleVersionId +
            "; resource=" + CurrentResource.Name);
        ResourceIdentity.CleanupObsolete(CurrentResource, Logger.Warning);
    }

    private void Cleanup()
    {
        _eventSubscriptions.Dispose();
        if (_cleaned) return;
        _active = false;
        // These IDs belong to RageCoop's remote gunshot pool. Cleanup runs
        // on the game thread, including the SHVDN host's Abort prefix.
        SoundIdPool.Clear();
        CancelTextInput();
        HostMenuInputGuard.Uninstall();
        try { PassengerSeatSetting.Restore(); }
        catch (Exception ex)
        {
            Logger.Warning("Coop Control: original passenger setting could not be restored: " + ex.GetType().Name);
        }
        RepairSync.Stop();
        VehicleAudioSync.Uninstall();
#if !PUBLIC_RELEASE
        CleanupSpeedometer();
#endif
        DisableNoWanted();
        DisableInvincibility();
        CleanupPlayerAbilities();
        CleanupWeapons();
        CleanupVehicleTools();
        CleanupVehicleSpawn();
        ReleaseSpawnModelPreload();
        CleanupPlayersUi();
        ClearTeleportFocus();
        API.Events.OnKeyDown -= OnKeyDown;
        API.Events.OnKeyUp -= OnKeyUp;
        _shortcutLatch.Clear();
        API.Events.OnTick -= OnTick;
        API.Events.OnVehicleSpawned -= OnVehicleSpawned;
        API.Events.OnVehicleDeleted -= OnVehicleDeleted;
        _pool?.HideAll();
        if (!_menuResourcesReleased && _pool != null)
        {
            _menuResourcesReleased = true;
            foreach (NativeMenu menu in _pool.OfType<NativeMenu>()) menu.Buttons.Dispose();
        }
        _vehicles.Clear();
        _vehicleRows.Clear();
        UninstallGameplayGuard();
        EngineSendGuard.Uninstall();
        EngineMotionGuard.Uninstall();
        EngineDamageGuard.Uninstall();
        EngineTrafficGuard.Uninstall();
        EngineNetworkGuard.Uninstall();
        EnginePacketGuard.Uninstall();
        EngineIngressGuard.Uninstall();
        EngineRosterGuard.Uninstall();
        CoopControl.Shared.EngineTransportGuard.Uninstall();
        CoopControl.Shared.EngineWorkerGuard.Uninstall();
        CoopControl.Shared.EngineLoggerGuard.Uninstall();
        _pendingTeleport = null;
        _pendingRecall = null;
        _recallRequests.Cancel();
        _timeSelection.Cancel();
        CancelPlayerTeleportRequest();
        _cleaned = true;
    }

    private void CleanupBeforeHostAbort()
    {
        // This is the actual SHVDN wrapper's Abort prefix, before its Aborted event
        // stops RageCoop. Installed SHVDN executes native tasks with game-thread TLS.
        _stopping = true;
        Cleanup();
    }

    private static void UninstallGameplayGuard()
    {
        // A failed static initializer poisons the guard type. Contain that failure
        // so the remaining owners are still removed during rollback or cleanup.
        try { EngineGameplayGuard.Uninstall(); }
        catch (Exception ex)
        {
            try { API.Logger.Warning("Coop Control: gameplay guard cleanup failed: " + ex.GetType().Name); }
            catch { }
        }
    }

    private void BuildMenus()
    {
        _pool = new ObjectPool();
        _mainMenu = CreateMenu("Совместная игра", "Главное меню");
        NativeMenu worldMenu = CreateMenu("Совместная игра", "Мир");
        _vehicleMenu = CreateMenu("Совместная игра", "Транспорт");
        _playerMenu = CreateMenu("Совместная игра", "Персонаж");
        NativeMenu weatherMenu = CreateMenu("Совместная игра", "Погода");
        NativeMenu timeMenu = CreateMenu("Совместная игра", "Время");
        _searchMenu = CreateMenu("Совместная игра", "Результаты поиска");
        _searchMenu.Parent = _vehicleMenu;

        NativeItem shareWaypointItem = new NativeItem("Поделиться меткой", "Заменить точку на карте у всех игроков вашей меткой.");
        shareWaypointItem.Activated += (_, _) => ShareWaypoint();
        _mainMenu.Add(shareWaypointItem);
        _mainMenu.AddSubMenu(_vehicleMenu);
        BuildTeleportMenu();
        BuildPlayersMenu();
        _mainMenu.AddSubMenu(_playerMenu);
        BuildWeaponsMenu();
        _mainMenu.AddSubMenu(worldMenu);
        BuildInterfaceMenu();

        NativeItem quickRepair = new NativeItem("Починить машину", "Починить текущий транспорт или ближайший пешком. K; на геймпаде LB + RB + ↑.");
        quickRepair.Activated += (_, _) => RequestRepair();
        _vehicleMenu.Add(quickRepair);
        NativeItem quickFlip = new NativeItem("Поставить на колёса", "Поставить свою машину на колёса, если она перевернулась.");
        quickFlip.Activated += (_, _) => RequestFlipVehicle();
        _vehicleMenu.Add(quickFlip);
        BuildFavoritesMenu();
        BuildAddonVehiclesMenu();

        worldMenu.AddSubMenu(weatherMenu);
        worldMenu.AddSubMenu(timeMenu);
        var health = new NativeItem("Восстановить здоровье и броню", "Полностью восстановить здоровье и броню вашего персонажа.");
        health.Activated += (_, _) => RestoreHealthAndArmor();
        _playerMenu.Add(health);

        _wantedItem = new NativeListItem<string>(
            "Мой розыск",
            "Выберите число звёзд стрелками ←/→. Меняется только ваш розыск.",
            WantedOptions);
        _wantedItem.ItemChanged += (_, e) =>
        {
            if (_updatingWantedItem || !API.IsOnServer)
            {
                return;
            }

            SetLocalWantedLevel(e.Index);
        };
        _playerMenu.Add(_wantedItem);
        _noWantedItem = new NativeCheckboxItem("Без розыска", "Полиция не разыскивает вашего персонажа.");
        _noWantedItem.CheckboxChanged += (_, _) => SetNoWanted(_noWantedItem.Checked);
        _playerMenu.Add(_noWantedItem);
        _invincibleItem = new NativeCheckboxItem("Бессмертие", "Ваш персонаж не получает урон.");
        _invincibleItem.CheckboxChanged += (_, _) => SetInvincibility(_invincibleItem.Checked);
        _playerMenu.Add(_invincibleItem);

        BuildPlayerAbilitiesMenu();
        BuildVehicleToolsMenu();

        NativeItem findItem = new NativeItem("Найти транспорт", "Поиск машины по названию или коду.");
        findItem.Activated += (_, _) => SearchModel();
        _vehicleMenu.Add(findItem);

        NativeItem codeItem = new NativeItem("Ввести код модели", "Машина вне каталога, например adder.");
        codeItem.Activated += (_, _) => SpawnByCode();
        _vehicleMenu.Add(codeItem);
        _catalogState = new NativeItem("Каталог загружается…", "Обычно занимает несколько секунд.") { Enabled = false };
        _vehicleMenu.Add(_catalogState);

        for (int i = 0; i < WeatherNames.Length; i++)
        {
            int weatherIndex = i;
            NativeItem item = new NativeItem(WeatherNames[i], "Установить погоду для всех игроков.");
            item.Activated += (_, _) => SendWeather(weatherIndex);
            weatherMenu.Add(item);
            _weatherItems.Add(item);
        }

        _currentTimeItem = new NativeItem("Сейчас", "Текущее игровое время на вашем экране.")
        {
            AltTitle = FormatTime(GTA.Chrono.GameClock.Hour, GTA.Chrono.GameClock.Minute),
            Enabled = false
        };
        timeMenu.Add(_currentTimeItem);
        string[] hours = Enumerable.Range(0, 24).Select(hour => hour.ToString("00")).ToArray();
        _hourItem = new NativeListItem<string>("Час", "Листайте ←/→. Время для всех изменится после короткой паузы.", hours);
        string[] minutes = Enumerable.Range(0, 60).Select(minute => minute.ToString("00")).ToArray();
        _minuteItem = new NativeListItem<string>("Минуты", "Листайте ←/→. Время для всех изменится после короткой паузы.", minutes);
        _hourItem.ItemChanged += (_, e) =>
        {
            if (!_updatingTimeItems && API.IsOnServer)
                _timeSelection.SelectHour(e.Index, Stopwatch.GetTimestamp());
        };
        _minuteItem.ItemChanged += (_, e) =>
        {
            if (!_updatingTimeItems && API.IsOnServer)
                _timeSelection.SelectMinute(e.Index, Stopwatch.GetTimestamp());
        };
        timeMenu.Add(_hourItem);
        timeMenu.Add(_minuteItem);
        timeMenu.Shown += (_, _) => RefreshTimeSelection();

        AddTimePreset(timeMenu, "Рассвет", 6);
        AddTimePreset(timeMenu, "Полдень", 12);
        AddTimePreset(timeMenu, "Закат", 19);
        AddTimePreset(timeMenu, "Ночь", 23);

        _pool.Add(_mainMenu);
        _pool.Add(worldMenu);
        _pool.Add(_vehicleMenu);
        _pool.Add(_playerMenu);
        _pool.Add(weatherMenu);
        _pool.Add(timeMenu);
        _pool.Add(_searchMenu);

        foreach (VehicleHash hash in Enum.GetValues(typeof(VehicleHash)))
        {
            int modelHash = unchecked((int)(uint)hash);
            if (!_pendingModels.Contains(modelHash))
            {
                _pendingModels.Enqueue(modelHash);
            }
        }
    }

    private static NativeMenu CreateMenu(string title, string subtitle)
    {
        NativeMenu menu = new NativeMenu(title, subtitle)
        {
            MaxItems = 10
        };
        menu.Banner.Color = Color.FromArgb(235, 14, 18, 24);
        menu.BannerText.Color = Color.FromArgb(255, 255, 167, 46);
        menu.BannerText.Font = GTA.UI.Font.ChaletLondon;
        menu.BannerText.Scale = 0.62f;
        float bannerBudget = 2f * (Math.Min(209f, menu.Width - 209f) - 16f);
        float bannerWidth = menu.BannerText.Width;
        if (bannerBudget > 0f && bannerWidth > bannerBudget)
            menu.BannerText.Scale *= bannerBudget / bannerWidth;
        menu.NoItemsText = "Пока пусто";
        menu.Buttons.Clear();
        menu.Buttons.Add(new InstructionalButton("Выбрать", GTA.Control.FrontendAccept));
        menu.Buttons.Add(new InstructionalButton("Назад", GTA.Control.FrontendCancel));
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_active && !_stopping)
        {
            HandleMenuKey(e);
        }
    }

    private void OnTick()
    {
        if (!_active || _stopping || _pool == null)
        {
            return;
        }

        RepairSync.Tick();
        if (!API.IsOnServer)
        {
            CancelTextInput();
            _pool.HideAll();
            _vehicles.Clear();
            DisableNoWanted();
            DisableInvincibility();
            CleanupPlayerAbilities();
            CleanupWeapons();
#if !PUBLIC_RELEASE
            CleanupSpeedometer();
#endif
            CleanupVehicleTools();
            CleanupVehicleSpawn();
            CleanupPlayersUi();
            ReleaseSpawnModelPreload();
            ClearTeleportFocus();
            _pendingTeleport = null;
            _pendingRecall = null;
            _recallRequests.Cancel();
            _timeSelection.Cancel();
            CancelPlayerTeleportRequest();
            return;
        }

        if (_gettingUserInput && (_textInput?.Visible != true || API.IsChatFocused || API.IsMenuVisible ||
            Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE) || Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) == 0))
        {
            CancelTextInput();
        }

        if (IsInputBlocked() && !_gettingUserInput && _pool.AreAnyVisible)
        {
            _pool.HideAll();
        }

        long now = Stopwatch.GetTimestamp();
        ExpirePlayerTeleportRequest(now);
        TickVehicleSpawn(now);
        ProcessMenus(ProcessQuickControls(now));
        ProcessTimeSelection(Stopwatch.GetTimestamp());
        if (!IsSpawnMenuVisible())
        {
            ReleaseSpawnModelPreload();
        }
        if (_favoritesDirty)
        {
            RefreshFavoritesMenu();
        }

        // Spread native model checks over frames to avoid a visible pause when joining.
        for (int i = 0; i < 25 && _pendingModels.Count > 0; i++)
        {
            AddModelIfAvailable(_pendingModels.Dequeue());
        }

        if (_pendingModels.Count == 0 && _catalogState != null)
        {
            FinishCatalog();
        }

        if ((now - _lastSampleTick) / (double)Stopwatch.Frequency >= 0.5)
        {
            _lastSampleTick = now;
            DetectRepairs(now);
        }

        if (_pool.AreAnyVisible &&
            (now - _lastMenuRefreshTick) / (double)Stopwatch.Frequency >= 0.4)
        {
            _lastMenuRefreshTick = now;
            SetWantedItem(Game.Player.Wanted.WantedLevel);
            if (_currentTimeItem != null)
            {
                _currentTimeItem.AltTitle = FormatTime(GTA.Chrono.GameClock.Hour, GTA.Chrono.GameClock.Minute);
            }
            RefreshTimeSelection();
        }

        if (_noWanted && (now - _lastNoWantedTick) / (double)Stopwatch.Frequency >= 0.25)
        {
            _lastNoWantedTick = now;
            Wanted wanted = Game.Player.Wanted;
            if (!wanted.PoliceBackOff)
            {
                wanted.SetPoliceIgnorePlayer(true);
            }

            if (wanted.WantedLevel != 0)
            {
                ClearLocalWantedLevel();
            }
        }

        if (_invincible)
        {
            ApplyInvincibility();
        }

        ProcessPendingTeleport(now);
        ProcessPendingRecall(now);
        TickPlayerAbilities();
        TickWeapons(now);
        TickVehicleTools();
        ProcessPlayersMenu(now);
#if !PUBLIC_RELEASE
        ProcessSpeedometer(now);
#endif
    }

    private void AddModelIfAvailable(int hash)
    {
        if (FindAddonVehicle(hash) != null) { return; }
        VehicleEntry? entry = DescribeVehicle(hash);
        if (entry.HasValue)
        {
            _catalog.Add(entry.Value);
        }
    }

    private static VehicleEntry? DescribeVehicle(int hash)
    {
        Model model = new Model(hash);
        if (!model.IsInCdImage || !model.IsVehicle)
        {
            return null;
        }

        string code = ((VehicleHash)unchecked((uint)hash)).ToString();
        string label = Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, hash);
        string displayName = string.IsNullOrWhiteSpace(label) ? string.Empty : Game.GetLocalizedString(label);
        if (string.IsNullOrWhiteSpace(displayName) || displayName == "NULL")
        {
            displayName = code;
        }

        int vehicleClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, hash);
        return new VehicleEntry(hash, code, displayName, vehicleClass);
    }

    private void FinishCatalog()
    {
        if (_vehicleMenu == null || _pool == null || _catalogState == null)
        {
            return;
        }

        _vehicleMenu.Remove(_catalogState);
        _catalogState = null;
        _catalogReady = true;

        foreach (IGrouping<int, VehicleEntry> group in _catalog.GroupBy(e => e.VehicleClass).OrderBy(g => g.Key))
        {
            string className = group.Key >= 0 && group.Key < ClassNames.Length
                ? ClassNames[group.Key]
                : "Другие модели";
            NativeMenu menu = CreateMenu("Совместная игра", className);
            foreach (VehicleEntry entry in group.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                AddVehicleItem(menu, entry);
            }

            _vehicleMenu.AddSubMenu(menu);
            _classMenus[group.Key] = menu;
            _pool.Add(menu);
        }

        Notification.PostTicker("~o~Совместная игра:~s~ каталог транспорта готов (" + _catalog.Count + " моделей).", false);
        _favoritesDirty = true;
    }

    private void SearchModel()
    {
        if (!_catalogReady)
        {
            Notification.PostTicker("~o~Каталог транспорта ещё загружается. Повторите поиск чуть позже.", false);
            return;
        }

        PromptUserInput(32, "Найти машину", ShowVehicleSearchResults);
    }

    private void ShowVehicleSearchResults(string query)
    {
        if (!_active || _stopping || !API.IsOnServer || !_catalogReady || query.Length == 0 ||
            _searchMenu == null || _vehicleMenu == null)
        {
            return;
        }

        RemoveTrackedRows(_searchMenu);
        _searchMenu.Clear();
        List<VehicleEntry> matches = _catalog
            .Where(entry => VehicleSearch.Matches(entry.DisplayName, entry.Code, query))
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(101)
            .ToList();

        List<AddonVehicleDefinition> addonMatches = _addonVehicles.Where(entry => entry.Matches(query))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (matches.Count == 0 && addonMatches.Count == 0)
        {
            _searchMenu.Add(new NativeItem("Ничего не найдено", "Проверьте название или введите код модели вручную.") { Enabled = false });
        }
        else
        {
            if (matches.Count > 100)
            {
                matches.RemoveAt(matches.Count - 1);
                _searchMenu.Add(new NativeItem("Найдено больше 100 моделей", "Показаны первые 100 обычных моделей и совпавшие добавленные машины. Уточните название, чтобы сузить поиск.") { Enabled = false });
            }
            foreach (VehicleEntry entry in matches)
            {
                AddVehicleItem(_searchMenu, entry);
            }
            foreach (AddonVehicleDefinition entry in addonMatches)
            {
                AddAddonVehicleItem(_searchMenu, entry);
            }
        }

        _vehicleMenu.Visible = false;
        _searchMenu.Visible = true;
    }

    private void SpawnByCode()
    {
        PromptUserInput(24, "Код модели — латиница, цифры, _", SpawnModelCode);
    }

    private void SpawnModelCode(string code)
    {
        if (!_active || _stopping || !API.IsOnServer || code.Length == 0)
        {
            return;
        }

        if (code.Length > 24)
        {
            Notification.PostTicker("~r~Код модели:~s~ не больше 24 символов.", false);
            return;
        }

        if (code.Any(ch =>
                !(ch >= 'a' && ch <= 'z') && !(ch >= 'A' && ch <= 'Z') &&
                !(ch >= '0' && ch <= '9') && ch != '_'))
        {
            Notification.PostTicker("~r~Код модели:~s~ используйте только латинские буквы, цифры и _.", false);
            return;
        }

        int modelHash = unchecked((int)StringHash.AtStringHash(code.ToLowerInvariant()));
        Model model = new Model(modelHash);
        if (!model.IsInCdImage || !model.IsVehicle)
        {
            Notification.PostTicker("~r~Код модели:~s~ такой машины нет в установленной GTA V Legacy.", false);
            return;
        }

        PreloadSpawnModel(modelHash);
        SendSpawn(modelHash);
    }

    private void SendSpawn(int modelHash) => RequestVehicleSpawn(modelHash, null);

    private static void SendWeather(int index)
    {
        if (API.IsOnServer)
        {
            API.SendCustomEvent(WeatherRequest, index);
        }
    }

    private void AddTimePreset(NativeMenu menu, string name, int hour)
    {
        NativeItem item = new NativeItem(name, "Установить " + FormatTime(hour, 0) + " для всех игроков.")
        {
            AltTitle = FormatTime(hour, 0)
        };
        item.Activated += (_, _) =>
        {
            if (!API.IsOnServer) return;
            _timeSelection.SelectPreset(hour, 0, Stopwatch.GetTimestamp());
            UpdateTimeRows();
        };
        menu.Add(item);
    }

    private static string FormatTime(int hour, int minute) => $"{hour:00}:{minute:00}";

    private void ProcessTimeSelection(long now)
    {
        if (_timeSelection.TryExpire(now))
        {
            RefreshTimeSelection();
            Notification.PostTicker("~o~Время:~s~ сервер не подтвердил изменение. Проверьте время и повторите выбор.", false);
        }

        if (_timeSelection.TryTakeRequest(now, out int hour, out int minute))
            API.SendCustomEvent(TimeRequest, hour, minute);
    }

    private void RefreshTimeSelection()
    {
        _timeSelection.Refresh(GTA.Chrono.GameClock.Hour, GTA.Chrono.GameClock.Minute);
        UpdateTimeRows();
    }

    private void UpdateTimeRows()
    {
        _updatingTimeItems = true;
        try
        {
            if (_hourItem != null) _hourItem.SelectedIndex = _timeSelection.Hour;
            if (_minuteItem != null) _minuteItem.SelectedIndex = _timeSelection.Minute;
        }
        finally { _updatingTimeItems = false; }
    }

    private static void ShareWaypoint()
    {
        Blip? waypoint = World.WaypointBlip;
        if (waypoint == null || !waypoint.Exists())
        {
            Notification.PostTicker("~r~Метка:~s~ сначала поставьте точку на карте.", false);
            return;
        }

        GTA.Math.Vector3 position = waypoint.Position;
        if (!TryScaleCoordinate(position.X, out int x) || !TryScaleCoordinate(position.Y, out int y))
        {
            Notification.PostTicker("~r~Метка:~s~ координаты вне игровой карты.", false);
            return;
        }

        if (API.IsOnServer)
        {
            API.SendCustomEvent(ShareWaypointRequest, x, y);
        }
    }

    private static bool TryScaleCoordinate(float coordinate, out int scaled)
    {
        scaled = 0;
        if (float.IsNaN(coordinate) || float.IsInfinity(coordinate) || coordinate < -10_000f || coordinate > 10_000f)
        {
            return false;
        }

        scaled = (int)Math.Round(coordinate * 100f);
        return true;
    }

    private static bool IsValidScaledCoordinate(int coordinate) => coordinate >= -1_000_000 && coordinate <= 1_000_000;

    private void TeleportToWaypoint()
    {
        Blip? waypoint = World.WaypointBlip;
        if (waypoint == null || !waypoint.Exists())
        {
            Notification.PostTicker("~r~Телепорт:~s~ сначала поставьте точку на карте.", false);
            return;
        }

        GTA.Math.Vector3 position = waypoint.Position;
        if (!TryScaleCoordinate(position.X, out _) || !TryScaleCoordinate(position.Y, out _))
        {
            Notification.PostTicker("~r~Телепорт:~s~ метка вне игровой карты.", false);
            return;
        }

        BeginTeleport(position.X, position.Y, null, "метки");
    }

    private void RequestTeleportToPlayer(int targetClientId)
    {
        if (API.IsOnServer && targetClientId != API.LocalPlayerID)
        {
            long now = Stopwatch.GetTimestamp();
            if (_pendingPlayerTeleportToken != 0 &&
                (now - _playerTeleportRequestStarted) / (double)Stopwatch.Frequency <= 8)
            {
                Notification.PostTicker("~o~Телепорт:~s~ дождитесь текущего перемещения.", false);
                return;
            }

            _nextPlayerTeleportToken = unchecked(_nextPlayerTeleportToken + 1);
            if (_nextPlayerTeleportToken == 0) { _nextPlayerTeleportToken = 1; }
            _pendingPlayerTeleportToken = _nextPlayerTeleportToken;
            _playerTeleportRequestStarted = now;
            API.SendCustomEvent(TeleportToPlayerRequest, targetClientId, _pendingPlayerTeleportToken);
        }
    }

    private void ExpirePlayerTeleportRequest(long now)
    {
        if (_pendingPlayerTeleportToken == 0 ||
            (now - _playerTeleportRequestStarted) / (double)Stopwatch.Frequency <= 8) { return; }
        CancelPlayerTeleportRequest();
        Notification.PostTicker("~o~Телепорт:~s~ не получили ответ. Повторите попытку.", false);
    }

    private void CancelPlayerTeleportRequest()
    {
        _pendingPlayerTeleportToken = 0;
        _playerTeleportRequestStarted = 0;
    }

    private void BeginTeleport(float x, float y, float? expectedZ, string destination, bool forRecall = false)
    {
        CancelPlayerTeleportRequest();
        CancelPassengerTeleport(null);
        ClearTeleportFocus();
        if (!forRecall)
        {
            _pendingRecall = null;
            _recallRequests.Cancel();
        }

        _pendingTeleport = new PendingTeleport(x, y, expectedZ, destination, Stopwatch.GetTimestamp(), forRecall);
        Notification.PostTicker("~o~Телепорт:~s~ загружаем место назначения…", false);
    }

    private void ProcessPendingTeleport(long now)
    {
        PendingTeleport? pending = _pendingTeleport;
        if (pending == null)
        {
            return;
        }

        if ((now - pending.StartTick) / (double)Stopwatch.Frequency > 8.0)
        {
            FinishTeleport(pending, false, "место ещё не загрузилось. Попробуйте другую точку рядом.");
            return;
        }

        if ((now - pending.LastProbeTick) / (double)Stopwatch.Frequency < 0.15)
        {
            return;
        }

        pending.LastProbeTick = now;
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            FinishTeleport(pending, false, "персонаж сейчас недоступен.");
            return;
        }

        float hintZ = pending.ExpectedZ ?? 100f;
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pending.X, pending.Y, hintZ);
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pending.X, pending.Y, 1000f);
        if (!pending.FocusSet && GTA.Math.Vector3.Distance(player.Position,
                new GTA.Math.Vector3(pending.X, pending.Y, player.Position.Z)) > 150f)
        {
            Function.Call(Hash.SET_FOCUS_POS_AND_VEL, pending.X, pending.Y, hintZ, 0f, 0f, 0f);
            pending.FocusSet = true;
        }

        Vehicle? vehicle = player.CurrentVehicle;
        if (vehicle != null && vehicle.Exists() && vehicle.Driver?.Handle != player.Handle)
        {
            FinishTeleport(pending, false, "вы пассажир. Выйдите из машины и повторите.");
            return;
        }

        if (vehicle != null && vehicle.Exists() && !IsLocallyOwnedSyncedVehicle(vehicle))
        {
            FinishTeleport(pending, false, "эта машина не ваша. Выйдите и повторите.");
            return;
        }

        if (!TryFindArrival(pending, vehicle != null && vehicle.Exists(), out GTA.Math.Vector3 surface))
        {
            return;
        }

        Entity target = vehicle != null && vehicle.Exists() ? vehicle : player;
        GTA.Math.Vector3 arrival = new GTA.Math.Vector3(surface.X, surface.Y,
            surface.Z + (vehicle != null ? 2.0f : 1.2f));
        target.Position = arrival;
        target.Velocity = GTA.Math.Vector3.Zero;
        if (GTA.Math.Vector3.Distance(target.Position, arrival) < 10f)
        {
            FinishTeleport(pending, true, "вы на месте.");
        }
        else
        {
            FinishTeleport(pending, false, "игра не подтвердила перемещение.");
        }
    }

    private bool IsLocallyOwnedSyncedVehicle(Vehicle vehicle)
    {
        SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(vehicle.Handle);
        return synced?.IsLocal == true;
    }

    private static bool TryFindArrival(PendingTeleport pending, bool inVehicle, out GTA.Math.Vector3 surface)
    {
        surface = default;
        GTA.Math.Vector3 target = new GTA.Math.Vector3(pending.X, pending.Y, pending.ExpectedZ ?? 100f);

        if (pending.ForRecall)
        {
            // Check two positions per probe so navmesh queries do not stall a frame.
            for (int i = 0; i < 2; i++)
            {
                GTA.Math.Vector2 offset = RecallOffsets[pending.NextRecallOffset++ % RecallOffsets.Length];
                GTA.Math.Vector3 besideCar = new GTA.Math.Vector3(
                    target.X + offset.X, target.Y + offset.Y, target.Z);
                if (TryFindArrivalAt(besideCar, pending.ExpectedZ, false, 14f, out surface) &&
                    IsNearby(target, surface, 11f))
                {
                    return true;
                }
            }

            return false;
        }

        return TryFindArrivalAt(target, pending.ExpectedZ, inVehicle, 65f, out surface);
    }

    private static bool TryFindArrivalAt(GTA.Math.Vector3 target, float? expectedZ, bool inVehicle,
        float searchRadius, out GTA.Math.Vector3 surface)
    {
        surface = default;

        // A teammate or a parked car may stand on a bridge or roof far above terrain Z.
        // Probe the actual nearby collision rather than comparing it with terrain height.
        if (expectedZ.HasValue && !inVehicle &&
            TrySurfaceRay(target, expectedZ.Value + 12f, expectedZ.Value - 20f, out surface) &&
            Math.Abs(surface.Z - expectedZ.Value) <= (searchRadius < 20f ? 9f : 18f) && IsDry(surface))
        {
            return true;
        }

        if (inVehicle)
        {
            GTA.Math.Vector3 road = World.GetNextPositionOnStreet(target, unoccupied: true);
            if (IsNearby(target, road, searchRadius) && IsDry(road) &&
                (!expectedZ.HasValue || Math.Abs(road.Z - expectedZ.Value) <= 50f))
            {
                surface = road;
                return true;
            }
        }
        else if (World.GetSafePositionForPed(target, out GTA.Math.Vector3 safe) &&
                 IsNearby(target, safe, searchRadius) && IsDry(safe) &&
                 (!expectedZ.HasValue || Math.Abs(safe.Z - expectedZ.Value) <=
                     (searchRadius < 20f ? 9f : 20f)))
        {
            surface = safe;
            return true;
        }

        GTA.Math.Vector3 groundProbe = new GTA.Math.Vector3(target.X, target.Y, 1000f);
        if (World.GetGroundHeight(groundProbe, out float ground) &&
            ground >= -200f && ground <= 1000f && !float.IsNaN(ground) &&
            (!expectedZ.HasValue || Math.Abs(ground - expectedZ.Value) <=
                (searchRadius < 20f ? 9f : 25f)) &&
            IsDry(new GTA.Math.Vector3(target.X, target.Y, ground)))
        {
            surface = new GTA.Math.Vector3(target.X, target.Y, ground);
            return true;
        }

        return false;
    }

    private static bool TrySurfaceRay(GTA.Math.Vector3 target, float top, float bottom,
        out GTA.Math.Vector3 surface)
    {
        surface = default;
        RaycastResult hit = World.Raycast(
            new GTA.Math.Vector3(target.X, target.Y, top),
            new GTA.Math.Vector3(target.X, target.Y, bottom),
            IntersectFlags.Map | IntersectFlags.Objects);
        if (!hit.DidHit || hit.SurfaceNormal.Z < 0.55f ||
            float.IsNaN(hit.HitPosition.Z) || float.IsInfinity(hit.HitPosition.Z))
        {
            return false;
        }

        surface = hit.HitPosition;
        return true;
    }

    private static bool IsNearby(GTA.Math.Vector3 requested, GTA.Math.Vector3 candidate, float maxDistance)
    {
        float x = requested.X - candidate.X;
        float y = requested.Y - candidate.Y;
        return x * x + y * y <= maxDistance * maxDistance &&
            !float.IsNaN(candidate.Z) && !float.IsInfinity(candidate.Z);
    }

    private static bool IsDry(GTA.Math.Vector3 surface)
    {
        if (surface.Z < -200f || surface.Z > 1000f ||
            float.IsNaN(surface.Z) || float.IsInfinity(surface.Z))
        {
            return false;
        }

        OutputArgument height = new OutputArgument();
        if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, surface.X, surface.Y, surface.Z + 2f, height) &&
            height.GetResult<float>() >= surface.Z - 0.5f)
        {
            return false;
        }

        GTA.Math.Vector3 probe = new GTA.Math.Vector3(surface.X, surface.Y, 1000f);
        return !World.GetGroundHeight(probe, out float dryGround) ||
            !World.GetGroundHeight(probe, out float waterSurface,
                GetGroundHeightMode.ConsiderWaterAsGround) ||
            waterSurface <= dryGround + 0.5f || waterSurface <= surface.Z + 0.5f;
    }

    private void FinishTeleport(PendingTeleport pending, bool success, string message)
    {
        if (pending.FocusSet)
        {
            Function.Call(Hash.CLEAR_FOCUS);
        }

        _pendingTeleport = null;
        if (success && pending.ForRecall && _pendingRecall != null)
        {
            _pendingRecall.LastTeleportSucceeded = true;
        }

        Notification.PostTicker((success ? "~g~" : "~r~") + "Телепорт:~s~ " + message, false);
    }

    private void ClearTeleportFocus()
    {
        if (_pendingTeleport?.FocusSet == true)
        {
            Function.Call(Hash.CLEAR_FOCUS);
        }
    }

    private void RequestRepair()
    {
        Ped player = Game.Player.Character;
        if (!API.IsOnServer) return;
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Ремонт:~s~ дождитесь возрождения и повторите.", false);
            return;
        }

        Vehicle? current = player.CurrentVehicle;
        SyncedVehicle? target = current != null && current.Exists()
            ? RuntimeEntityLookup.GetVehicleByHandle(current.Handle) : null;
        if (current == null || !current.Exists())
        {
            // The local spawn event excludes remote traffic. Resolve the nearest
            // actual nearby car in RageCoop's shared table, regardless of who created it.
            float bestDistance = 8f * 8f;
            foreach (Vehicle candidate in World.GetNearbyVehicles(player.Position, 8f))
            {
                if (!candidate.Exists()) continue;
                float distance = (candidate.Position - player.Position).LengthSquared();
                if (distance >= bestDistance) continue;
                SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(candidate.Handle);
                if (synced == null) continue;
                target = synced;
                bestDistance = distance;
            }
        }

        if (target == null)
        {
            Notification.PostTicker("~o~Ремонт:~s~ сядьте в машину или подойдите к ней на 8 м. Если она только появилась, подождите секунду.", false);
            return;
        }
        Vehicle repairVehicle = target.MainVehicle;
        if (repairVehicle == null || !repairVehicle.Exists() || repairVehicle.Model.Hash == 0)
        {
            Notification.PostTicker("~o~Ремонт:~s~ машина ещё загружается. Повторите через секунду.", false);
            return;
        }
        if (_vehicles.TryGetValue(target.ID, out WatchedVehicle? watched))
            watched.LastSentTick = Stopwatch.GetTimestamp();
        API.SendCustomEvent(RepairRequest, target.ID, repairVehicle.Model.Hash);
    }

    private void RequestRecallCar()
    {
        CancelPlayerTeleportRequest();
        CancelPassengerTeleport(null);
        if (!API.IsOnServer)
        {
            return;
        }

        if (_pendingRecall != null || _pendingTeleport != null)
        {
            Notification.PostTicker("~o~Машина:~s~ дождитесь текущего перемещения.", false);
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~r~Машина:~s~ сначала дождитесь возрождения.", false);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastRecallRequestTick != 0 && (now - _lastRecallRequestTick) / (double)Stopwatch.Frequency < 1.5)
        {
            return;
        }

        _lastRecallRequestTick = now;
        API.SendCustomEvent(RecallCarRequest, _recallRequests.Start(now));
        Notification.PostTicker("~o~Машина:~s~ ищем вашу последнюю машину…", false);
    }

    private void ProcessPendingRecall(long now)
    {
        if (_recallRequests.TryExpire(now))
            Notification.PostTicker("~o~Машина:~s~ сервер не ответил. Повторите попытку.", false);
        PendingRecall? pending = _pendingRecall;
        if (pending == null)
        {
            return;
        }

        if ((now - pending.StartTick) / (double)Stopwatch.Frequency > 15.0)
        {
            FailRecall("машина не появилась рядом. Попробуйте ещё раз.");
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            FailRecall("сначала дождитесь возрождения.");
            return;
        }

        Vehicle? target = null;
        SyncedVehicle? recallVehicle = RuntimeEntityLookup.GetVehicleByID(pending.VehicleId);
        if (recallVehicle != null)
        {
            Vehicle localVehicle = recallVehicle.MainVehicle;
            if (localVehicle != null && localVehicle.Exists())
            {
                target = localVehicle;
            }
        }

        if (target != null && (target.IsDead || !target.IsDriveable))
        {
            FailRecall("машина уничтожена.");
            return;
        }

        Vehicle? current = player.CurrentVehicle;
        if (target != null && current != null && current.Exists() && current.Handle == target.Handle)
        {
            if (player.SeatIndex != VehicleSeat.Driver)
            {
                FailRecall("место водителя занято. Вы на пассажирском месте.");
                return;
            }
            _pendingRecall = null;
            Notification.PostTicker(pending.SeatAttempted
                ? "~g~Машина:~s~ вы за рулём."
                : "~g~Машина:~s~ вы уже в своей машине.", false);
            return;
        }

        if (pending.SeatAttempted)
        {
            if ((now - pending.SeatAttemptTick) / (double)Stopwatch.Frequency > 1.5)
            {
                pending.SeatAttempted = false;
                if (pending.TeleportAttempts == 0 && target != null &&
                    GTA.Math.Vector3.Distance(player.Position, target.Position) > 20f)
                {
                    pending.DirectSeatFailed = true;
                }
                else
                {
                    FailRecall("не удалось занять место водителя.");
                }
            }

            return;
        }

        if (current != null && current.Exists())
        {
            FailRecall("выйдите из текущей машины и повторите.");
            return;
        }

        if (target != null && !target.IsSeatFree(VehicleSeat.Driver))
        {
            FailRecall("место водителя занято.");
            return;
        }

        if (_pendingTeleport != null)
        {
            return;
        }

        if (pending.TeleportAttempts > 0 && !pending.LastTeleportSucceeded)
        {
            // The teleport handler already explained the surface or movement failure.
            _pendingRecall = null;
            return;
        }

        // A streamed, intact vehicle is a safer destination than guessed ground beside it.
        // If the engine cannot seat us at a distance, fall back to a checked landing nearby.
        if (target != null && (!pending.DirectSeatFailed ||
            (pending.TeleportAttempts > 0 && pending.LastTeleportSucceeded &&
             GTA.Math.Vector3.Distance(player.Position, target.Position) <= 25f)))
        {
            player.SetIntoVehicle(target, VehicleSeat.Driver);
            pending.SeatAttempted = true;
            pending.SeatAttemptTick = now;
            return;
        }

        if (pending.TeleportAttempts > 0 &&
            GTA.Math.Vector3.Distance(player.Position,
                new GTA.Math.Vector3(pending.LastTeleportX, pending.LastTeleportY, player.Position.Z)) > 20f)
        {
            FailRecall("не удалось добраться до машины. Попробуйте ещё раз.");
            return;
        }

        if (target == null)
        {
            GTA.Math.Vector3 reported = pending.ReportedPosition;
            if (pending.TeleportAttempts == 0 &&
                GTA.Math.Vector3.Distance(player.Position, reported) > 20f)
            {
                StartRecallTeleport(pending, reported);
            }

            return;
        }

        if (pending.TeleportAttempts >= 2)
        {
            FailRecall("машина переместилась; попробуйте ещё раз.");
            return;
        }

        StartRecallTeleport(pending, target.Position);
    }

    private void StartRecallTeleport(PendingRecall pending, GTA.Math.Vector3 carPosition)
    {
        // The teleport probe tries several dry landing points around this centre.
        float x = carPosition.X;
        float y = carPosition.Y;
        if (!TryScaleCoordinate(x, out _) || !TryScaleCoordinate(y, out _))
        {
            FailRecall("координаты машины недоступны.");
            return;
        }

        pending.TeleportAttempts++;
        pending.LastTeleportSucceeded = false;
        pending.LastTeleportX = x;
        pending.LastTeleportY = y;
        BeginTeleport(x, y, carPosition.Z, "машине", forRecall: true);
    }

    private void FailRecall(string reason)
    {
        _pendingRecall = null;
        API.LocalChatMessage("Совместная игра", "Машина: " + reason);
    }

    private void OnVehicleSpawned(object? sender, SyncedVehicle vehicle)
    {
        API.QueueAction(() =>
        {
            if (_active && !_stopping && API.IsOnServer && vehicle.IsLocal &&
                RuntimeEntityLookup.IsCurrentVehicle(vehicle))
            {
                _vehicles[vehicle.ID] = new WatchedVehicle(vehicle);
            }
        });
    }

    private void OnVehicleDeleted(object? sender, SyncedVehicle vehicle)
    {
        API.QueueAction(() =>
        {
            if (_vehicles.TryGetValue(vehicle.ID, out WatchedVehicle? watched) &&
                ReferenceEquals(watched.Synced, vehicle))
            {
                _vehicles.Remove(vehicle.ID);
            }
        });
    }

    private void DetectRepairs(long now)
    {
        if (RuntimeEntityLookup.IsAvailable)
        {
            foreach (int id in _vehicles.Where(pair => !RuntimeEntityLookup.IsCurrentVehicle(pair.Value.Synced))
                         .Select(pair => pair.Key).ToArray())
            {
                _vehicles.Remove(id);
            }
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists())
        {
            return;
        }

        Vehicle? currentVehicle = player.CurrentVehicle;
        GTA.Math.Vector3 playerPosition = player.Position;
        if (currentVehicle != null && currentVehicle.Exists()) TrackLocalVehicle(currentVehicle);
        foreach (Vehicle nearby in World.GetNearbyVehicles(playerPosition, 20f)) TrackLocalVehicle(nearby);
        foreach (WatchedVehicle watched in _vehicles.Values)
        {
            if (!watched.Synced.IsLocal)
            {
                watched.WasLocal = false;
                watched.Previous = null;
                continue;
            }

            Vehicle vehicle = watched.Synced.MainVehicle;
            if (vehicle == null || !vehicle.Exists())
            {
                watched.Previous = null;
                continue;
            }

            bool isCurrent = currentVehicle != null && currentVehicle.Exists() && currentVehicle.Handle == vehicle.Handle;
            GTA.Math.Vector3 offset = vehicle.Position - playerPosition;
            if (!isCurrent && offset.LengthSquared() > 400f)
            {
                watched.Previous = null;
                watched.WasLocal = false;
                continue;
            }

            DamageSample current = DamageSample.Take(vehicle);
            if (!watched.WasLocal)
            {
                watched.WasLocal = true;
                watched.VehicleHandle = vehicle.Handle;
                watched.Previous = current;
                continue;
            }

            if (watched.VehicleHandle != vehicle.Handle)
            {
                watched.VehicleHandle = vehicle.Handle;
                watched.Previous = current;
                continue;
            }

            if (watched.Previous.HasValue && watched.Previous.Value.IsDamaged && current.IsPristine &&
                (now - watched.LastSentTick) / (double)Stopwatch.Frequency >= 8)
            {
                watched.LastSentTick = now;
                API.SendCustomEvent(RepairRequest, watched.Synced.ID, vehicle.Model.Hash);
            }

            watched.Previous = current;
        }
    }

    private void TrackLocalVehicle(Vehicle vehicle)
    {
        if (!vehicle.Exists()) return;
        SyncedVehicle? synced = RuntimeEntityLookup.GetVehicleByHandle(vehicle.Handle);
        if (synced?.IsLocal != true || synced.ID == 0) return;
        if (!_vehicles.TryGetValue(synced.ID, out WatchedVehicle? existing) ||
            !ReferenceEquals(existing.Synced, synced))
        {
            _vehicles[synced.ID] = new WatchedVehicle(synced);
        }
    }

    private void OnStatusReceived(CustomEventReceivedArgs e)
    {
        if (_stopping || e.Args.Length != 1 || e.Args[0] is not string message)
        {
            return;
        }

        string safeMessage = message.Length > 160 ? message.Substring(0, 160) : message;
        safeMessage = safeMessage.Replace("~", string.Empty);
        API.QueueAction(() =>
        {
            if (_active && !_stopping)
            {
                Notification.PostTicker("~o~Совместная игра:~s~ " + safeMessage, false);
            }
        });
    }

    private void OnWeatherReceived(CustomEventReceivedArgs e)
    {
        if (_stopping || e.Args.Length != 1)
        {
            return;
        }

        int index;
        try
        {
            index = Convert.ToInt32(e.Args[0]);
        }
        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
        {
            return;
        }

        if (index < 0 || index >= WeatherNames.Length)
        {
            return;
        }

        API.QueueAction(() =>
        {
            if (!_active || _stopping)
            {
                return;
            }

            for (int i = 0; i < _weatherItems.Count; i++)
            {
                _weatherItems[i].AltTitle = i == index ? "Сейчас" : string.Empty;
            }

            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, WeatherCodes[index]);
            Notification.PostTicker("~o~Погода:~s~ " + WeatherNames[index] + ".", false);
        });
    }

    private void OnTimeReceived(CustomEventReceivedArgs e)
    {
        if (_stopping || !API.IsOnServer || e.Args.Length != 2 || e.Args[0] is not int hour || e.Args[1] is not int minute ||
            hour < 0 || hour > 23 || minute < 0 || minute > 59)
        {
            return;
        }

        API.QueueAction(() =>
        {
            if (!_active || _stopping || !API.IsOnServer)
            {
                return;
            }

            Function.Call(Hash.SET_CLOCK_TIME, hour, minute, 0);
            if (_currentTimeItem != null)
            {
                _currentTimeItem.AltTitle = FormatTime(hour, minute);
            }

            _timeSelection.Confirm(hour, minute);
            UpdateTimeRows();

            Notification.PostTicker("~o~Время:~s~ " + FormatTime(hour, minute) + ".", false);
        });
    }

    private void OnWaypointReceived(CustomEventReceivedArgs e)
    {
        if (_stopping || e.Args.Length != 2 || e.Args[0] is not int x || e.Args[1] is not int y ||
            !IsValidScaledCoordinate(x) || !IsValidScaledCoordinate(y))
        {
            return;
        }

        API.QueueAction(() =>
        {
            if (!_active || _stopping)
            {
                return;
            }

            World.WaypointPosition = new GTA.Math.Vector3(x / 100f, y / 100f, 0f);
            Notification.PostTicker("~o~Общая метка:~s~ появилась на вашей карте.", false);
        });
    }

    private void OnTeleportToPlayerReceived(CustomEventReceivedArgs e)
    {
        if (_stopping || e.Args.Length != 4 || e.Args[0] is not int token || token == 0 ||
            e.Args[1] is not int x || e.Args[2] is not int y || e.Args[3] is not int z ||
            !IsValidScaledCoordinate(x) || !IsValidScaledCoordinate(y) ||
            !IsValidScaledCoordinate(z))
        {
            return;
        }

        API.QueueAction(() =>
        {
            if (_active && !_stopping && API.IsOnServer && token == _pendingPlayerTeleportToken &&
                _playerTeleportRequestStarted != 0 &&
                (Stopwatch.GetTimestamp() - _playerTeleportRequestStarted) / (double)Stopwatch.Frequency <= 8)
            {
                BeginTeleport(x / 100f, y / 100f, z / 100f, "напарнику");
            }
        });
    }

    private void OnRecallCarReceived(CustomEventReceivedArgs e)
    {
        if (_stopping)
        {
            return;
        }

        API.QueueAction(() =>
        {
            long now = Stopwatch.GetTimestamp();
            if (_active && !_stopping && API.IsOnServer &&
                _recallRequests.TryAccept(e.Args, now, out RecallReply reply))
            {
                if (reply.Error != null)
                {
                    Notification.PostTicker("~o~Машина:~s~ " + reply.Error.Replace("~", string.Empty), false);
                    return;
                }
                if (_pendingTeleport != null || _pendingRecall != null || _passengerTeleport != null ||
                    _passengerTargetId != 0) return;
                _pendingRecall = new PendingRecall(reply.VehicleId,
                    new GTA.Math.Vector3(reply.X / 100f, reply.Y / 100f, reply.Z / 100f), now);
                ProcessPendingRecall(now);
            }
        });
    }

    private void SetWantedItem(int level)
    {
        if (_wantedItem == null)
        {
            return;
        }

        _updatingWantedItem = true;
        try
        {
            _wantedItem.SelectedIndex = Math.Max(0, Math.Min(5, level));
        }
        finally
        {
            _updatingWantedItem = false;
        }
    }

    private void SetNoWanted(bool enabled)
    {
        if (_noWanted == enabled)
        {
            return;
        }

        if (!enabled)
        {
            DisableNoWanted();
            return;
        }

        Wanted wanted = Game.Player.Wanted;
        _policeBackOffBeforeNoWanted = wanted.PoliceBackOff;
        _noWanted = true;
        wanted.SetPoliceIgnorePlayer(true);
        ClearLocalWantedLevel();
        if (_wantedItem != null)
        {
            _wantedItem.Enabled = false;
        }

        SetWantedItem(0);
    }

    private void DisableNoWanted()
    {
        if (!_noWanted)
        {
            return;
        }

        _noWanted = false;
        Game.Player.Wanted.SetPoliceIgnorePlayer(_policeBackOffBeforeNoWanted);
        if (_wantedItem != null)
        {
            _wantedItem.Enabled = true;
        }

        if (_noWantedItem?.Checked == true)
        {
            _noWantedItem.Checked = false;
        }
    }

    private void SetInvincibility(bool enabled)
    {
        if (!enabled)
        {
            DisableInvincibility();
            return;
        }

        _invincible = true;
        ApplyInvincibility();
    }

    private void ApplyInvincibility()
    {
        Ped character = Game.Player.Character;
        if (character == null || !character.Exists())
        {
            return;
        }

        if (_protectedPed == null || !_protectedPed.Exists() || _protectedPed.Handle != character.Handle)
        {
            RestoreProtectedPed();
            _protectedPed = character;
            _originalInvincible = character.IsInvincible;
        }

        if (!character.IsInvincible)
        {
            character.IsInvincible = true;
        }
    }

    private void DisableInvincibility()
    {
        _invincible = false;
        RestoreProtectedPed();
        if (_invincibleItem?.Checked == true)
        {
            _invincibleItem.Checked = false;
        }
    }

    private void RestoreProtectedPed()
    {
        if (_protectedPed != null && _protectedPed.Exists())
        {
            _protectedPed.IsInvincible = _originalInvincible;
        }

        _protectedPed = null;
    }

    private static void ClearLocalWantedLevel()
    {
        Wanted wanted = Game.Player.Wanted;
        wanted.SetWantedLevel(0, false);
        wanted.ApplyWantedLevelChangeNow(false);
        if (wanted.WantedLevel != 0)
        {
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player.Handle);
        }
    }

    private void SetLocalWantedLevel(int level)
    {
        if (_noWanted)
        {
            return;
        }

        Wanted wanted = Game.Player.Wanted;
        if (wanted.WantedLevel == level)
        {
            return;
        }

        wanted.SetWantedLevel(level, false);
        wanted.ApplyWantedLevelChangeNow(false);
        if (level == 0 && wanted.WantedLevel != 0)
        {
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player.Handle);
        }
    }

    private readonly struct VehicleEntry
    {
        public VehicleEntry(int hash, string code, string displayName, int vehicleClass)
        {
            Hash = hash;
            Code = code;
            DisplayName = displayName;
            VehicleClass = vehicleClass;
        }

        public int Hash { get; }
        public string Code { get; }
        public string DisplayName { get; }
        public int VehicleClass { get; }
    }

    private sealed class WatchedVehicle
    {
        public WatchedVehicle(SyncedVehicle synced)
        {
            Synced = synced;
        }

        public SyncedVehicle Synced { get; }
        public int VehicleHandle { get; set; }
        public bool WasLocal { get; set; }
        public DamageSample? Previous { get; set; }
        public long LastSentTick { get; set; }
    }

    private sealed class PendingTeleport
    {
        public PendingTeleport(float x, float y, float? expectedZ, string destination, long startTick, bool forRecall)
        {
            X = x;
            Y = y;
            ExpectedZ = expectedZ;
            Destination = destination;
            StartTick = startTick;
            ForRecall = forRecall;
        }

        public float X { get; }
        public float Y { get; }
        public float? ExpectedZ { get; }
        public string Destination { get; }
        public long StartTick { get; }
        public bool ForRecall { get; }
        public long LastProbeTick { get; set; }
        public bool FocusSet { get; set; }
        public int NextRecallOffset { get; set; }
    }

    private sealed class PendingRecall
    {
        public PendingRecall(int vehicleId, GTA.Math.Vector3 reportedPosition, long startTick)
        {
            VehicleId = vehicleId;
            ReportedPosition = reportedPosition;
            StartTick = startTick;
        }

        public int VehicleId { get; }
        public GTA.Math.Vector3 ReportedPosition { get; }
        public long StartTick { get; }
        public int TeleportAttempts { get; set; }
        public float LastTeleportX { get; set; }
        public float LastTeleportY { get; set; }
        public bool SeatAttempted { get; set; }
        public bool DirectSeatFailed { get; set; }
        public long SeatAttemptTick { get; set; }
        public bool LastTeleportSucceeded { get; set; }
    }

    private readonly struct DamageSample
    {
        private DamageSample(float bodyHealth, float engineHealth, int brokenDoors, bool windowsIntact)
        {
            BodyHealth = bodyHealth;
            EngineHealth = engineHealth;
            BrokenDoors = brokenDoors;
            WindowsIntact = windowsIntact;
        }

        private float BodyHealth { get; }
        private float EngineHealth { get; }
        private int BrokenDoors { get; }
        private bool WindowsIntact { get; }

        public bool IsDamaged => BodyHealth < 950f || EngineHealth < 950f || BrokenDoors != 0 || !WindowsIntact;
        public bool IsPristine => BodyHealth >= 990f && EngineHealth >= 990f && BrokenDoors == 0 && WindowsIntact;

        public static DamageSample Take(Vehicle vehicle)
        {
            int brokenDoors = 0;
            foreach (VehicleDoor door in vehicle.Doors)
            {
                if (door.IsBroken)
                {
                    brokenDoors++;
                }
            }

            return new DamageSample(vehicle.BodyHealth, vehicle.EngineHealth, brokenDoors, vehicle.Windows.AllWindowsIntact);
        }
    }
}
