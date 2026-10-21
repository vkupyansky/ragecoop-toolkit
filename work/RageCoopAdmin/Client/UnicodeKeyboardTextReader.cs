using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RageCoopAdmin.Client;

// RageCoop exposes virtual keys, not character events. Use the active foreground
// window's layout rather than the SHVDN worker thread's usually English layout.
internal static class UnicodeKeyboardTextReader
{
    internal static string Read(KeyEventArgs key)
    {
        if ((int)key.KeyCode < 0 || (int)key.KeyCode > 255 || (key.Control && !key.Alt)) return string.Empty;
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return string.Empty;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0 || process != GetCurrentProcessId()) return string.Empty;
        IntPtr layout = GetKeyboardLayout(thread);
        if (layout == IntPtr.Zero) return string.Empty;
        var state = new byte[256];
        if (!GetKeyboardState(state)) return string.Empty;
        state[(int)key.KeyCode] = 0x80;
        state[(int)Keys.ShiftKey] = key.Shift ? (byte)0x80 : (byte)0;
        state[(int)Keys.ControlKey] = key.Control ? (byte)0x80 : (byte)0;
        state[(int)Keys.Menu] = key.Alt ? (byte)0x80 : (byte)0;
        state[(int)Keys.CapsLock] = (byte)(GetKeyState((int)Keys.CapsLock) & 1);
        return Translate((uint)key.KeyCode, state, layout);
    }

    internal static string Translate(uint virtualKey, byte[] state, IntPtr layout)
    {
        if (virtualKey > 255 || state == null || state.Length != 256 || layout == IntPtr.Zero)
            throw new ArgumentException("A virtual key, 256-byte keyboard state and active layout are required.");
        var output = new StringBuilder(16);
        uint scan = MapVirtualKeyEx(virtualKey, 0, layout);
        // Flag 4 leaves the OS dead-key buffer untouched. Ignore individual
        // dead keys: this reader does not implement composed-key or IME input.
        int length = ToUnicodeEx(virtualKey, scan, state, output, output.Capacity, 4, layout);
        return length > 0 && length <= output.Length ? output.ToString(0, length) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [Out] StringBuilder output,
        int capacity, uint flags, IntPtr layout);
}
