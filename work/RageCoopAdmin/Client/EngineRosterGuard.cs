using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using Blip = GTA.Blip;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

internal static class EngineRosterGuard
{
    private const string PatchId = "friendslegacy.ragecoop.engineroster";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object Gate = new object();
    private static readonly Type Roster = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.PlayerList", true)!;
    private static readonly FieldInfo Players = Roster.GetField("Players", Static)!;
    private static readonly MethodInfo Queue = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Main", true)!
        .GetMethod("QueueAction", Static, null, new[] { typeof(Action) }, null)!;
    private static readonly Type Pool = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly FieldInfo Connection = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Networking", true)!
        .GetField("ServerConnection", Static)!;
    private static readonly MethodInfo RemoveProjectile = Pool.GetMethod("RemoveProjectile", Static, null, new[] { typeof(int), typeof(string) }, null)!;
    private static readonly MethodInfo Count = typeof(Dictionary<int, Player>).GetProperty("Count")!.GetGetMethod()!;
    private static readonly PropertyInfo FakeBlip = typeof(Player).GetProperty("FakeBlip")!;
    private static FieldInfo? _queuedPlayer;
    private static readonly Dictionary<string, string> Bodies = new Dictionary<string, string>
    {
        ["PlayerList.SetPlayer"] = "74a0273a1dcc3494fc9bccf61b95784dea085f17a97179e810c673a8aea5e845",
        ["PlayerList.RemovePlayer"] = "1bece7cbee48aa3df9e9d6c730d851dee9358efba12230f802d324dd1b9981de",
        ["PlayerList.Cleanup"] = "b32e86070aca8111af622a8b7c72948479f05b02cdd438f795d6bd8f043a7ed1",
        ["PlayerList.UpdatePlayer"] = "05c58aefb26aa6e181e317f1f3e6eb790a1b736c664a78ac90091f3680996c80",
        ["PlayerList.GetPlayer"] = "28cb59ee7065e586e2437e2e6be11c5cf14c4c55df970160a5bc03b5a7a521e3",
        ["PlayerList.Update"] = "de0f6a8aba27ffbffe757e0174e7f1dd00df3253de9aba7c45fad9270935d0d6",
        ["EntityPool.RemoveAllFromPlayer"] = "940d1339b72534ab29d5c7624608d450a745886712a56eb73b31e98a75477f58",
        ["EntityPool.UpdateTargets"] = "e99a858bb7f910f1b6691c16b4a0f75dcd3d2b55e947492d3af200e7518f52b3",
        ["API.get_Players"] = "5544439bf8d60df701ffa67f1eeb24b3613230efb449281776e07efd70403611",
        ["HolePunch.DoPunch"] = "f6f3a34839977dd73deb32782c2618dddcdf710a3e7166f2913f01eace31e993",
        ["HolePunch.Add"] = "0196ecb692a02ae3ea801403cb85c1ef6ca4293878cf38b0bb54ebcaef5195c0",
        ["HolePunch.Punched"] = "b9cde789b080e4dcba193c48fd9ecd62f8bded840ceadee004aa9cd0e0cb9ba9",
        ["Networking.ProcessMessage"] = "2eb20f51ef3a68412335373ce6772092f3f84d3344188fed8aca4c555b74abea",
        ["Networking.PedSync"] = "22c896f801fe51436e0bc0ef1e13831046473b5b6a7a27b88a0ed5b06aebb7eb",
        ["Networking.VehicleSync"] = "206f751d7e1a1eadc1cdcc3ede5d30003db0ad0aebaa73851aeee090f2c0afab"
    };
    private static Harmony? _harmony;
    private static readonly List<MethodBase> Targets = new List<MethodBase>();

    internal static void Install()
    {
        if (_harmony != null) return;
        if (!HasSupportedContract()) throw Unsupported();
        var harmony = new Harmony(PatchId);
        try
        {
            Patch(harmony, Roster.GetMethod("SetPlayer", Static)!, nameof(Enter), null, nameof(Exit));
            Patch(harmony, Roster.GetMethod("RemovePlayer", Static)!, nameof(RemovePlayer));
            Patch(harmony, Roster.GetMethod("GetPlayer", Static, null, new[] { typeof(int) }, null)!, nameof(GetPlayer));
            Patch(harmony, Roster.GetMethod("Cleanup", Static)!, nameof(Cleanup));
            foreach (MethodInfo method in ReadTargets()) Patch(harmony, method, null, nameof(SnapshotReads));
            Type network = typeof(SyncedEntity).Assembly.GetType("RageCoop.Client.Networking", true)!;
            foreach (string name in new[] { "PedSync", "VehicleSync" })
                Patch(harmony, network.GetMethod(name, Static)!, nameof(HasPlayers));
            MethodInfo update = Roster.GetMethod("UpdatePlayer", Static)!;
            _queuedPlayer = QueuedPlayerField(update);
            Patch(harmony, update, null, nameof(GuardPlayerUpdate));
            Patch(harmony, Pool.GetMethod("RemoveAllFromPlayer", Static)!, postfix: nameof(RemovePlayerProjectiles));
            _harmony = harmony;
        }
        catch
        {
            foreach (MethodBase target in Targets) harmony.Unpatch(target, HarmonyPatchType.All, PatchId);
            Targets.Clear();
            _queuedPlayer = null;
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (_harmony == null) return;
        foreach (MethodBase target in Targets) _harmony.Unpatch(target, HarmonyPatchType.All, PatchId);
        Targets.Clear();
        _harmony = null;
        _queuedPlayer = null;
    }

    internal static IEnumerable<MethodInfo> ReadTargets()
    {
        Assembly engine = typeof(SyncedEntity).Assembly;
        yield return Roster.GetMethod("Update", Static)!;
        yield return engine.GetType("RageCoop.Client.Scripting.API", true)!.GetProperty("Players")!.GetGetMethod()!;
        foreach (string name in new[] { "DoPunch", "Add", "Punched" })
            yield return engine.GetType("RageCoop.Client.HolePunch", true)!.GetMethod(name, Static)!;
        yield return engine.GetType("RageCoop.Client.EntityPool", true)!.GetMethod("UpdateTargets", Static)!;
        foreach (string name in new[] { "ProcessMessage", "PedSync", "VehicleSync" })
            yield return engine.GetType("RageCoop.Client.Networking", true)!.GetMethod(name, Static)!;
    }

    private static void Patch(Harmony harmony, MethodInfo target, string? prefix = null, string? transpiler = null,
        string? finalizer = null, string? postfix = null)
    {
        if (target == null) throw Unsupported();
        Targets.Add(target);
        HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(EngineRosterGuard), name);
        harmony.Patch(target, prefix: Hook(prefix), postfix: Hook(postfix), transpiler: Hook(transpiler), finalizer: Hook(finalizer));
    }

    private static void Enter(out bool __state)
    {
        __state = false;
        Monitor.Enter(Gate);
        __state = true;
    }

    private static Exception? Exit(Exception? __exception, bool __state)
    {
        if (__state) Monitor.Exit(Gate);
        return __exception;
    }

    private static Dictionary<int, Player> Snapshot()
    {
        lock (Gate) return new Dictionary<int, Player>((Dictionary<int, Player>)Players.GetValue(null)!);
    }

    private static bool HasPlayers()
    {
        lock (Gate) return ((Dictionary<int, Player>)Players.GetValue(null)!).Count != 0;
    }

    private static int ShareSoftLimit(int limit)
    {
        lock (Gate)
        {
            int count = ((Dictionary<int, Player>)Players.GetValue(null)!).Count;
            return count == 0 ? 0 : limit / count;
        }
    }

    private static bool GetPlayer(int __0, ref Player? __result)
    {
        lock (Gate) ((Dictionary<int, Player>)Players.GetValue(null)!).TryGetValue(__0, out __result);
        return false;
    }

    private static IEnumerable<CodeInstruction> SnapshotReads(IEnumerable<CodeInstruction> original)
    {
        var instructions = original.Select(code => new CodeInstruction(code)).ToList();
        for (int index = 0; index < instructions.Count; index++)
        {
            CodeInstruction code = instructions[index];
            if (code.opcode == OpCodes.Ldsfld && Equals(code.operand, Players))
            {
                if (index + 2 < instructions.Count && instructions[index + 1].Calls(Count) && instructions[index + 2].opcode == OpCodes.Div)
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(EngineRosterGuard), nameof(ShareSoftLimit));
                    code.labels.AddRange(instructions[index + 1].labels);
                    code.labels.AddRange(instructions[index + 2].labels);
                    code.blocks.AddRange(instructions[index + 1].blocks);
                    code.blocks.AddRange(instructions[index + 2].blocks);
                    index += 2;
                    yield return code;
                    continue;
                }
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(EngineRosterGuard), nameof(Snapshot));
            }
            yield return code;
        }
    }

    private static bool RemovePlayer(int __0)
    {
        Player? player;
        lock (Gate)
        {
            var players = (Dictionary<int, Player>)Players.GetValue(null)!;
            if (!players.TryGetValue(__0, out player)) return false;
            players.Remove(__0);
        }
        // Main executes callbacks while holding its queue monitor. Never enter
        // that monitor while holding Gate: callbacks also read roster membership.
        Queue.Invoke(null, new object[] { new Action(() => DeleteFakeBlip(player)) });
        return false;
    }

    private static bool Cleanup()
    {
        Player[] previous;
        lock (Gate)
        {
            previous = ((Dictionary<int, Player>)Players.GetValue(null)!).Values.ToArray();
            Players.SetValue(null, new Dictionary<int, Player>());
        }
        // GTA calls stay outside the roster monitor, including disconnect cleanup.
        foreach (Player player in previous) DeleteFakeBlip(player);
        return false;
    }

    private static IEnumerable<CodeInstruction> GuardPlayerUpdate(IEnumerable<CodeInstruction> original)
    {
        MethodInfo update = Roster.GetMethod("UpdatePlayer", Static)!;
        MethodInfo id = update.GetParameters()[0].ParameterType.GetProperty("PedID")!.GetGetMethod()!;
        foreach (CodeInstruction instruction in original)
        {
            if (instruction.Calls(Queue))
            {
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                yield return load;
                yield return new CodeInstruction(OpCodes.Callvirt, id);
                yield return CodeInstruction.Call(typeof(EngineRosterGuard), nameof(QueuePlayerUpdate));
            }
            else yield return new CodeInstruction(instruction);
        }
    }

    private static void QueuePlayerUpdate(Action action, int id)
    {
        Player? expected = action.Target == null ? null : _queuedPlayer?.GetValue(action.Target) as Player;
        if (expected == null || expected.ID != id) return;
        object? connection = Connection.GetValue(null);
        Queue.Invoke(null, new object[] { new Action(() =>
        {
            try
            {
                if (RageCoop.Client.Scripting.API.IsOnServer && ReferenceEquals(connection, Connection.GetValue(null)) && IsCurrent(id, expected)) action();
            }
            finally
            {
                if (!RageCoop.Client.Scripting.API.IsOnServer || !ReferenceEquals(connection, Connection.GetValue(null)) || !IsCurrent(id, expected)) DeleteFakeBlip(expected);
            }
        }) });
    }

    private static bool IsCurrent(int id, Player expected)
    {
        lock (Gate) return ((Dictionary<int, Player>)Players.GetValue(null)!).TryGetValue(id, out Player? actual) && ReferenceEquals(expected, actual);
    }

    private static void DeleteFakeBlip(Player player)
    {
        Blip? blip = player.FakeBlip;
        if (blip?.Exists() == true) blip.Delete();
        FakeBlip.SetValue(player, null);
    }

    private static void RemovePlayerProjectiles(int __0)
    {
        var entries = (IDictionary)Pool.GetField("ProjectilesByID", Static)!.GetValue(null)!;
        var ids = new List<int>();
        foreach (DictionaryEntry entry in entries)
        {
            object projectile = entry.Value!;
            var shooter = projectile.GetType().GetProperty("Shooter")!.GetValue(projectile) as SyncedEntity;
            if (shooter != null && (shooter.ID == __0 || shooter.OwnerID == __0)) ids.Add((int)entry.Key);
        }
        foreach (int id in ids) RemoveProjectile.Invoke(null, new object[] { id, "PlayerLeft" });
    }

    internal static MethodInfo[] MutationTargets() => new[] { "SetPlayer", "RemovePlayer", "Cleanup", "UpdatePlayer" }
        .Select(name => Roster.GetMethod(name, Static)!).Concat(new[]
        {
            Roster.GetMethod("GetPlayer", Static, null, new[] { typeof(int) }, null)!,
            Pool.GetMethod("RemoveAllFromPlayer", Static)!
        }).ToArray();

    private static FieldInfo? QueuedPlayerField(MethodInfo update)
    {
        FieldInfo[] fields = PatchProcessor.GetOriginalInstructions(update)
            .Where(code => code.opcode == OpCodes.Stfld && code.operand is FieldInfo field && field.FieldType == typeof(Player))
            .Select(code => (FieldInfo)code.operand).ToArray();
        return fields.Length == 1 ? fields[0] : null;
    }

    internal static bool HasSupportedContract()
    {
        if (Players.FieldType != typeof(Dictionary<int, Player>) || Queue.ReturnType != typeof(void) ||
            Connection.FieldType.FullName != "Lidgren.Network.NetConnection" ||
            FakeBlip.PropertyType != typeof(Blip) || FakeBlip.GetSetMethod(true) == null || RemoveProjectile.ReturnType != typeof(void)) return false;
        MethodInfo update = Roster.GetMethod("UpdatePlayer", Static)!;
        FieldInfo? captured = QueuedPlayerField(update);
        MethodInfo[] targets = MutationTargets().Concat(ReadTargets()).Distinct().ToArray();
        return targets.Length == Bodies.Count && targets.All(MatchesTarget) && captured != null && captured.DeclaringType?.DeclaringType == Roster &&
            PatchProcessor.GetOriginalInstructions(update).Count(code => code.Calls(Queue)) == 1 &&
            ReadTargets().All(method => PatchProcessor.GetOriginalInstructions(method).Any(code => code.LoadsField(Players))) &&
            MutationTargets().All(method => method != null);
    }

    internal static bool MatchesTarget(MethodInfo method)
    {
        if (method.DeclaringType?.Assembly != typeof(SyncedEntity).Assembly ||
            !Bodies.TryGetValue(method.DeclaringType.Name + "." + method.Name, out string? expected)) return false;
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return false;
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
    }

    private static InvalidOperationException Unsupported() => new InvalidOperationException("Unsupported RageCoop roster contract.");
}
