using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LemonUI.Menus;

namespace RageCoopAdmin.Client;

internal sealed class ResourceTextInput
{
    private const string KeyboardHelp = "Печатайте на клавиатуре. Enter — сохранить, Delete/Backspace — стереть, Esc — отмена.";
    private const string ControllerHelp = "Выберите язык и букву. A — добавить, B — отмена.";
    private const string AlphabetHelp = "←/→ — русский, английский или цифры и знаки.";
    private const string CaseHelp = "←/→ — строчные или заглавные буквы.";
    private const string LetterHelp = "←/→ — выбрать букву, A — добавить.";
    private const string EraseHelp = "Стереть последнюю букву или знак.";
    private const string ClearHelp = "Стереть весь введённый текст.";
    private static readonly string[][] Alphabets =
    {
        "абвгдеёжзийклмнопрстуфхцчшщъыьэюя".Select(value => value.ToString()).ToArray(),
        "abcdefghijklmnopqrstuvwxyz".Select(value => value.ToString()).ToArray(),
        new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "Пробел", "-", "_", ".", "'" }
    };
    private readonly NativeMenu _menu;
    private readonly NativeItem _value;
    private readonly NativeListItem<string> _alphabet;
    private readonly NativeListItem<string> _case;
    private readonly NativeListItem<string> _letter;
    private readonly NativeItem _erase;
    private readonly NativeItem _clear;
    private readonly NativeItem _done;
    private readonly NativeItem _cancel;
    private string _feedback = string.Empty;
    private UnicodeTextBuffer? _buffer;
    private Action<string?>? _completed;
    private bool _controller;
    private volatile bool _active;

    internal bool Active => _active;
    internal bool Visible => _menu.Visible;
    internal NativeMenu Menu => _menu;

    internal ResourceTextInput(NativeMenu menu)
    {
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _value = new NativeItem("Введите текст", KeyboardHelp);
        _value.Activated += (_, _) => { if (!_controller) Finish(submit: true); };
        _alphabet = new NativeListItem<string>("Язык ввода", AlphabetHelp, "Русский", "Английский", "Цифры и знаки");
        _case = new NativeListItem<string>("Регистр", CaseHelp, "Строчные", "Заглавные");
        _letter = new NativeListItem<string>("Буква", LetterHelp, Alphabets[0]);
        _alphabet.ItemChanged += (_, _) => RefreshLetters();
        _case.ItemChanged += (_, _) => RefreshLetters();
        _letter.Activated += (_, _) => Append(_letter.SelectedItem == "Пробел" ? " " : _letter.SelectedItem);
        _erase = new NativeItem("Стереть букву", EraseHelp);
        _erase.Activated += (_, _) => DeleteLast();
        _clear = new NativeItem("Очистить", ClearHelp);
        _clear.Activated += (_, _) => Clear();
        _done = new NativeItem("Сохранить", "Сохранить введённый текст.");
        _done.Activated += (_, _) => Finish(submit: true);
        _cancel = new NativeItem("Отмена", "Вернуться без сохранения текста.");
        _cancel.Activated += (_, _) => Finish(submit: false);
        menu.Add(_value);
        menu.Add(_clear);
        menu.Add(_done);
        menu.Add(_cancel);
        menu.Closed += (_, _) => Finish(submit: false);
    }

    internal void UpdateInputMode(bool controller)
    {
        if (_controller == controller) return;
        NativeItem? selected = _menu.Items.Count == 0 ? null : _menu.Items[_menu.SelectedIndex];
        _controller = controller;
        // Use LemonUI's operations so its visible-item cache and selection
        // remain valid. Never replace the menu/pool during an activation.
        if (controller)
        {
            _menu.Add(1, _alphabet);
            _menu.Add(2, _case);
            _menu.Add(3, _letter);
            _menu.Add(4, _erase);
        }
        else
        {
            _menu.Remove(_erase);
            _menu.Remove(_letter);
            _menu.Remove(_case);
            _menu.Remove(_alphabet);
        }
        _value.Enabled = !controller;
        int selectedIndex = selected == null ? -1 : _menu.Items.IndexOf(selected);
        _menu.SelectedIndex = selectedIndex >= 0 && selected?.Enabled == true
            ? selectedIndex : _menu.Items.IndexOf(controller ? _letter : _value);
        RefreshValue();
    }

    internal void GetControlHints(NativeItem? item, out string? accept, out string? direction)
    {
        accept = null;
        direction = null;
        if (ReferenceEquals(item, _alphabet)) direction = "Язык";
        else if (ReferenceEquals(item, _case)) direction = "Регистр";
        else if (ReferenceEquals(item, _letter))
        {
            accept = "Добавить";
            direction = _alphabet.SelectedIndex == 2 ? "Знак" : "Буква";
        }
        else if (ReferenceEquals(item, _value)) { if (!_controller) accept = "Сохранить"; }
        else if (item?.Enabled == true) accept = item.Title;
    }

    internal void Begin(int maximumLength, string title, Action<string?> completed)
    {
        if (Active) throw new InvalidOperationException("A text input is already active.");
        _buffer = new UnicodeTextBuffer(maximumLength);
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _menu.Name = title;
        _active = true;
        _feedback = string.Empty;
        RefreshValue();
        _menu.Visible = true;
        _menu.SelectedIndex = _menu.Items.IndexOf(_controller ? _letter : _value);
    }

    internal void HandleKey(KeyEventArgs key)
    {
        if (!Active) return;
        if (key.KeyCode == Keys.Back || key.KeyCode == Keys.Delete) DeleteLast();
        else if (key.KeyCode == Keys.Enter && _menu.AcceptsInput)
        {
            NativeItem selected = _menu.Items[_menu.SelectedIndex];
            if (ReferenceEquals(selected, _clear)) Clear();
            else if (ReferenceEquals(selected, _cancel)) Finish(submit: false);
            else Finish(submit: true);
        }
        else if (key.KeyCode == Keys.Escape && _menu.AcceptsInput) Finish(submit: false);
        else if (key.KeyCode != Keys.Escape && key.KeyCode != Keys.Enter)
            Append(UnicodeKeyboardTextReader.Read(key));
        key.Handled = true;
        key.SuppressKeyPress = true;
    }

    // Stop may run on the network thread. Revoke callbacks without touching
    // LemonUI/native functions; the normal game-thread Cleanup hides the view.
    internal void Abandon()
    {
        _active = false;
        Interlocked.Exchange(ref _completed, null);
    }

    internal void CloseSilently()
    {
        Abandon();
        _menu.Visible = false;
    }

    private void RefreshLetters()
    {
        // The installed LemonUI reads SelectedItem while replacing Items
        // and does not clamp an index left over from a longer alphabet.
        _letter.SelectedIndex = 0;
        string[] letters = Alphabets[_alphabet.SelectedIndex];
        _letter.Items = (_alphabet.SelectedIndex < 2 && _case.SelectedIndex == 1
            ? letters.Select(value => value.ToUpperInvariant()) : letters).ToList();
        _case.Enabled = _alphabet.SelectedIndex < 2;
        _letter.Title = _alphabet.SelectedIndex == 2 ? "Знак" : "Буква";
        RefreshValue();
    }

    private void Append(string text)
    {
        if (!Active || _buffer == null) return;
        TextAppendResult result = _buffer.Append(text);
        if (result == TextAppendResult.Empty) return;
        string feedback = result == TextAppendResult.TooLong
            ? "Не хватает места. Сотрите часть текста."
            : result == TextAppendResult.InvalidText
                ? "Этот знак нельзя использовать."
                : string.Empty;
        if (result != TextAppendResult.Added && feedback == _feedback) return;
        _feedback = feedback;
        RefreshValue();
    }

    private void DeleteLast()
    {
        if (!Active) return;
        _buffer?.DeleteLast();
        _feedback = string.Empty;
        RefreshValue();
    }

    private void Clear()
    {
        if (!Active || _buffer == null) return;
        _buffer = new UnicodeTextBuffer(_buffer.MaximumLength);
        _feedback = string.Empty;
        RefreshValue();
    }

    private void RefreshValue()
    {
        if (_buffer == null) return;
        _value.Title = string.IsNullOrEmpty(_buffer.Text) ? "Введите текст" : _buffer.Text;
        _value.AltTitle = _buffer.Text.Length + "/" + _buffer.MaximumLength;
        string status = string.IsNullOrEmpty(_feedback) ? string.Empty : _feedback + " ";
        string modeHelp = _controller ? ControllerHelp : KeyboardHelp;
        _value.Description = status + modeHelp;
        _alphabet.Description = status + AlphabetHelp;
        _case.Description = status + (_case.Enabled ? CaseHelp : "Для цифр и знаков регистр не нужен.");
        _letter.Description = status + (_alphabet.SelectedIndex == 2 ? "←/→ — выбрать знак, A — добавить." : LetterHelp);
        _erase.Description = status + EraseHelp;
        _clear.Description = status + ClearHelp;
        _done.Description = status + "Сохранить введённый текст.";
        _cancel.Description = status + "Вернуться без сохранения текста.";
    }

    private void Finish(bool submit)
    {
        if (!Active) return;
        _active = false;
        Action<string?>? completed = Interlocked.Exchange(ref _completed, null);
        string? result = submit ? _buffer?.Text.Trim() : null;
        _menu.Visible = false;
        completed?.Invoke(result);
    }
}
