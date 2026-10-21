using System;
using System.Collections.Generic;
using RageCoop.Client;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

internal static class PlayerSnapshots
{
    internal static bool TryGet(out Dictionary<int, Player> players)
        => TryGet(() => API.Players, out players);

    internal static bool TryGet(Func<Dictionary<int, Player>> copyRoster, out Dictionary<int, Player> players)
    {
        try
        {
            // RageCoop copies its dictionary while the listener can change the roster.
            players = copyRoster();
            foreach (KeyValuePair<int, Player> entry in players)
            {
                if (entry.Key == 0 || entry.Value == null || entry.Value.ID != entry.Key)
                {
                    players = new Dictionary<int, Player>();
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException ||
                                   (ex is ArgumentException && ex is not ArgumentNullException))
        {
            // Keep the last UI snapshot; the next scheduled refresh will retry.
            players = new Dictionary<int, Player>();
            return false;
        }
    }
}
