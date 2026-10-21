using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace RageCoopAdmin.Client;

public sealed partial class AdminMenuScript
{
    private long _boostDiagnosticLastLog;
    private HashSet<string>? _boostDiagnosticReasons;

    // Only held O reaches this trace. Aggregate brief eligibility changes so a
    // wheel-contact flicker remains visible without emitting a line every frame.
    // This diagnostic performs no game or network mutation.
    private void TraceKeyboardBoost(long now, string reason, int vehicle = 0, int model = 0,
        float? speed = null, float? target = null)
    {
        var logger = CurrentResource?.Logger;
        if (logger == null || logger.LogLevel > 1) return;
        (_boostDiagnosticReasons ??= new HashSet<string>()).Add(reason);
        if (_boostDiagnosticLastLog != 0 && (now <= _boostDiagnosticLastLog ||
            (now - _boostDiagnosticLastLog) / (double)Stopwatch.Frequency < 1d)) return;
        _boostDiagnosticLastLog = now;
        var synced = vehicle == 0 ? null : RuntimeEntityLookup.GetVehicleByHandle(vehicle);
        string ownership = synced == null ? "missing" : synced.IsLocal ? "local" : "remote";
        string Number(float? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
        logger.Debug("Coop Control: held O state=" + reason +
            "; observed=" + string.Join(",", _boostDiagnosticReasons.OrderBy(value => value)) +
            "; vehicle=" + vehicle.ToString(CultureInfo.InvariantCulture) +
            "; model=" + model.ToString("X8", CultureInfo.InvariantCulture) +
            "; ownership=" + ownership + "; speed_mps=" + Number(speed) + "; target_mps=" + Number(target));
        _boostDiagnosticReasons.Clear();
    }
}
