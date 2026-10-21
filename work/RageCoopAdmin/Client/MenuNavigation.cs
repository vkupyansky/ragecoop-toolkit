using GTA;
using GTA.Native;
using LemonUI.Menus;
using LemonUI.Scaleform;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private static readonly InputArgument[] MenuHintInputGroup = { 2 };
    private NativeMenu? _hintMenu;
    private NativeItem? _hintItem;
    private int _hintActionIndex;
    private bool _hintController;
    private bool _hintEnabled;
    private string? _hintAccept;
    private string? _hintDirection;

    private void RefreshMenuControlHints()
    {
        if (_pool == null || !_pool.AreAnyVisible)
        {
            _hintMenu = null;
            _hintItem = null;
            return;
        }

        // The normal path keeps its visible menu, avoiding a boxed pool
        // enumerator and rebuilding scaleform slots on every game frame.
        NativeMenu? menu = _hintMenu;
        if (menu?.Visible != true)
        {
            menu = null;
            foreach (var processable in _pool)
            {
                if (processable is NativeMenu candidate && candidate.Visible)
                {
                    menu = candidate;
                    break;
                }
            }
        }
        if (menu == null) return;

        NativeItem? item = menu.SelectedItem;
        bool enabled = item?.Enabled == true;
        bool controller = IsMenuUsingController();
        int actionIndex = item is NativeListItem<string> action ? action.SelectedIndex : -1;
        string? accept = null;
        string? direction = null;
        if (enabled)
        {
            if (item is NativeSubmenuItem) accept = "Открыть";
            else if (item is NativeCheckboxItem) accept = "Вкл./выкл.";
            else if (ReferenceEquals(item, _wantedItem)) direction = "Звёзды";
            else if (ReferenceEquals(item, _hourItem) || ReferenceEquals(item, _minuteItem)) direction = "Изменить";
            else if (ReferenceEquals(menu, _textInput?.Menu))
            {
                _textInput.GetControlHints(item, out accept, out direction);
            }
            else if (item is NativeListItem<string> choices)
            {
                if (choices.Items.Count > 0)
                {
                    accept = choices.SelectedItem;
                    direction = "Действие";
                }
            }
            else if (item is NativeListItem) direction = "Изменить";
            else accept = "Выбрать";
        }

        if (ReferenceEquals(menu, _hintMenu) && ReferenceEquals(item, _hintItem) &&
            actionIndex == _hintActionIndex && controller == _hintController && enabled == _hintEnabled &&
            accept == _hintAccept && direction == _hintDirection) return;

        menu.Buttons.Clear();
        if (accept != null) menu.Buttons.Add(CreateMenuControlHint(accept, Control.FrontendAccept));
        if (direction != null)
        {
            // Each glyph comes from the SDK's native control lookup. Keeping
            // both controls as slots avoids hardcoded keyboard/Xbox artwork.
            menu.Buttons.Add(CreateMenuControlHint(string.Empty, Control.FrontendLeft));
            menu.Buttons.Add(CreateMenuControlHint(direction, Control.FrontendRight));
        }
        menu.Buttons.Add(CreateMenuControlHint(ReferenceEquals(menu, _textInput?.Menu) ? "Отмена" : "Назад", Control.FrontendCancel));
        _hintMenu = menu;
        _hintItem = item;
        _hintActionIndex = actionIndex;
        _hintController = controller;
        _hintEnabled = enabled;
        _hintAccept = accept;
        _hintDirection = direction;
    }

    private static bool IsMenuUsingController() =>
        !Function.Call<bool>(Hash.IS_USING_KEYBOARD_AND_MOUSE, MenuHintInputGroup);

    private static InstructionalButton CreateMenuControlHint(string description, Control control) =>
        new InstructionalButton(description, control);
}
