using GTA;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    // Outdoor coordinates: gta5.photography/manuals/teleporting;
    // airfields: gtahash.com/tools/coord and vice-forge.com/tools/fivem-teleport-coordinates.
    // They remain hints: the existing teleport pipeline loads and validates collision.
    private static readonly TeleportLocation[] InterestingLocations =
    {
        new TeleportLocation("Город", "Пирс Дель-Перро", "Парк аттракционов у океана. Пешком.", -1850.127f, -1231.751f, 13.017f, true),
        new TeleportLocation("Город", "Пляжный скейт-парк", "Открытая площадка на пляже.", -1374.881f, -1398.835f, 6.141f),
        new TeleportLocation("Город", "Миррор-Парк", "Парк у озера в восточном Лос-Сантосе.", 1070.206f, -711.958f, 58.483f),
        new TeleportLocation("Город", "Амфитеатр Вайнвуд", "Сцена открытого амфитеатра. Пешком.", 686.245f, 577.950f, 130.461f, true),
        new TeleportLocation("Природа и виды", "Вершина Чилиад", "Смотровая точка на горе. Пешком.", 450.718f, 5566.614f, 806.183f, true),
        new TeleportLocation("Природа и виды", "Знак Вайнвуд", "Склон рядом со знаменитой надписью. Пешком.", 711.362f, 1198.134f, 348.526f, true),
        new TeleportLocation("Природа и виды", "Плотина Лэнд-Экт", "Смотровая площадка у плотины. Пешком.", 1660.369f, -12.013f, 170.020f, true),
        new TeleportLocation("Природа и виды", "Маяк Эль-Гордо", "Маяк на восточном побережье. Пешком.", 3430.155f, 5174.196f, 41.280f, true),
        new TeleportLocation("За городом", "Виноградники Марлоу", "Холмы и виноградники.", -1868.971f, 2095.674f, 139.115f),
        new TeleportLocation("За городом", "Лагерь хиппи", "Раскрашенный холм в пустыне.", 2476.712f, 3789.645f, 41.226f),
        new TeleportLocation("За городом", "Карьер", "Открытый карьер в восточной части карты.", 2954.196f, 2783.410f, 41.004f),
        new TeleportLocation("За городом", "Радиостанция Rebel Radio", "Радиостанция в пустыне.", 736.153f, 2583.143f, 79.634f),
        new TeleportLocation("Аэродромы", "Аэропорт Лос-Сантоса", "Подъезд к терминалу аэропорта.", -1037.700f, -2737.700f, 20.160f),
        new TeleportLocation("Аэродромы", "Аэродром Сэнди-Шорс", "Открытая территория аэродрома.", 1710.200f, 3256.000f, 41.100f)
    };

    private void BuildTeleportMenu()
    {
        if (_mainMenu == null || _pool == null)
        {
            return;
        }

        NativeMenu teleports = CreateMenu("Совместная игра", "Телепорты");
        _mainMenu.AddSubMenu(teleports);
        _pool.Add(teleports);

        NativeItem waypoint = new NativeItem("К моей метке", "Перенестись к метке на карте.");
        waypoint.Activated += (_, _) => TeleportToWaypoint();
        teleports.Add(waypoint);
        NativeItem recall = new NativeItem("К моей машине", "Вернуться к целой машине и сесть за руль.");
        recall.Activated += (_, _) => RequestRecallCar();
        teleports.Add(recall);

        NativeMenu places = CreateMenu("Совместная игра", "Интересные места");
        teleports.AddSubMenu(places);
        _pool.Add(places);
        string? previousCategory = null;
        NativeMenu? category = null;
        foreach (TeleportLocation location in InterestingLocations)
        {
            if (location.Category != previousCategory)
            {
                category = CreateMenu("Совместная игра", location.Category);
                places.AddSubMenu(category);
                _pool.Add(category);
                previousCategory = location.Category;
            }

            NativeItem item = new NativeItem(location.Name, location.Description);
            item.Activated += (_, _) => TeleportToLocation(location);
            category?.Add(item);
        }
    }

    private void TeleportToLocation(TeleportLocation location)
    {
        if (!API.IsOnServer)
        {
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            Notification.PostTicker("~o~Телепорт:~s~ сначала дождитесь возрождения.", false);
            return;
        }

        if (_pendingTeleport != null || _pendingRecall != null)
        {
            Notification.PostTicker("~o~Телепорт:~s~ дождитесь текущего перемещения.", false);
            return;
        }

        if (location.OnFootOnly && player.IsInVehicle())
        {
            Notification.PostTicker("~o~Телепорт:~s~ в это место можно пешком. Выйдите из транспорта.", false);
            return;
        }

        BeginTeleport(location.X, location.Y, location.Z, "месту «" + location.Name + "»");
    }

    private sealed class TeleportLocation
    {
        internal TeleportLocation(string category, string name, string description,
            float x, float y, float z, bool onFootOnly = false)
        {
            Category = category;
            Name = name;
            Description = description;
            X = x;
            Y = y;
            Z = z;
            OnFootOnly = onFootOnly;
        }

        internal string Category { get; }
        internal string Name { get; }
        internal string Description { get; }
        internal float X { get; }
        internal float Y { get; }
        internal float Z { get; }
        internal bool OnFootOnly { get; }
    }
}
