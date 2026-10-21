using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using GTA;
using GTA.UI;
using LemonUI.Menus;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private readonly List<PeerSnapshot> _peers = new List<PeerSnapshot>();
    private readonly Dictionary<int, NativeItem> _peerRows = new Dictionary<int, NativeItem>();
    private NativeMenu? _peersMenu;
    private NativeItem? _peerEmptyItem;
    private long _lastPeerSnapshot;

    private void BuildPlayersMenu()
    {
        if (_pool == null || _mainMenu == null) return;
        _peersMenu = CreateMenu("Совместная игра", "Напарники");
        _peersMenu.Description = "Выбрать игрока и переместиться к нему.";
        _mainMenu.AddSubMenu(_peersMenu);
        _peersMenu.Shown += (_, _) =>
        {
            if (!API.IsOnServer) return;
            UpdatePeerSnapshots();
            RefreshPeerRows();
        };
        _pool.Add(_peersMenu);
    }

    private void RequestPeerTeleport(int peerId)
    {
        if (!API.IsOnServer) return;
        if (!PlayerSnapshots.TryGet(out Dictionary<int, RageCoop.Client.Player> players))
        {
            Notification.PostTicker("~o~Список игроков обновляется. Повторите действие.", false);
            return;
        }
        if (!players.ContainsKey(peerId))
        {
            API.LocalChatMessage("Совместная игра", "Игрок уже отключился. Выберите другого напарника.");
            return;
        }
        RequestPassengerTeleport(peerId);
    }

    private void ProcessPlayersMenu(long now)
    {
        if (_peersMenu?.Visible != true) { return; }
        if ((now - _lastPeerSnapshot) / (double)Stopwatch.Frequency >= 0.5)
        {
            _lastPeerSnapshot = now;
            UpdatePeerSnapshots();
            RefreshPeerRows();
        }
    }

    private void UpdatePeerSnapshots()
    {
        if (!PlayerSnapshots.TryGet(out Dictionary<int, RageCoop.Client.Player> players)) return;
        Ped local = Game.Player.Character;
        if (local == null || !local.Exists()) return;
        _peers.Clear();
        GTA.Math.Vector3 localPosition = local.Position;
        // API.Players creates a dictionary copy. Read it once per snapshot,
        // and only read a streamed ped after checking it really is this player.
        foreach (KeyValuePair<int, RageCoop.Client.Player> entry in players)
        {
            RageCoop.Client.Player peer = entry.Value;
            if (entry.Key == API.LocalPlayerID) continue;
            float distance = peer.Position.DistanceTo(localPosition);
            string state = "Далеко";
            Ped? ped = RuntimeEntityLookup.GetPlayerPed(peer.ID)?.MainPed;
            if (ped != null && ped.Exists())
                state = ped.IsDead ? "Возрождается" : ped.IsInVehicle() ? "В транспорте" : "Пешком";
            else if (distance < 150f) state = "Загружается";
            float latency = peer.Ping;
            int ping = float.IsNaN(latency) || float.IsInfinity(latency) || latency < 0 ? -1 : (int)Math.Min(latency * 1000f, 9999f);
            _peers.Add(new PeerSnapshot(peer.ID, CleanPlayerName(peer.Username), distance, state, ping));
        }
        _peers.Sort((first, second) =>
        {
            int compare = first.Distance.CompareTo(second.Distance);
            return compare != 0 ? compare : first.Id.CompareTo(second.Id);
        });
    }

    private void RefreshPeerRows()
    {
        if (_peersMenu == null) return;
        bool rosterChanged = _peerRows.Count != _peers.Count || _peers.Any(peer => !_peerRows.ContainsKey(peer.Id));
        if (rosterChanged || (_peerEmptyItem == null && _peers.Count == 0))
        {
            int? selectedId = _peerRows.Where(pair => ReferenceEquals(pair.Value, _peersMenu.SelectedItem))
                .Select(pair => (int?)pair.Key).FirstOrDefault();
            int selectedIndex = _peersMenu.SelectedIndex;
            _peersMenu.Clear();
            _peerRows.Clear();
            _peerEmptyItem = null;
            foreach (PeerSnapshot peer in _peers)
            {
                int id = peer.Id;
                NativeItem item = new NativeItem("К " + peer.Name);
                item.Activated += (_, _) => RequestPeerTeleport(id);
                _peerRows.Add(id, item);
                _peersMenu.Add(item);
            }
            if (_peers.Count == 0)
            {
                _peerEmptyItem = new NativeItem("Вы пока одни", "Напарники появятся здесь после подключения.") { Enabled = false };
                _peersMenu.Add(_peerEmptyItem);
            }
            if (selectedId.HasValue && _peerRows.TryGetValue(selectedId.Value, out NativeItem? selected))
                _peersMenu.SelectedItem = selected;
            else if (_peersMenu.Items.Count > 0)
                _peersMenu.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, _peersMenu.Items.Count - 1));
        }
        // Do not reorder rows as players move while someone is choosing a target.
        foreach (PeerSnapshot peer in _peers)
        {
            NativeItem item = _peerRows[peer.Id];
            item.Title = "К " + peer.Name;
            item.AltTitle = FormatPeerDistance(peer.Distance);
            item.Description = "На свободное пассажирское место или рядом. За рулём своей машины — вместе с ней. " +
                peer.State + ". " + (peer.Ping >= 0 ? "Задержка: " + peer.Ping + " мс." : "Связь устанавливается.");
        }
    }

    private static string CleanPlayerName(string? name)
    {
        string clean = new string((name ?? "Игрок").Where(character => !char.IsControl(character) && character != '~').Take(20).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Игрок" : clean;
    }

    private static string FormatPeerDistance(float distance) =>
        float.IsNaN(distance) || float.IsInfinity(distance) ? "—" : distance >= 1000f
            ? (distance / 1000f).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " км"
            : Math.Max(0, (int)distance).ToString(CultureInfo.InvariantCulture) + " м";

    private void CleanupPlayersUi()
    {
        _peers.Clear();
        _lastPeerSnapshot = 0;
    }

    private sealed class PeerSnapshot
    {
        internal PeerSnapshot(int id, string name, float distance, string state, int ping)
        { Id = id; Name = name; Distance = distance; State = state; Ping = ping; }
        internal int Id { get; }
        internal string Name { get; }
        internal float Distance { get; }
        internal string State { get; }
        internal int Ping { get; }
    }
}
