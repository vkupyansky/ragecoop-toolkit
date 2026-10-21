namespace CoopControl.Shared;

internal static class EngineTransportGuard
{
    private static bool _installed;
    internal static void Install()
    {
        if (_installed) { return; }
        EngineCodecGuard.Install();
        try { EngineFragmentGuard.Install(); }
        catch { EngineCodecGuard.Uninstall(); throw; }
        _installed = true;
    }

    internal static void Uninstall()
    {
        try { EngineFragmentGuard.Uninstall(); }
        finally { EngineCodecGuard.Uninstall(); _installed = false; }
    }
}
