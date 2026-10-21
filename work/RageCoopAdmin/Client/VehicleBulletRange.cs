using System;
using GTA.Math;

namespace RageCoopAdmin.Client;

internal static class VehicleBulletRange
{
    internal const float StandardRangeSquared = 50f * 50f;

    internal static bool NeedsVehicleCheck(float distanceSquared) =>
        !float.IsNaN(distanceSquared) && !float.IsInfinity(distanceSquared) &&
        distanceSquared > StandardRangeSquared;

    internal static bool IsVehicleHit(Vector3 localHit, Vector3 minimum, Vector3 maximum)
    {
        if (!IsFinite(localHit) || !IsFinite(minimum) || !IsFinite(maximum) ||
            minimum.X >= maximum.X || minimum.Y >= maximum.Y || minimum.Z >= maximum.Z)
        {
            return false;
        }

        // GTA reports impact points on the surface; allow small interpolation error.
        const float margin = 1.5f;
        return localHit.X >= minimum.X - margin && localHit.X <= maximum.X + margin &&
            localHit.Y >= minimum.Y - margin && localHit.Y <= maximum.Y + margin &&
            localHit.Z >= minimum.Z - margin && localHit.Z <= maximum.Z + margin;
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
        !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
        !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
}
