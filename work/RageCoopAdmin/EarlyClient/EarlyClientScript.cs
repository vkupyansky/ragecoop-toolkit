using System;
using GTA;
using RageCoopAdmin.Client;

namespace RageCoop.EarlyClient;

// SHVDN orders the discovered types by assembly name before constructing them.
// CoopControl.EarlyClient precedes RageCoop.Client, including Main's settings read.
[ScriptAttributes(NoScriptThread = true)]
public sealed class EarlyClientScript : Script
{
    public EarlyClientScript()
    {
        try
        {
            LoggerFileGuard.Install();
            ClientSettingsGuard.Install();
            ResourceAssemblyLoadGuard.Install();
            ServerBrowserTlsGuard.Install();
            EngineIngressGuard.Install("friendslegacy.ragecoop.earlyingress");
        }
        catch
        {
            EngineIngressGuard.Uninstall();
            ServerBrowserTlsGuard.Uninstall();
            ResourceAssemblyLoadGuard.Uninstall();
            ClientSettingsGuard.Uninstall();
            LoggerFileGuard.Uninstall();
            throw;
        }
        Aborted += OnAborted;
    }

    private static void OnAborted(object? sender, EventArgs args)
    {
        try { EngineIngressGuard.Uninstall(); }
        finally
        {
            try { ServerBrowserTlsGuard.Uninstall(); }
            finally
            {
                try { ResourceAssemblyLoadGuard.Uninstall(); }
                finally
                {
                    try { ClientSettingsGuard.Uninstall(); }
                    finally { LoggerFileGuard.Uninstall(); }
                }
            }
        }
    }
}
