using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LemonUI.Menus;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            CheckBuffer();
            CheckDialog();
            CheckDialogFeedback();
            CheckNativeDecoder();
            Console.WriteLine("Unicode input checks passed (" + _checks + "; menu rendering/controller polling isolated, Win32 translation real)");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void CheckBuffer()
    {
        var value = new UnicodeTextBuffer(8);
        Assert(value.Append("БМВ") == TextAppendResult.Added && value.Text == "БМВ", "Cyrillic is preserved without encoding conversion");
        Assert(value.Append(" M3") == TextAppendResult.Added && value.Text == "БМВ M3", "mixed scripts and spaces are preserved");
        Assert(value.Append(" abc") == TextAppendResult.TooLong && value.Text == "БМВ M3", "overlong inserts do not truncate the current text");
        value.DeleteLast(); Assert(value.Text == "БМВ M", "delete removes one final text element");
        while (value.Text.Length > 0) value.DeleteLast();
        Assert(value.Append("a\u0301") == TextAppendResult.Added, "combining text can be entered");
        value.DeleteLast(); Assert(value.Text.Length == 0, "backspace removes the complete combining sequence");
        Assert(value.Append("\ud83d\ude97") == TextAppendResult.Added, "well-formed supplementary characters are accepted");
        value.DeleteLast(); Assert(value.Text.Length == 0, "backspace removes the complete surrogate pair");
        foreach (string bad in new[] { "~r~", "\n", "\t", "a\0b", "\ud800", "\udc00", "\u202e" })
            Assert(value.Append(bad) == TextAppendResult.InvalidText && value.Text.Length == 0, "control, markup and invalid UTF16 are rejected");
        var shortValue = new UnicodeTextBuffer(1);
        Assert(shortValue.Append("\ud83d\ude97") == TextAppendResult.TooLong && shortValue.Text.Length == 0, "maximum length cannot split a surrogate pair");
        Assert(value.Append(string.Empty) == TextAppendResult.Empty, "non-character keys do not report invalid text");
        value.DeleteLast(); Assert(value.Text.Length == 0, "backspace on an empty field is safe");
    }

    private static NativeItem Row(NativeMenu menu, string title) => menu.Items.Find(item => item.Title == title)
        ?? throw new InvalidOperationException("Missing input row " + title);

    private static void Insert(ResourceTextInput input, string text) => typeof(ResourceTextInput)
        .GetMethod("Append", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(input, new object[] { text });

    private static void CheckDialog()
    {
        var menu = new NativeMenu();
        var input = new ResourceTextInput(menu);
        int calls = 0;
        string? result = null;
        input.Begin(32, "Имя машины", text => { calls++; result = text; });
        Assert(input.Active && menu.Visible && menu.Name == "Имя машины" && menu.SelectedIndex == 0 && menu.Items.Count == 4,
            "keyboard entry opens directly on text with clear, save and cancel only");
        Assert(!menu.Items.Exists(row => row.Title is "Символ" or "Буква" or "Язык ввода"),
            "keyboard entry has no controller character selector or unclear symbol row");
        Assert(menu.Items[0].Description.Contains("Delete/Backspace") && !menu.Items[0].Description.Contains("A —"),
            "keyboard description names physical erasure without controller instructions");
        Insert(input, "a\u0301\ud83d\ude97");
        foreach (Keys key in new[] { Keys.Delete, Keys.Back })
        {
            var pressed = new KeyEventArgs(key);
            input.HandleKey(pressed);
            Assert(pressed.Handled && pressed.SuppressKeyPress && input.Active,
                "both physical delete variants are consumed without leaving the field");
        }
        Assert(menu.Items[0].AltTitle == "0/32", "Delete and Backspace each erase one complete Unicode text element");
        Insert(input, "не сохранять");
        menu.SelectedIndex = menu.Items.IndexOf(Row(menu, "Очистить"));
        input.HandleKey(new KeyEventArgs(Keys.Enter));
        Assert(input.Active && calls == 0 && menu.Items[0].AltTitle == "0/32", "Enter on Clear erases the field without submitting");
        Insert(input, "БМВ");
        input.UpdateInputMode(controller: true);
        Assert(menu.Items.Count == 8 && menu.Items[0].Title == "БМВ" && Row(menu, "Очистить") == menu.Items[menu.SelectedIndex],
            "switching to controller preserves text and the selected shared action");
        var alphabet = (NativeListItem<string>)Row(menu, "Язык ввода");
        var letter = (NativeListItem<string>)Row(menu, "Буква");
        var letterCase = (NativeListItem<string>)Row(menu, "Регистр");
        Row(menu, "Очистить").Activate();
        letter.Select(32); alphabet.Select(2);
        Assert(letter.SelectedIndex == 0 && letter.SelectedItem == "0" && !letterCase.Enabled && letter.Title == "Знак",
            "digits safely replace the final Cyrillic letter and explain that case does not apply");
        alphabet.Select(0); letter.Select(1); letter.Activate();
        letterCase.Select(1); letter.Activate();
        alphabet.Select(1); letter.Select(12); letter.Activate();
        alphabet.Select(2); letter.Select(3); letter.Activate();
        Assert(menu.Items[0].Title == "бАM3", "controller explicitly enters language, case and digits through one text buffer");
        input.GetControlHints(alphabet, out string? accept, out string? direction);
        Assert(accept == null && direction == "Язык", "language selection has a directional footer and no misleading Accept");
        input.GetControlHints(letter, out accept, out direction);
        Assert(accept == "Добавить" && direction == "Знак", "controller character footer reflects digits mode");
        menu.SelectedIndex = menu.Items.IndexOf(letter);
        input.UpdateInputMode(controller: false);
        Assert(menu.Items.Count == 4 && menu.SelectedIndex == 0 && menu.Items[0].Title == "бАM3",
            "switching back to keyboard removes controller rows and focuses entered text");
        input.GetControlHints(menu.Items[0], out accept, out direction);
        Assert(accept == "Сохранить" && direction == null, "keyboard text footer exposes its actual Enter action");
        input.UpdateInputMode(controller: true);
        Assert(menu.Items[menu.SelectedIndex] == letter && menu.Items[0].Title == "бАM3",
            "a controller taking over from direct typing focuses the letter action and preserves text");
        input.UpdateInputMode(controller: false);
        input.HandleKey(new KeyEventArgs(Keys.Enter));
        Assert(calls == 1 && result == "бАM3" && !input.Active && !menu.Visible,
            "submitting hides the dialog and completes exactly once");
        Row(menu, "Сохранить").Activate(); menu.Visible = false;
        Assert(calls == 1, "stale controls cannot submit twice");
        input.Begin(40, "Имя машины", text => { calls++; result = text; });
        menu.Visible = false;
        Assert(calls == 2 && result == null && !input.Active, "Back/hide cancels without accepted text");
        input.Begin(40, "Имя машины", text => { calls++; result = text; });
        Insert(input, "а");
        input.HandleKey(new KeyEventArgs(Keys.Control | Keys.A));
        Assert(menu.Items[0].Title == "а", "Ctrl+A does not unexpectedly erase entered text");
        input.Abandon();
        Assert(!input.Active && menu.Visible, "network-thread abandonment revokes callbacks without touching the UI");
        input.CloseSilently();
        Assert(calls == 2 && !menu.Visible, "later game-thread cleanup hides the dialog without callback");
        input.UpdateInputMode(controller: true);
        input.Begin(1, "Коротко", text => { calls++; result = text; });
        alphabet.Select(0); letterCase.Select(0); letter.Select(0); letter.Activate(); letter.Activate();
        Assert(menu.SelectedIndex == menu.Items.IndexOf(letter) && menu.Items[0].Title == "а", "controller opens on letter entry and obeys the field maximum");
        bool rejected = false;
        try { input.Begin(1, "Второе", _ => { }); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && input.Active, "a duplicate open cannot replace the active callback");
        input.CloseSilently();
        input.UpdateInputMode(controller: false);
        input.Begin(32, "Клавиатура", text => { calls++; result = text; });
        menu.AcceptsInput = false;
        input.HandleKey(new KeyEventArgs(Keys.Enter));
        Assert(input.Active && calls == 2, "held opening Enter cannot submit before the input activation guard releases");
        menu.AcceptsInput = true;
        Insert(input, "текст");
        input.HandleKey(new KeyEventArgs(Keys.Enter));
        Assert(!input.Active && calls == 3 && result == "текст", "keyboard Enter submits directly from the text row");
        input.Begin(32, "Отмена", text => { calls++; result = text; });
        menu.SelectedIndex = menu.Items.IndexOf(Row(menu, "Отмена"));
        input.HandleKey(new KeyEventArgs(Keys.Enter));
        Assert(!input.Active && calls == 4 && result == null, "Enter on the explicit Cancel row cancels without saving");
        input.Begin(32, "Отмена", text => { calls++; result = text; });
        input.HandleKey(new KeyEventArgs(Keys.Escape));
        Assert(!input.Active && calls == 5 && result == null, "keyboard Esc cancels independently of selection");
    }

    private static void CheckDialogFeedback()
    {
        var menu = new NativeMenu();
        var input = new ResourceTextInput(menu);
        input.UpdateInputMode(controller: true);
        NativeItem value = menu.Items[0];
        var alphabet = (NativeListItem<string>)Row(menu, "Язык ввода");
        var letter = (NativeListItem<string>)Row(menu, "Буква");
        int calls = 0;
        string? result = null;
        input.Begin(4, "Название", text => { calls++; result = text; });
        Assert(value.AltTitle == "0/4" && value.Description.Contains("A — добавить") && !value.Description.Contains("Enter"),
            "the controller value shows its field limit and only controller help");
        Insert(input, "a\u0301");
        Assert(value.Title == "a\u0301" && value.AltTitle == "2/4", "combining text uses the same UTF16 length contract as validation");
        Insert(input, "\ud83d\ude97");
        Assert(value.Title == "a\u0301\ud83d\ude97" && value.AltTitle == "4/4", "supplementary text reaches the limit without splitting");
        letter.Activate();
        Assert(value.Title == "a\u0301\ud83d\ude97" && value.AltTitle == "4/4" && letter.Description.Contains("Не хватает места."),
            "rejected controller input preserves the text and explains the full field");
        string feedback = letter.Description;
        int updates = letter.DescriptionUpdates;
        letter.Activate();
        Assert(letter.Description == feedback && letter.DescriptionUpdates == updates,
            "a repeated rejected character does not repeat the same UI feedback");
        foreach (NativeItem row in menu.Items)
            Assert(row.Description.Contains("Не хватает места."), "length feedback remains visible from every selected action");
        Insert(input, string.Empty);
        Assert(letter.Description == feedback && letter.DescriptionUpdates == updates,
            "a non-character key preserves refusal feedback without redrawing it");
        input.HandleKey(new KeyEventArgs(Keys.Delete));
        Assert(value.Title == "a\u0301" && value.AltTitle == "2/4" && !letter.Description.Contains("Не хватает"),
            "Delete erases a complete surrogate pair and clears the refusal");
        Insert(input, "~");
        Assert(value.Title == "a\u0301" && value.AltTitle == "2/4" && letter.Description.Contains("нельзя использовать"),
            "invalid translated input is explained without losing valid text");
        updates = letter.DescriptionUpdates;
        Insert(input, "\ud800");
        Assert(letter.DescriptionUpdates == updates && value.Title == "a\u0301", "another invalid character retains one inline explanation");
        Insert(input, "б");
        Assert(value.Title == "a\u0301б" && value.AltTitle == "3/4" && !letter.Description.Contains("нельзя"), "successful input clears the error");
        alphabet.Select(1); letter.Select(2); letter.Activate();
        Assert(value.Title == "a\u0301бc" && value.AltTitle == "4/4", "keyboard and controller edits share the exact remaining capacity");
        Row(menu, "Сохранить").Activate();
        Assert(calls == 1 && result == "a\u0301бc" && !input.Active, "explicit controller Save returns the exact Unicode text");
        input.Begin(2, "Новое имя", _ => calls++);
        Assert(value.Title == "Введите текст" && value.AltTitle == "0/2" && !letter.Description.Contains("нельзя") && menu.SelectedIndex == menu.Items.IndexOf(letter),
            "reopening resets text, feedback, limit and controller focus");
        input.CloseSilently();
        Insert(input, "x");
        Assert(calls == 1 && value.AltTitle == "0/2" && !menu.Visible, "late translated input cannot alter a closed field or invoke its callback");
    }

    private static void CheckNativeDecoder()
    {
        var layouts = new IntPtr[64];
        int count = GetKeyboardLayoutList(layouts.Length, layouts);
        Assert(count > 0, "the actual Windows/CrossOver keyboard layouts are available");
        for (int index = 0; index < count; index++)
        {
            var state = new byte[256];
            string lower = UnicodeKeyboardTextReader.Translate((uint)Keys.A, state, layouts[index]);
            state[(int)Keys.ShiftKey] = 0x80;
            string upper = UnicodeKeyboardTextReader.Translate((uint)Keys.A, state, layouts[index]);
            Assert(lower.Length > 0 && upper.Length > 0 && lower.ToUpperInvariant() == upper,
                "real ToUnicodeEx applies Shift to text from a loaded layout");
            state[(int)Keys.ShiftKey] = 0;
            state[(int)Keys.CapsLock] = 1;
            string caps = UnicodeKeyboardTextReader.Translate((uint)Keys.A, state, layouts[index]);
            Assert(caps == upper, "real ToUnicodeEx respects CapsLock");
            Console.WriteLine("Layout " + layouts[index].ToInt64().ToString("x") + ": A => U+" + ((int)lower[0]).ToString("X4"));
        }
        // GetKeyboardLayoutList can expose a Russian HKL that the bottle never
        // loaded. Activate the layout explicitly, as the composed input check
        // does, so ToUnicodeEx uses the real Russian table.
        IntPtr russianLayout = LoadKeyboardLayout("00000419", 0x00000001);
        Assert(russianLayout != IntPtr.Zero, "a real loaded Russian keyboard layout is available");
        var russianState = new byte[256];
        string russianP = UnicodeKeyboardTextReader.Translate((uint)Keys.P, russianState, russianLayout);
        Assert(russianP == "з", "real Russian ToUnicodeEx maps the GTA pause key VK_P to the ordinary letter з");
        var menu = new NativeMenu();
        var input = new ResourceTextInput(menu);
        int callbacks = 0;
        input.Begin(32, "Имя машины", _ => callbacks++);
        Insert(input, russianP);
        Assert(input.Active && menu.Visible && menu.Items[0].Title == "з" && callbacks == 0,
            "the reported Russian letter remains ordinary garage-name text without completing the field");
        input.CloseSilently();
        var control = new KeyEventArgs(Keys.Control | Keys.V);
        Assert(UnicodeKeyboardTextReader.Read(control).Length == 0, "ordinary Ctrl shortcuts do not insert translated characters");
        string actual = UnicodeKeyboardTextReader.Read(new KeyEventArgs(Keys.A));
        IntPtr window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint foregroundProcess);
        if (window == IntPtr.Zero || foregroundProcess != GetCurrentProcessId())
            Assert(actual.Length == 0, "the reader ignores late input while another program or no Windows window has focus");
        else
            Assert(actual.Length > 0, "the real reader resolves the foreground window layout and keyboard state");
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
    [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
        _checks++;
    }
}
