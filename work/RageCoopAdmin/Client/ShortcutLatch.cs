using System.Collections.Generic;
using System.Windows.Forms;

namespace RageCoopAdmin.Client;

// A held key must trigger an action once, even when Windows sends repeated KeyDown events.
internal sealed class ShortcutLatch
{
    private readonly HashSet<int> _pressed = new HashSet<int>();

    internal bool TryPress(int keyCode) => _pressed.Add(keyCode);
    internal void Release(int keyCode) => _pressed.Remove(keyCode);
    internal void Clear() => _pressed.Clear();
}

internal enum InterfaceShortcut
{
    None,
    Menu,
    Repair,
    Boost
}

internal static class InterfaceShortcutMatcher
{
    internal static InterfaceShortcut Match(Keys key, Keys modifiers, InterfaceSettings settings)
    {
        if (modifiers != Keys.None)
        {
            return InterfaceShortcut.None;
        }

        if (key == Keys.J)
        {
            return InterfaceShortcut.Menu;
        }

        if (settings.RepairShortcut == 0 && key == Keys.K)
        {
            return InterfaceShortcut.Repair;
        }

        if (settings.KeyboardBoost && key == Keys.O)
        {
            return InterfaceShortcut.Boost;
        }

        return InterfaceShortcut.None;
    }
}

// Accept simultaneous bumpers as well as either order. A direction completes the chord.
internal sealed class BumperShortcutChord
{
    private bool _active;
    private bool _requireRelease;

    internal bool IsActive => _active;

    internal void CancelUntilRelease()
    {
        _active = false;
        _requireRelease = true;
    }

    internal bool Update(bool left, bool right)
    {
        if (_requireRelease)
        {
            if (!left && !right)
            {
                _requireRelease = false;
            }

            return false;
        }

        if (!left && !right)
        {
            _active = false;
            return false;
        }

        _active = left && right;
        return _active;
    }
}

// Keep consumed controls suppressed through their release frame.
internal sealed class MenuInputReleaseGuard
{
    private bool _captured;
    private bool _releaseFrame;

    internal bool IsCaptured => _captured;

    internal bool Update(bool ownerActive, bool controlHeld)
    {
        if (ownerActive)
        {
            _captured = true;
            _releaseFrame = false;
            return true;
        }

        if (!_captured) return false;
        if (controlHeld)
        {
            _releaseFrame = false;
            return true;
        }

        if (!_releaseFrame)
        {
            _releaseFrame = true;
            return true;
        }

        _captured = false;
        _releaseFrame = false;
        return false;
    }
}
