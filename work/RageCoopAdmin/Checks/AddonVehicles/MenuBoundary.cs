using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

// Only the GTA, menu and RageCoop boundaries are replaced. The catalog, menu,
// availability checks, preloading and favorites below are the production sources.
internal static class GameBoundary
{
    internal static readonly HashSet<int> Installed = new HashSet<int>();
    internal static readonly List<int> Requested = new List<int>();
    internal static readonly List<int> Released = new List<int>();
    internal static readonly List<int> Spawned = new List<int>();
    internal static readonly List<string> Notifications = new List<string>();

    internal static void Reset()
    {
        Installed.Clear();
        Requested.Clear();
        Released.Clear();
        Spawned.Clear();
        Notifications.Clear();
    }
}

namespace GTA
{
    internal readonly struct Model
    {
        internal Model(int hash) { Hash = hash; }
        internal int Hash { get; }
        internal bool IsInCdImage => GameBoundary.Installed.Contains(Hash);
        internal bool IsVehicle => IsInCdImage;
        internal void Request() => GameBoundary.Requested.Add(Hash);
        internal void MarkAsNoLongerNeeded() => GameBoundary.Released.Add(Hash);
    }

    internal sealed class Ped { internal Vehicle? CurrentVehicle { get; set; } }
    internal sealed class Vehicle
    {
        internal Model Model { get; set; }
        internal bool Exists() => true;
    }
    internal sealed class Player { internal Ped Character { get; } = new Ped(); }
    internal static class Game { internal static Player Player { get; } = new Player(); }
}

namespace GTA.UI
{
    internal static class Notification
    {
        internal static void PostTicker(string message, bool _) => GameBoundary.Notifications.Add(message);
    }
}

namespace RageCoop.Client
{
    internal static class StringHash
    {
        internal static uint AtStringHash(string text)
        {
            uint hash = 0;
            foreach (char character in text.ToLowerInvariant())
            {
                hash += character;
                hash += hash << 10;
                hash ^= hash >> 6;
            }
            hash += hash << 3;
            hash ^= hash >> 11;
            return hash + (hash << 15);
        }
    }
}

namespace RageCoop.Client.Scripting
{
    internal static class API { internal static bool IsOnServer { get; set; } = true; }
}

namespace LemonUI.Menus
{
    using LemonUI.Elements;

    internal class NativeItem
    {
        protected readonly ScaledText title;
        internal NativeItem(string title, string description)
        { this.title = new ScaledText(title, 0.345f); Description = description; }
        internal string Title { get => title.Text; set => title.Text = value; }
        internal string Description { get; }
        internal string AltTitle { get; set; } = string.Empty;
        internal ScaledTexture? LeftBadge { get; set; }
        internal ScaledTexture? RightBadge { get; set; }
        internal bool Enabled { get; set; } = true;
        public virtual void Recalculate(PointF position, SizeF size, bool selected)
        { title.Position = new PointF(position.X + 6f, position.Y + 3f); }
        internal event EventHandler? Selected;
        internal event EventHandler? Activated;
        internal void Select() => Selected?.Invoke(this, EventArgs.Empty);
        internal void Activate() { if (Enabled) { Activated?.Invoke(this, EventArgs.Empty); } }
    }

    internal class NativeListItem<T> : NativeItem
    {
        protected readonly ScaledText text = new ScaledText(string.Empty, 0.35f);
        private List<T> _items;
        private int _selectedIndex;
        internal NativeListItem(string title, string description, params T[] values) : base(title, description)
        { _items = values.ToList(); UpdateIndex(); }
        internal ScaledTexture LeftArrow { get; } = new ScaledTexture();
        internal ScaledTexture RightArrow { get; } = new ScaledTexture();
        internal List<T> Items { get => _items; set { _items = value; UpdateIndex(); } }
        internal int SelectedIndex { get => _selectedIndex; set { _selectedIndex = value; UpdateIndex(); } }
        private void UpdateIndex()
        {
            text.Text = Items.Count == 0 ? string.Empty : Items[SelectedIndex]?.ToString() ?? string.Empty;
            text.Position = new PointF(RightArrow.Position.X - text.Width + 3f, text.Position.Y);
            LeftArrow.Position = new PointF(text.Position.X - LeftArrow.Size.Width, LeftArrow.Position.Y);
        }
        public override void Recalculate(PointF position, SizeF size, bool selected)
        {
            base.Recalculate(position, size, selected);
            LeftArrow.Size = RightArrow.Size = selected && Enabled ? new SizeF(30f, 30f) : SizeF.Empty;
            RightArrow.Position = new PointF(position.X + size.Width - RightArrow.Size.Width - 5f, position.Y + 4f);
            text.Position = new PointF(position.X + size.Width - RightArrow.Size.Width - 1f - text.Width, position.Y + 3f);
            LeftArrow.Position = new PointF(text.Position.X - LeftArrow.Size.Width, position.Y + 4f);
        }
    }

    internal sealed class NativeMenu
    {
        internal List<NativeItem> Items { get; } = new List<NativeItem>();
        internal NativeItem? SelectedItem { get; set; }
        internal bool Visible { get; set; }
        internal void Add(NativeItem item) => Items.Add(item);
        internal void AddSubMenu(NativeMenu _) { }
        internal void Clear() => Items.Clear();
    }
}

namespace LemonUI.Elements
{
    // Deliberately proportional boundary metrics; actual SDK geometry is checked
    // separately by EngineComposition. These are not measurements from GTA.
    internal sealed class ScaledText
    {
        internal ScaledText(string text, float scale) { Text = text; Scale = scale; }
        internal string Text { get; set; }
        internal float Scale { get; }
        internal PointF Position { get; set; }
        internal float Width => Text.Sum(character => character is 'W' or 'M' or 'Ж' or 'Ш' ? 13f :
            character is 'i' or 'l' or ' ' ? 4f : 9f) * Scale / 0.345f;
    }

    internal sealed class ScaledTexture
    {
        internal PointF Position { get; set; }
        internal SizeF Size { get; set; }
    }
}

namespace RageCoopAdmin.Client
{
    using LemonUI.Menus;

    public sealed partial class AdminMenuScript
    {
        private readonly List<NativeMenu> _pool = new List<NativeMenu>();
        private readonly NativeMenu _vehicleMenu = new NativeMenu();
        private readonly NativeMenu _searchMenu = new NativeMenu();
        private readonly NativeMenu _presetsMenu = new NativeMenu();
        private readonly Dictionary<int, NativeMenu> _classMenus = new Dictionary<int, NativeMenu>();
        private readonly List<VehicleEntry> _catalog = new List<VehicleEntry>();
        private static NativeMenu CreateMenu(string _, string __) => new NativeMenu();
        private static VehicleEntry? DescribeVehicle(int _) => null;
        private static void SendSpawn(int hash) => GameBoundary.Spawned.Add(hash);

        private static class Logger { internal static void Warning(string _) { } }
        private static class CurrentResource { internal static string DataFolder { get; set; } = string.Empty; }
        private readonly struct VehicleEntry
        {
            internal VehicleEntry(int hash, string code, string name, int vehicleClass)
            { Hash = hash; Code = code; DisplayName = name; }
            internal int Hash { get; }
            internal string Code { get; }
            internal string DisplayName { get; }
        }

        internal IReadOnlyList<NativeItem> AddonRows => _addonVehiclesMenu?.Items ?? throw new InvalidOperationException();
        internal bool HasAddonMenu => _addonVehiclesMenu != null;
        internal int RegisteredMenuCount => _pool.Count;
        internal IReadOnlyList<NativeItem> FavoriteRows => _favoritesMenu?.Items ?? throw new InvalidOperationException();
        internal bool HasPreload => _preloadedSpawnModel.HasValue;
        internal bool FavoritesDirty => _favoritesDirty;
        internal bool SpawnMenuVisible => IsSpawnMenuVisible();
        internal bool HasFavorite(int hash) => _favoriteHashes.Contains(hash);
        internal int FavoriteHash(NativeItem item) => item is NativeListItem<string> row &&
            _vehicleRows.TryGetValue(row, out int hash) ? hash : _unavailableFavoriteRows[item];
        internal bool CanSpawn(int hash) => EnsureSpawnModelAvailable(hash);
        internal void Preload(int hash) => PreloadSpawnModel(hash);
        internal void ShowAddons(bool visible)
        { if (_addonVehiclesMenu != null) { _addonVehiclesMenu.Visible = visible; } }
        internal void ClosePreload() => ReleaseSpawnModelPreload();
        internal void Toggle(int hash) => ToggleFavorite(hash);
        internal void RefreshFavorites() => RefreshFavoritesMenu();
        internal void Start(string directory)
        {
            CurrentResource.DataFolder = directory;
            InitializeFavorites();
            _catalogReady = true;
            BuildFavoritesMenu();
            BuildAddonVehiclesMenu();
        }
    }
}
