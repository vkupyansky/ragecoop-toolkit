using System;
using System.Diagnostics;

namespace RageCoopAdmin.Client;

// Coalesce list navigation without sending intermediate hours or echoing server updates.
internal sealed class WorldTimeSelection
{
    private const double SelectionDelaySeconds = 0.65;
    private const double ConfirmationTimeoutSeconds = 5;
    private bool _pending;
    private bool _immediate;
    private bool _sent;
    private bool _awaitingConfirmation;
    private long _lastEditTick;
    private long _lastSendTick;
    private int _sentHour;
    private int _sentMinute;

    internal int Hour { get; private set; }
    internal int Minute { get; private set; }

    internal void Refresh(int hour, int minute)
    {
        Validate(hour, minute);
        if (_pending || _awaitingConfirmation) return;
        Hour = hour;
        Minute = minute;
    }

    internal void SelectHour(int hour, long now)
    {
        Validate(hour, Minute);
        if (Hour == hour) return;
        Hour = hour;
        Schedule(now, immediate: false);
    }

    internal void SelectMinute(int minute, long now)
    {
        Validate(Hour, minute);
        if (Minute == minute) return;
        Minute = minute;
        Schedule(now, immediate: false);
    }

    internal void SelectPreset(int hour, int minute, long now)
    {
        Validate(hour, minute);
        Hour = hour;
        Minute = minute;
        Schedule(now, immediate: true);
    }

    internal bool TryTakeRequest(long now, out int hour, out int minute)
    {
        hour = Hour;
        minute = Minute;
        if (!_pending ||
            (!_immediate && Elapsed(now, _lastEditTick) < SelectionDelaySeconds) ||
            (_sent && Elapsed(now, _lastSendTick) < SelectionDelaySeconds)) return false;

        _pending = false;
        _immediate = false;
        _sent = true;
        _awaitingConfirmation = true;
        _lastSendTick = now;
        _sentHour = hour;
        _sentMinute = minute;
        return true;
    }

    internal void Confirm(int hour, int minute)
    {
        Validate(hour, minute);
        if (_awaitingConfirmation && hour == _sentHour && minute == _sentMinute)
            _awaitingConfirmation = false;
        Refresh(hour, minute);
    }

    internal bool TryExpire(long now)
    {
        if (!_awaitingConfirmation || Elapsed(now, _lastSendTick) < ConfirmationTimeoutSeconds)
            return false;
        _awaitingConfirmation = false;
        return true;
    }

    internal void Cancel()
    {
        _pending = false;
        _immediate = false;
        _sent = false;
        _awaitingConfirmation = false;
    }

    private void Schedule(long now, bool immediate)
    {
        _pending = true;
        _immediate = immediate;
        _lastEditTick = now;
    }

    private static double Elapsed(long now, long previous) =>
        (now - previous) / (double)Stopwatch.Frequency;

    private static void Validate(int hour, int minute)
    {
        if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour));
        if (minute < 0 || minute > 59) throw new ArgumentOutOfRangeException(nameof(minute));
    }
}
