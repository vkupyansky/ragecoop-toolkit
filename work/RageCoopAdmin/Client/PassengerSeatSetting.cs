using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LemonUI.Menus;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// Operate on the original setting instance on the game thread. Other menu
// items and the user's saved RageCoop settings remain outside this lifecycle.
internal static class PassengerSeatSetting
{
    private static NativeMenu? _menu;
    private static NativeItem? _item;
    private static NativeItem? _next;
    private static int _index;

    internal static bool HasSupportedContract()
    {
        Type? type = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Menus.SettingsMenu", false);
        FieldInfo? menu = type == null ? null : AccessTools.Field(type, "Menu");
        FieldInfo? item = type == null ? null : AccessTools.Field(type, "_passengerKey");
        ConstructorInfo? initializer = type?.TypeInitializer;
        if (menu?.FieldType != typeof(NativeMenu) || !menu.IsStatic || item?.FieldType != typeof(NativeItem) ||
            !item.IsStatic || !item.IsInitOnly || initializer == null) { return false; }
        MethodInfo add = AccessTools.DeclaredMethod(typeof(NativeMenu), nameof(NativeMenu.Add), new[] { typeof(NativeItem) });
        List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(initializer);
        if (instructions.Count < 3) { return false; }
        return Enumerable.Range(1, instructions.Count - 2).Count(index => instructions[index - 1].LoadsField(menu) &&
            instructions[index].LoadsField(item) && instructions[index + 1].Calls(add)) == 1;
    }

    internal static bool Hide()
    {
        if (_menu != null) { return true; }
        if (!HasSupportedContract()) { return false; }
        Type type = typeof(SyncedPed).Assembly.GetType("RageCoop.Client.Menus.SettingsMenu", true)!;
        if (AccessTools.Field(type, "Menu").GetValue(null) is not NativeMenu menu ||
            AccessTools.Field(type, "_passengerKey").GetValue(null) is not NativeItem item) { return false; }
        return Hide(menu, item);
    }

    internal static bool Hide(NativeMenu menu, NativeItem item)
    {
        if (_menu != null) { return true; }
        if (menu.Items.Count(candidate => ReferenceEquals(candidate, item)) != 1) { return false; }
        _menu = menu;
        _item = item;
        _index = menu.Items.IndexOf(item);
        _next = _index + 1 < menu.Items.Count ? menu.Items[_index + 1] : null;
        NativeItem? selected = menu.SelectedItem;
        menu.Remove(item);
        if (menu.Items.Count > 0)
        {
            menu.SelectedItem = selected != null && menu.Items.Contains(selected) ? selected : menu.Items[Math.Min(_index, menu.Items.Count - 1)];
        }
        return true;
    }

    internal static void Restore()
    {
        if (_menu == null || _item == null) { return; }
        if (!_menu.Items.Contains(_item))
        {
            NativeItem? selected = _menu.SelectedItem;
            int nextIndex = _next == null ? -1 : _menu.Items.IndexOf(_next);
            _menu.Add(nextIndex >= 0 ? nextIndex : Math.Min(_index, _menu.Items.Count), _item);
            if (selected != null && _menu.Items.Contains(selected)) { _menu.SelectedItem = selected; }
        }
        _menu = null;
        _item = null;
        _next = null;
    }
}
