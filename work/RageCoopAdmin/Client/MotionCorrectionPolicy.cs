using GTA.Math;

namespace RageCoopAdmin.Client;

internal static class MotionCorrectionPolicy
{
    // The original correction points at the predicted position. Preserve it
    // while its relative displacement fits inside this frame's error; otherwise
    // cap only that correction to distance / dt. Owner velocity remains separate.
    internal static Vector3 Limit(Vector3 correction, float distance, float frameSeconds)
    {
        if (frameSeconds <= 0f || float.IsNaN(frameSeconds) || float.IsInfinity(frameSeconds)) { return Vector3.Zero; }
        float speed = correction.Length();
        float maximumSpeed = distance / frameSeconds;
        return speed > maximumSpeed ? correction * (maximumSpeed / speed) : correction;
    }
}
