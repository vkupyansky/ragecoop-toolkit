using System;

namespace RageCoopAdmin.Client;

internal static class KeyboardVehicleBoost
{
    private const float MinimumForwardSpeed = 3f;
    private const double AccelerationMetresPerSecondSquared = 60d;

    internal static bool TryGetForwardSpeed(bool held, float currentSpeed, float elapsedSeconds, out float forwardSpeed)
    {
        forwardSpeed = 0f;
        if (!held || float.IsNaN(currentSpeed) || float.IsInfinity(currentSpeed) || currentSpeed < 0f ||
            float.IsNaN(elapsedSeconds) || float.IsInfinity(elapsedSeconds) || elapsedSeconds <= 0f)
        {
            return false;
        }

        // Keep Native Trainer's launch floor, then add a fixed acceleration.
        // Multiplying speed each frame compounds into extreme commands within
        // one second. A stalled frame must not apply a catch-up impulse.
        double target = Math.Max(currentSpeed, MinimumForwardSpeed) +
            AccelerationMetresPerSecondSquared * Math.Min(elapsedSeconds, 0.1f);
        if (target > float.MaxValue)
        {
            return false;
        }

        forwardSpeed = (float)target;
        return true;
    }
}
