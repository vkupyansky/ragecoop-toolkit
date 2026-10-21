using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Core;

namespace RageCoopAdmin.Client;

// Apply each received snapshot as one operation against RageCoop's own pools.
// The vehicle phase also inspects peds, so vehicles precede peds. Keep the
// host's existing per-phase locks: its ped update can yield while loading a model.
internal static class EngineNetworkGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginenetwork";
    internal const long ExpiryMilliseconds = 10000;
    private static Harmony? _harmony;
    private static readonly List<MethodBase> Patched = new List<MethodBase>();
    private static object[] _locks = Array.Empty<object>();
    private static FieldInfo? _players;
    private static FieldInfo? _peds;
    private static MethodInfo? _characterSetter;
    private static long _skippedSnapshots;

    internal static long SkippedSnapshots => Interlocked.Read(ref _skippedSnapshots);

    internal static void Install()
    {
        if (_harmony != null) { return; }
        Assembly assembly = typeof(SyncedEntity).Assembly;
        Type pool = RequireType(assembly, "RageCoop.Client.EntityPool");
        Type networking = RequireType(assembly, "RageCoop.Client.Networking");
        Type players = RequireType(assembly, "RageCoop.Client.PlayerList");
        _players = RequireField(players, "Players", typeof(Dictionary<int, Player>));
        _peds = RequireField(pool, "PedsByID", typeof(Dictionary<int, SyncedPed>));
        _characterSetter = typeof(Player).GetProperty(nameof(Player.Character))?.GetSetMethod(true)
            ?? throw Unsupported("Player.Character setter");
        MethodInfo owner = typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.OwnerID))?.GetSetMethod(true)
            ?? throw Unsupported("OwnerID setter");
        MethodInfo expiry = typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.IsOutOfSync))?.GetGetMethod()
            ?? throw Unsupported("IsOutOfSync getter");
        if (owner.ReturnType != typeof(void) || !owner.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(int) }) ||
            expiry.ReturnType != typeof(bool) || expiry.GetParameters().Length != 0 || FindCharacterAssignment(PatchProcessor.GetOriginalInstructions(owner)) < 0)
        {
            throw Unsupported("entity owner or expiry contract");
        }

        MethodInfo[] snapshots =
        {
            RequireMethod(networking, "PedSync", RequirePacket("PedSync")),
            RequireMethod(networking, "VehicleSync", RequirePacket("VehicleSync")),
            RequireMethod(networking, "ProjectileSync", RequirePacket("ProjectileSync"))
        };
        MethodInfo[] cleanup =
        {
            RequireMethod(pool, "Cleanup", typeof(bool), typeof(bool)),
            RequireMethod(pool, "RemoveAllFromPlayer", typeof(int))
        };
        _locks = new[]
        {
            RequireField(pool, "VehiclesLock", typeof(object)).GetValue(null) ?? throw Unsupported("vehicle monitor"),
            RequireField(pool, "PedsLock", typeof(object)).GetValue(null) ?? throw Unsupported("ped monitor"),
            RequireField(pool, "ProjectilesLock", typeof(object)).GetValue(null) ?? throw Unsupported("projectile monitor")
        };
        if (_locks.Distinct().Count() != _locks.Length) { throw Unsupported("distinct pool monitors"); }

        var harmony = new Harmony(PatchId);
        try
        {
            foreach (MethodInfo method in snapshots.Concat(cleanup))
            {
                Patched.Add(method);
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(EngineNetworkGuard), snapshots.Contains(method) ? nameof(BeforeSnapshot) : nameof(BeforePoolOperation)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(EngineNetworkGuard), nameof(AfterPoolOperation)) { priority = Priority.Last });
            }
            Patched.Add(owner);
            harmony.Patch(owner,
                prefix: new HarmonyMethod(typeof(EngineNetworkGuard), nameof(BeforeOwnerChange)),
                postfix: new HarmonyMethod(typeof(EngineNetworkGuard), nameof(AfterOwnerChange)),
                transpiler: new HarmonyMethod(typeof(EngineNetworkGuard), nameof(ProtectPlayerCharacter)));
            Patched.Add(expiry);
            harmony.Patch(expiry, prefix: new HarmonyMethod(typeof(EngineNetworkGuard), nameof(BeforeExpiry)));
            RepairPlayerCharacters();
            _harmony = harmony;
        }
        catch
        {
            foreach (MethodBase method in Patched) { harmony.Unpatch(method, HarmonyPatchType.All, PatchId); }
            Patched.Clear();
            _locks = Array.Empty<object>();
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (_harmony == null) { return; }
        foreach (MethodBase method in Patched) { _harmony.Unpatch(method, HarmonyPatchType.All, PatchId); }
        Patched.Clear();
        _harmony = null;
        // In-flight finalizers retain their own acquired monitor array.
        _locks = Array.Empty<object>();
    }

    private static void BeforePoolOperation(out PoolLease? __state)
    {
        object[] monitors = _locks;
        foreach (object monitor in monitors)
        {
            if (!Monitor.IsEntered(monitor))
            {
                __state = new PoolLease(monitors);
                return;
            }
        }
        __state = null;
    }

    private static bool BeforeSnapshot(out PoolLease? __state)
    {
        // These snapshots are unreliable and superseded by subsequent packets.
        // Never make the network reader hold one monitor while waiting for a
        // game script that may yield with another monitor held.
        var lease = new PoolLease(_locks, wait: false);
        if (!lease.IsComplete)
        {
            Interlocked.Increment(ref _skippedSnapshots);
            __state = null;
            return false;
        }
        __state = lease;
        return true;
    }

    private static void AfterPoolOperation(PoolLease? __state)
    {
        __state?.Dispose();
    }

    private static bool BeforeExpiry(SyncedEntity __instance, ref bool __result)
    {
        var watch = __instance.LastSyncedStopWatch;
        __result = HasExpired(__instance.ID, watch != null && watch.IsRunning, watch?.ElapsedMilliseconds ?? 0);
        return false;
    }

    private static void BeforeOwnerChange(SyncedEntity __instance, out int __state)
    {
        __state = __instance.OwnerID;
    }

    private static void AfterOwnerChange(SyncedEntity __instance, int __state)
    {
        // Locally owned entities receive no snapshots. A later handoff starts
        // a fresh remote grace period without inventing an initial receipt.
        if (__instance.OwnerID != 0 && __instance.OwnerID != __state && __instance.LastSyncedStopWatch?.IsRunning == true)
        {
            __instance.LastSyncedStopWatch.Restart();
        }
    }

    internal static bool HasExpired(int id, bool receivedSnapshot, long elapsedMilliseconds) =>
        id != 0 && receivedSnapshot && elapsedMilliseconds >= ExpiryMilliseconds;

    internal static int FindCharacterAssignment(IList<CodeInstruction> instructions)
    {
        MethodInfo? setter = typeof(Player).GetProperty(nameof(Player.Character))?.GetSetMethod(true);
        int match = -1;
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].operand is not MethodInfo method || method != setter) { continue; }
            if (match >= 0 || (instructions[index].opcode != OpCodes.Call && instructions[index].opcode != OpCodes.Callvirt)) { return -1; }
            match = index;
        }
        return match;
    }

    internal static IEnumerable<CodeInstruction> ProtectPlayerCharacter(IEnumerable<CodeInstruction> original)
    {
        List<CodeInstruction> instructions = original.Select(instruction => new CodeInstruction(instruction)).ToList();
        int assignment = FindCharacterAssignment(instructions);
        if (assignment < 0) { throw Unsupported("OwnerID character assignment IL"); }
        instructions[assignment].opcode = OpCodes.Call;
        instructions[assignment].operand = AccessTools.DeclaredMethod(typeof(EngineNetworkGuard), nameof(AssignPlayerCharacter));
        return instructions;
    }

    private static void AssignPlayerCharacter(Player owner, SyncedPed ped)
    {
        if (ped.ID != 0 && ped.ID == owner.ID && ped.OwnerID == owner.ID)
        {
            (_characterSetter ?? throw Unsupported("character assignment binding")).Invoke(owner, new object[] { ped });
        }
    }

    internal static void RepairPlayerCharacters()
    {
        using (new PoolLease(_locks))
        {
            if (_players?.GetValue(null) is not IDictionary || _peds?.GetValue(null) is not IDictionary peds)
            {
                throw Unsupported("current player roster");
            }
            // EngineRosterGuard is installed first and makes this API copy
            // under the roster monitor. Pool locks do not protect membership.
            foreach (Player player in RageCoop.Client.Scripting.API.Players.Values)
            {
                SyncedPed? ped = peds[player.ID] as SyncedPed;
                if (ped == null || ped.ID != player.ID || ped.OwnerID != player.ID) { ped = null; }
                (_characterSetter ?? throw Unsupported("character assignment binding")).Invoke(player, new object?[] { ped });
            }
        }
    }

    private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, false) ?? throw Unsupported(name);

    private static Type RequirePacket(string name) => RequireType(typeof(Logger).Assembly, "RageCoop.Core.Packets+" + name);

    private static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return field?.FieldType == fieldType ? field : throw Unsupported(type.Name + "." + name);
    }

    private static MethodInfo RequireMethod(Type type, string name, params Type[] arguments)
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, arguments, null);
        return method?.ReturnType == typeof(void) ? method : throw Unsupported(type.Name + "." + name);
    }

    private static InvalidOperationException Unsupported(string contract) =>
        new InvalidOperationException("Unsupported RageCoop engine network contract: " + contract + ".");

    private sealed class PoolLease : IDisposable
    {
        private readonly object[] _monitors;
        private int _held;

        internal bool IsComplete => _held == _monitors.Length;

        internal PoolLease(object[] monitors, bool wait = true)
        {
            _monitors = monitors;
            try
            {
                foreach (object monitor in monitors)
                {
                    bool taken = false;
                    try
                    {
                        if (wait) { Monitor.Enter(monitor, ref taken); }
                        else { Monitor.TryEnter(monitor, 0, ref taken); }
                    }
                    finally { if (taken) { _held++; } }
                    if (!taken)
                    {
                        Dispose();
                        return;
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            while (_held > 0) { Monitor.Exit(_monitors[--_held]); }
        }
    }
}
