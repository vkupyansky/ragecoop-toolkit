using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GTA;
using GTA.Native;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string GuardId = "friendslegacy.ragecoop.enginesend";
    private static readonly Assembly Client = typeof(SyncedVehicle).Assembly;
    private static readonly Type Network = Client.GetType("RageCoop.Client.Networking", true)!;
    private static readonly Type Host = Client.GetType("RageCoop.Client.Main", true)!;
    private static readonly Type Pool = Client.GetType("RageCoop.Client.EntityPool", true)!;
    private static readonly List<MethodInfo> BoundaryTargets = new List<MethodInfo>();
    private static readonly Dictionary<MethodInfo, DynamicMethod> Stubs = new Dictionary<MethodInfo, DynamicMethod>();
    private static readonly List<Captured> Sent = new List<Captured>();
    private static GTA.Player _player = null!;
    private static Vehicle? _lastVehicle;
    private static Vehicle? _currentVehicle;
    private static byte _speed;
    private static byte _station;
    private static VehicleSeat _seat = VehicleSeat.Driver;
    private static int _invalidVehicles = 0;

    private static int Main()
    {
        var boundary = new Harmony("friendslegacy.check.enginesendboundary");
        try
        {
            CheckContracts();
            InstallBoundary(boundary);
            Host.GetField("LocalPlayerID", Static)!.SetValue(null, 1);
            Network.GetField("SyncInterval", Static)!.SetValue(null, 0);
            _player = (GTA.Player)typeof(GTA.Player).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { 0 });
            SyncedPed ped = PedRecord();
            SyncedVehicle first = VehicleRecord(71, 910071, 22);
            SyncedVehicle second = VehicleRecord(72, 910072, 22);
            _lastVehicle = first.MainVehicle;
            _speed = 6;
            bool failed = false;
            try { SendPed(ped); }
            catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { failed = true; }
            Assert(failed && _invalidVehicles == 1 && Sent.Count == 0,
                "The actual unpatched SendPed crashes after its Invalid vehicle branch when the real speed helper reports an exit task and all vehicle references are null.");
            _station = 7;
            SendVehicle(first, true);
            SendVehicle(second, true);
            Assert(Sent.Last().Radio == 7,
                "The actual unpatched SendVehicle reuses the preceding car's station for another car.");
            Sent.Clear();
            EngineSendGuard.Install();
            EngineSendGuard.Install();
            Assert(new[] { EngineSendGuard.FindSend(typeof(SyncedPed))!, EngineSendGuard.FindSend(typeof(SyncedVehicle))! }
                .All(target => Harmony.GetPatchInfo(target)?.Transpilers.Count(patch => patch.owner == GuardId) == 1),
                "Repeated installation adds exactly one patch to each real send method.");
            CheckMissingVehicle(ped);
            CheckOwnership(ped, first);
            CheckRadio(first, second);
            EngineSendGuard.Uninstall();
            EngineSendGuard.Uninstall();
            Assert(new[] { EngineSendGuard.FindSend(typeof(SyncedPed))!, EngineSendGuard.FindSend(typeof(SyncedVehicle))! }
                .All(target => Harmony.GetPatchInfo(target)?.Owners.Contains(GuardId) != true),
                "Resource unload removes both send guards.");
            Console.WriteLine("Actual engine SendPed/SendVehicle checks passed; GTA reads and the transport send boundary were isolated.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            EngineSendGuard.Uninstall();
            foreach (MethodInfo target in BoundaryTargets) { boundary.Unpatch(target, HarmonyPatchType.All, boundary.Id); }
        }
    }

    private static void CheckContracts()
    {
        MethodInfo ped = EngineSendGuard.FindSend(typeof(SyncedPed))!;
        List<CodeInstruction> originalPed = PatchProcessor.GetOriginalInstructions(ped);
        int check = EngineSendGuard.FindOwnerCheck(ped, originalPed);
        Assert(check >= 0, "The exact installed SendPed contains the unguarded ownership lookup.");
        List<CodeInstruction> protectedPed = EngineSendGuard.ProtectMissingVehicle(originalPed).ToList();
        Assert(protectedPed.Count == originalPed.Count && protectedPed.Where((code, index) =>
            code.opcode != originalPed[index].opcode || !Equals(code.operand, originalPed[index].operand)).Count() == 1,
            "The ped patch replaces only the IsLocal call and preserves the complete send method.");
        MethodInfo vehicle = EngineSendGuard.FindSend(typeof(SyncedVehicle))!;
        List<CodeInstruction> originalVehicle = PatchProcessor.GetOriginalInstructions(vehicle);
        int marker = EngineSendGuard.FindRadioReset(vehicle, originalVehicle);
        Assert(marker >= 0, "The exact installed SendVehicle contains the full-packet LastVehicle radio condition.");
        List<CodeInstruction> reset = EngineSendGuard.ResetRadioStation(originalVehicle).ToList();
        Assert(reset.Count == originalVehicle.Count + 3, "The radio patch adds only an explicit off assignment before the original condition.");
        for (int index = 0; index < originalVehicle.Count; index++)
        {
            CodeInstruction actual = reset[index < marker ? index : index + 3];
            Assert(actual.opcode == originalVehicle[index].opcode && Equals(actual.operand, originalVehicle[index].operand),
                "Every existing vehicle-send instruction remains unchanged.");
        }
        foreach (Func<CodeInstruction[]> rewrite in new Func<CodeInstruction[]>[]
        {
            () => EngineSendGuard.ProtectMissingVehicle(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(),
            () => EngineSendGuard.ResetRadioStation(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray()
        })
        {
            bool rejected = false;
            try { rewrite(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "An unknown send body cannot receive a guessed engine edit.");
        }
        originalPed[check + 1].opcode = OpCodes.Brfalse_S;
        Assert(EngineSendGuard.FindOwnerCheck(ped, originalPed) < 0, "A changed ownership condition is rejected.");
        int full = originalVehicle.FindIndex(code => code.opcode == OpCodes.Ldarg_1);
        originalVehicle[full + 3].opcode = OpCodes.Brtrue;
        Assert(EngineSendGuard.FindRadioReset(vehicle, originalVehicle) < 0, "A changed full-state gate is rejected before the radio patch.");
    }

    private static void CheckMissingVehicle(SyncedPed ped)
    {
        _currentVehicle = null;
        foreach (byte speed in new byte[] { 4, 5, 6 })
        {
            Sent.Clear();
            _speed = speed;
            SendPed(ped);
            Assert(Sent.Count == 1 && Sent[0].Kind == "PedSync" && Sent[0].VehicleId == 0 && Sent[0].Speed == speed &&
                Sent[0].Channel == 10 && Sent[0].Delivery == NetDeliveryMethod.UnreliableSequenced,
                "The actual sender finishes a transitional missing-vehicle state without inventing an ID or ownership event.");
        }
        Assert(_invalidVehicles == 4, "The original specific diagnostic is preserved for every missing-vehicle snapshot.");
    }

    private static void CheckOwnership(SyncedPed ped, SyncedVehicle vehicle)
    {
        IDictionary handles = (IDictionary)Pool.GetField("VehiclesByHandle", Static)!.GetValue(null)!;
        handles.Add(vehicle.MainVehicle.Handle, vehicle);
        try
        {
            _currentVehicle = vehicle.MainVehicle;
            _speed = 4;
            _seat = VehicleSeat.Driver;
            Sent.Clear();
            SendPed(ped);
            Assert(vehicle.OwnerID == 1 && Sent.Count == 2 && Sent[0].Kind == "OwnerChanged" &&
                Sent[0].Channel == 12 && Sent[0].Delivery == NetDeliveryMethod.ReliableOrdered && Sent[1].VehicleId == vehicle.ID,
                "A registered remote vehicle still transfers to its real local driver through the original reliable ownership event and packet.");
            Sent.Clear();
            SendPed(ped);
            Assert(Sent.Count == 1 && Sent[0].Kind == "PedSync", "An already local vehicle does not emit a duplicate ownership event.");
            typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(vehicle, 22);
            _seat = VehicleSeat.Passenger;
            Sent.Clear();
            SendPed(ped);
            Assert(Sent.Count == 1 && vehicle.OwnerID == 22, "A passenger still cannot claim ownership.");
        }
        finally
        {
            handles.Remove(vehicle.MainVehicle.Handle);
            _currentVehicle = null;
        }
    }

    private static void CheckRadio(SyncedVehicle first, SyncedVehicle second)
    {
        Sent.Clear();
        _lastVehicle = first.MainVehicle;
        _station = 7;
        SendVehicle(first, true);
        SendVehicle(second, true);
        Assert(Sent[0].Radio == 7 && Sent[1].Radio == 255 && Sent.All(packet => packet.Full && packet.Channel == 9 && packet.Delivery == NetDeliveryMethod.UnreliableSequenced),
            "The actual full sender retains the player's station and sends explicit off for the next different car with unchanged delivery.");
        _station = 12;
        SendVehicle(first, true);
        SendVehicle(second, false);
        Assert(Sent[2].Radio == 12 && !Sent[3].Full && Sent[3].Bytes.Length == 75,
            "The player's new station is read normally; a compact movement packet gains no radio bytes or full-state flag.");
        _station = 255;
        SendVehicle(first, true);
        Assert(Sent.Last().Radio == 255, "The player's radio off state is preserved.");
        _lastVehicle = null;
        SendVehicle(second, true);
        Assert(Sent.Last().Radio == 255, "A missing LastVehicle also produces explicit off without guessing another station.");
    }

    private static void InstallBoundary(Harmony harmony)
    {
        MethodInfo[] bodies =
        {
            EngineSendGuard.FindSend(typeof(SyncedPed))!, EngineSendGuard.FindSend(typeof(SyncedVehicle))!,
            Client.GetType("RageCoop.Client.PedExtensions", true)!.GetMethod("GetPedSpeed", Static)!,
            Client.GetType("RageCoop.Client.Util", true)!.GetMethod("GetPlayerRadioIndex", Static)!
        };
        foreach (MethodInfo body in bodies)
        {
            harmony.Patch(body, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateGtaReads)) { priority = Priority.Last });
            BoundaryTargets.Add(body);
        }
        MethodInfo send = Network.GetMethod("SendSync", Static)!;
        harmony.Patch(send, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureSend)));
        BoundaryTargets.Add(send);
        MethodInfo movie = typeof(Scaleform).GetMethod("RequestMovie", Static, null, new[] { typeof(string) }, null)!;
        harmony.Patch(movie, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMovie)));
        BoundaryTargets.Add(movie);
    }

    // Only GTA reads/extraction and logging are replaced. The actual speed
    // decision, vehicle lookup/ownership, full branch and packet writes run.
    private static IEnumerable<CodeInstruction> IsolateGtaReads(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && NeedsGtaStub(method))
            {
                code.opcode = OpCodes.Call;
                code.operand = NativeStub(method);
            }
            yield return code;
        }
    }

    private static bool NeedsGtaStub(MethodInfo method)
    {
        string? type = method.DeclaringType?.FullName;
        if (method.DeclaringType?.IsValueType == true) { return false; }
        if (type == "RageCoop.Core.Logger" && method.Name == "Error") { return true; }
        if (type == "RageCoop.Client.Memory") { return true; }
        if (type == "RageCoop.Client.PedExtensions" && (method.Name == "GetPedFlags" || method.Name == "GetSeatTryingToEnter" || method.Name == "IsTaskActive")) { return true; }
        if (type == "RageCoop.Client.VehicleExtensions" && new[] { "GetVehicleFlags", "GetVehicleDamageModel", "GetVehicleMods" }.Contains(method.Name)) { return true; }
        return method.DeclaringType?.Assembly == typeof(Ped).Assembly &&
            (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name == "IsSittingInVehicle" ||
                method.Name == "IsTaskActive" || (type == "GTA.Native.Function" && method.Name == "Call"));
    }

    private static DynamicMethod NativeStub(MethodInfo target)
    {
        if (Stubs.TryGetValue(target, out DynamicMethod? existing)) { return existing; }
        Type[] arguments = target.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (!target.IsStatic) { arguments = new[] { target.DeclaringType! }.Concat(arguments).ToArray(); }
        var stub = new DynamicMethod("GtaRead_" + Stubs.Count, target.ReturnType, arguments, typeof(Program), true);
        ILGenerator il = stub.GetILGenerator();
        string? field = target.Name switch
        {
            "get_Player" => nameof(_player),
            "get_LastVehicle" when target.DeclaringType == typeof(GTA.Player) => nameof(_lastVehicle),
            "get_CurrentVehicle" => nameof(_currentVehicle),
            "get_SeatIndex" => nameof(_seat),
            "GetSeatTryingToEnter" => nameof(_seat),
            _ => null
        };
        if (target.Name == "Error")
        {
            il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_invalidVehicles), Static)!);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stsfld, typeof(Program).GetField(nameof(_invalidVehicles), Static)!);
        }
        else if (field != null) { il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(field, Static)!); }
        else if (target.Name == "IsSittingInVehicle") { il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(Sitting), Static)!); }
        else if (target.Name == "IsTaskActive")
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(nameof(TaskActive), Static)!);
        }
        else if (target.DeclaringType == typeof(Function) && target.Name == "Call" && target.ReturnType == typeof(int) && target.GetParameters().Length == 1)
        {
            il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(_station), Static)!);
        }
        else if (target.ReturnType == typeof(Dictionary<int, int>)) { il.Emit(OpCodes.Newobj, target.ReturnType.GetConstructor(Type.EmptyTypes)!); }
        else if (target.ReturnType == typeof(string)) { il.Emit(OpCodes.Ldstr, "CHECK"); }
        else if (target.ReturnType != typeof(void))
        {
            if (target.ReturnType.IsValueType)
            {
                LocalBuilder value = il.DeclareLocal(target.ReturnType);
                il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Initobj, target.ReturnType);
                il.Emit(OpCodes.Ldloc, value);
            }
            else { il.Emit(OpCodes.Ldnull); }
        }
        il.Emit(OpCodes.Ret);
        Stubs.Add(target, stub);
        return stub;
    }

    private static bool Sitting() => _speed == 4;
    private static bool TaskActive(int task) =>
        (_speed == 5 && task == Convert.ToInt32(Enum.Parse(Client.GetType("TaskType", true)!, "CTaskEnterVehicle"))) ||
        (_speed == 6 && task == Convert.ToInt32(Enum.Parse(Client.GetType("TaskType", true)!, "CTaskExitVehicle")));
    private static bool SkipMovie(ref Scaleform? __result) { __result = null; return false; }

    private static bool CaptureSend(object[] __args)
    {
        object packet = __args[0];
        Type type = packet.GetType();
        object? Get(string name) => type.GetProperty(name)?.GetValue(packet);
        var outgoing = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
        type.GetMethod("Pack")!.Invoke(packet, new object[] { outgoing });
        Sent.Add(new Captured
        {
            Kind = type.Name,
            VehicleId = (int?)Get("VehicleID") ?? 0,
            Speed = (byte?)Get("Speed") ?? 0,
            Radio = (byte?)Get("RadioStation") ?? 255,
            Full = (Convert.ToUInt64(Get("Flags") ?? 0) & 8192) != 0,
            Channel = Convert.ToInt32(__args[1]),
            Delivery = (NetDeliveryMethod)__args[2],
            Bytes = outgoing.Data.Take(outgoing.LengthBytes).ToArray()
        });
        return false;
    }

    private static SyncedPed PedRecord()
    {
        var ped = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        Initialize(ped, 1, 1);
        typeof(SyncedPed).GetProperty(nameof(SyncedPed.MainPed))!.SetValue(ped, Native<Ped>(910001));
        return ped;
    }

    private static SyncedVehicle VehicleRecord(int id, int handle, int owner)
    {
        var vehicle = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        Initialize(vehicle, id, owner);
        typeof(SyncedVehicle).GetProperty(nameof(SyncedVehicle.MainVehicle))!.SetValue(vehicle, Native<Vehicle>(handle));
        return vehicle;
    }

    private static void Initialize(SyncedEntity entity, int id, int owner)
    {
        typeof(SyncedEntity).GetProperty(nameof(SyncedEntity.ID))!.SetValue(entity, id);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(entity, owner);
        typeof(SyncedEntity).GetProperty("LastSentStopWatch", Instance)!.SetValue(entity, Stopwatch.StartNew());
    }

    private static T Native<T>(int handle) where T : Entity => (T)typeof(T).GetConstructor(Instance, null, new[] { typeof(int) }, null)!.Invoke(new object[] { handle });
    private static void SendPed(SyncedPed ped) => EngineSendGuard.FindSend(typeof(SyncedPed))!.Invoke(null, new object[] { ped, false });
    private static void SendVehicle(SyncedVehicle vehicle, bool full) => EngineSendGuard.FindSend(typeof(SyncedVehicle))!.Invoke(null, new object[] { vehicle, full });
    private static void Assert(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Captured
    {
        internal string Kind = "";
        internal int VehicleId;
        internal byte Speed;
        internal byte Radio;
        internal bool Full;
        internal int Channel;
        internal NetDeliveryMethod Delivery;
        internal byte[] Bytes = Array.Empty<byte>();
    }
}
