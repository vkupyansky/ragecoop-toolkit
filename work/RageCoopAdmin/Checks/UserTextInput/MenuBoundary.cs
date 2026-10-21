using System;
using System.Collections.Generic;
using System.Linq;

// Isolate rendering and physical controller polling, not text-input behavior.
namespace LemonUI.Menus;

internal class NativeMenu
{
    private bool _visible;
    internal string Name { get; set; } = string.Empty;
    internal List<NativeItem> Items { get; } = new List<NativeItem>();
    internal int SelectedIndex { get; set; }
    internal bool AcceptsInput { get; set; } = true;
    internal bool Visible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; if (!value) Closed?.Invoke(this, EventArgs.Empty); }
    }
    internal event EventHandler? Closed;
    internal void Add(NativeItem item) => Items.Add(item);
    internal void Add(int position, NativeItem item) => Items.Insert(position, item);
    internal void Remove(NativeItem item)
    {
        Items.Remove(item);
        if (SelectedIndex >= Items.Count) SelectedIndex = Items.Count - 1;
    }
}

internal class NativeItem
{
    internal string Title { get; set; }
    internal string AltTitle { get; set; } = string.Empty;
    private string _description = string.Empty;
    internal string Description { get => _description; set { _description = value; DescriptionUpdates++; } }
    internal int DescriptionUpdates { get; private set; }
    internal bool Enabled { get; set; } = true;
    internal event EventHandler? Activated;
    internal NativeItem(string title, string description) { Title = title; Description = description; }
    internal void Activate() { if (Enabled) Activated?.Invoke(this, EventArgs.Empty); }
}

internal sealed class NativeListItem<T> : NativeItem
{
    private List<T> _items;
    private int _selectedIndex;
    internal List<T> Items { get => _items; set { _items = value; _ = SelectedItem; } }
    internal int SelectedIndex { get => _selectedIndex; set => _selectedIndex = value; }
    internal T SelectedItem => Items[_selectedIndex];
    internal event EventHandler<ItemChangedEventArgs>? ItemChanged;
    internal NativeListItem(string title, string description, params T[] values) : base(title, description) => _items = values.ToList();
    internal void Select(int index) { _selectedIndex = index; ItemChanged?.Invoke(this, new ItemChangedEventArgs(index)); }
}

internal sealed class ItemChangedEventArgs : EventArgs
{
    internal int Index { get; }
    internal ItemChangedEventArgs(int index) => Index = index;
}
