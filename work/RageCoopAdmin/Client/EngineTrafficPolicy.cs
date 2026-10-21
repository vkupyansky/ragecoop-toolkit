namespace RageCoopAdmin.Client;

internal static class EngineTrafficPolicy
{
    // Keep enough road ahead to cover fast driving while GTA streams new traffic.
    internal const float ProtectedDistance = 150f;

    internal static bool CanCull(bool overLimit, bool onScreen, bool persistent, bool playerOccupant, float playerDistanceSquared) =>
        overLimit && !onScreen && !persistent && !playerOccupant &&
        !float.IsNaN(playerDistanceSquared) && !float.IsInfinity(playerDistanceSquared) &&
        playerDistanceSquared > ProtectedDistance * ProtectedDistance;
}
