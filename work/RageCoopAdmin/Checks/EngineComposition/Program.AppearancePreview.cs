using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using GTA;
using GTA.Native;
using GTA.UI;
using HarmonyLib;
using RageCoop.Client;
using RageCoop.Client.Scripting;

internal static partial class Program
{
    private static readonly Ped AppearancePed = SpawnNative<Ped>(860001);
    private static readonly Vehicle AppearanceCar = SpawnNative<Vehicle>(860002);
    private static readonly List<(int Event, object[] Args)> AppearanceSent = new List<(int, object[])>();
    private static readonly Dictionary<MethodInfo, DynamicMethod> AppearanceStubs = new Dictionary<MethodInfo, DynamicMethod>();
    private static int _appearancePreviewMod, _appearanceNativeWrites;

    private static void CheckAppearancePreviewComposition()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        Type native = Resource.GetType("RageCoopAdmin.Client.NativeVehicleAppearance", true)!;
        MethodInfo Method(string name) => script.GetMethod(name, Instance)!;
        object host = FormatterServices.GetUninitializedObject(script);
        foreach (string name in new[] { "_appearanceStates", "_appearanceEdits", "_failedAppearanceChanges" })
        {
            FieldInfo field = script.GetField(name, Instance)!;
            field.SetValue(host, Activator.CreateInstance(field.FieldType, true));
        }
        void Call(string name, params object[] args) => Method(name).Invoke(host, args);
        var boundary = new Harmony("friendslegacy.check.appearancepreview");
        void Patch(MethodInfo target, string prefix) => boundary.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
        IDictionary vehicles = (IDictionary)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!;
        IDictionary handles = (IDictionary)Pool.GetField("VehiclesByHandle", Static)!.GetValue(null)!;
        var record = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        typeof(SyncedEntity).GetProperty("ID")!.SetValue(record, -860003);
        typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(record, 1);
        typeof(SyncedVehicle).GetProperty("MainVehicle")!.SetValue(record, AppearanceCar);
        try
        {
            vehicles.Add(record.ID, record); handles.Add(AppearanceCar.Handle, record);
            Patch(typeof(API).GetProperty("IsOnServer")!.GetGetMethod()!, nameof(AppearanceConnected));
            Patch(typeof(API).GetProperty("LocalPlayerID")!.GetGetMethod()!, nameof(AppearanceLocalId));
            Patch(typeof(API).GetMethod("SendCustomEvent", Static, null, new[] { typeof(int), typeof(object[]) }, null)!, nameof(AppearanceSend));
            foreach (MethodInfo notification in typeof(Notification).GetMethods(Static).Where(method => method.Name == "PostTicker"))
                Patch(notification, nameof(SkipMenuLayout));
            foreach (MethodInfo target in new[] { Method("CaptureOwnerAppearance"), Method("TryApplyVehicleAppearanceCore"), Method("FindAppearanceSnapshot") }
                .Concat(native.GetMethods(Static).Where(method => new[] { "Capture", "Apply", "ReadRgb", "ApplyRgb" }.Contains(method.Name))))
                boundary.Patch(target, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateAppearanceNatives)));

            long now = Stopwatch.GetTimestamp();
            void Capture(int selection)
            {
                _appearancePreviewMod = selection;
                now += Stopwatch.Frequency;
                Call("CaptureOwnerAppearance", now);
            }
            object queue = script.GetField("_appearanceEdits", Instance)!.GetValue(host)!;
            MethodInfo take = queue.GetType().GetMethod("TryTake", Instance)!;
            object Take(long milliseconds)
            {
                object?[] args = { milliseconds, null };
                Assert((bool)take.Invoke(queue, args)!, "The actual appearance queue provides the expected pending snapshot.");
                return args[1]!;
            }
            int Nonce(object edit) => (int)edit.GetType().GetProperty("Nonce", Instance)!.GetValue(edit)!;
            string Xml(object edit) => (string)edit.GetType().GetProperty("Xml", Instance)!.GetValue(edit)!;
            void Echo(object edit, int revision) => Call("OnVehicleAppearanceState", new CustomEventReceivedArgs
            { Args = new object[] { record.ID, revision, Xml(edit), 1, Nonce(edit) } });
            void Apply() => Call("TryApplyVehicleAppearanceCore", record, false);

            Capture(0); object original = Take(1000); Echo(original, 1);
            _appearanceNativeWrites = 0;
            foreach (int preview in new[] { 1, 2, 3, 4 })
            {
                _appearancePreviewMod = preview; Apply();
                Assert(_appearancePreviewMod == preview && _appearanceNativeWrites == 0,
                    "An uncaptured local LSC preview remains untouched by the actual appearance apply path.");
            }
            Capture(5); object previewEdit = Take(2000);
            Capture(0); object cancel = Take(2500);
            Echo(previewEdit, 2); Apply();
            Assert(_appearancePreviewMod == 0 && _appearanceNativeWrites == 0,
                "An older captured preview echo cannot undo a later cancellation.");
            Echo(cancel, 3); Apply();
            Capture(6); object purchase = Take(3000); Echo(cancel, 3); Echo(purchase, 4); Apply();
            Assert(_appearancePreviewMod == 6 && _appearanceNativeWrites == 0,
                "Fast preview, cancel and final purchase settle on the real owner's last capture without native replay.");

            Capture(7); object expired = Take(4000);
            Take(5500); Take(7000); Take(8500);
            script.GetField("_lastAppearanceTick", Instance)!.SetValue(host, Stopwatch.GetTimestamp());
            // Tick expires the real fourth retry before its throttled capture.
            object pending = ((IDictionary)queue.GetType().GetField("_pending", Instance)!.GetValue(queue)!)[record.ID]!;
            pending.GetType().GetProperty("NextAttemptMilliseconds", Instance)!.SetValue(pending, 0L);
            queue.GetType().GetField("_lastSent", Instance)!.SetValue(queue, long.MinValue);
            Call("TickVehicleTools");
            _appearancePreviewMod = 8;
            Echo(expired, 5); Apply();
            Assert(_appearancePreviewMod == 8 && _appearanceNativeWrites == 0,
                "A late acknowledgement after retry expiry cannot replace a newer live owner preview before its next capture.");
            Capture(9); object final = Take(Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000 + 2000);
            Echo(expired, 5); Echo(final, 6); Apply();
            Assert(_appearancePreviewMod == 9 && _appearanceNativeWrites == 0,
                "The owner's later final selection remains authoritative after a delayed expired acknowledgement.");

            Call("OnVehicleAppearanceState", new CustomEventReceivedArgs
            { Args = new object[] { record.ID, 7, Xml(purchase), 1, 0 } });
            Apply();
            Assert(_appearancePreviewMod == 6 && _appearanceNativeWrites > 0,
                "A server-created garage configuration with nonce zero still applies to the actual local vehicle.");
            typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(record, 2);
            _appearancePreviewMod = 10; _appearanceNativeWrites = 0;
            Echo(final, 8); Apply();
            Assert(_appearancePreviewMod == 9 && _appearanceNativeWrites > 0,
                "The same complete snapshot still applies after this vehicle transfers to another owner.");
            Console.WriteLine("Actual appearance preview checks passed: scrolling, cancel, purchase, expired acknowledgements, garage creation and remote ownership; GTA natives isolated.");
        }
        finally
        {
            vehicles.Remove(record.ID); handles.Remove(AppearanceCar.Handle);
            boundary.UnpatchAll(boundary.Id); AppearanceSent.Clear(); AppearanceStubs.Clear();
            _appearanceNativeWrites = 0; _appearancePreviewMod = 0;
        }
    }

    private static bool AppearanceConnected(ref bool __result) { __result = true; return false; }
    private static bool AppearanceLocalId(ref int __result) { __result = 1; return false; }
    private static bool AppearanceSend(int __0, object[] __1) { AppearanceSent.Add((__0, __1)); return false; }

    private static IEnumerable<CodeInstruction> IsolateAppearanceNatives(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method &&
                (method.DeclaringType == typeof(Function) ||
                 method.DeclaringType == typeof(InputArgument) && method.Name == "op_Implicit" && method.GetParameters()[0].ParameterType == typeof(string) ||
                 method.DeclaringType == typeof(Game) && method.Name == "get_Player" ||
                 typeof(Entity).IsAssignableFrom(method.DeclaringType) && method.Name is "Exists" or "get_IsDead" or "get_Model" or "get_Driver" or "get_CurrentVehicle" ||
                 method.DeclaringType == typeof(GTA.Player) && method.Name == "get_Character"))
            { code.opcode = OpCodes.Call; code.operand = AppearanceStub(method); }
            yield return code;
        }
    }

    private static DynamicMethod AppearanceStub(MethodInfo target)
    {
        if (AppearanceStubs.TryGetValue(target, out DynamicMethod? existing)) return existing;
        Type[] args = target.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (!target.IsStatic) args = new[] { target.DeclaringType! }.Concat(args).ToArray();
        var stub = new DynamicMethod("AppearanceBoundary" + AppearanceStubs.Count, target.ReturnType, args, typeof(Program), true);
        ILGenerator il = stub.GetILGenerator();
        if (target.DeclaringType == typeof(Function))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, args.Length - 1); il.Emit(OpCodes.Newarr, typeof(InputArgument));
            for (int index = 1; index < args.Length; index++)
            { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, index - 1); il.Emit(OpCodes.Ldarg, index); il.Emit(OpCodes.Stelem_Ref); }
            string handler = target.ReturnType == typeof(int) ? nameof(AppearanceReadInt) : target.ReturnType == typeof(bool) ? nameof(AppearanceReadBool) :
                target.ReturnType == typeof(string) ? nameof(AppearanceReadString) : nameof(AppearanceWrite);
            il.Emit(OpCodes.Call, typeof(Program).GetMethod(handler, Static)!);
        }
        else if (target.Name == "get_Player") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(SpawnPlayer), Static)!);
        else if (target.Name is "get_Character" or "get_Driver") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(AppearancePed), Static)!);
        else if (target.Name == "get_CurrentVehicle") il.Emit(OpCodes.Ldsfld, typeof(Program).GetField(nameof(AppearanceCar), Static)!);
        else if (target.Name == "Exists") il.Emit(OpCodes.Ldc_I4_1);
        else if (target.Name == "get_IsDead") il.Emit(OpCodes.Ldc_I4_0);
        else if (target.Name == "get_Model") { il.Emit(OpCodes.Ldc_I4, 1234); il.Emit(OpCodes.Newobj, typeof(Model).GetConstructor(new[] { typeof(int) })!); }
        else if (target.DeclaringType == typeof(InputArgument))
        { il.Emit(OpCodes.Ldc_I8, 0L); il.Emit(OpCodes.Newobj, typeof(InputArgument).GetConstructor(new[] { typeof(ulong) })!); }
        else throw new InvalidOperationException("Unexpected appearance boundary " + target);
        il.Emit(OpCodes.Ret); AppearanceStubs.Add(target, stub); return stub;
    }

    private static int AppearanceReadInt(Hash hash, InputArgument[] args) => hash switch
    {
        Hash.GET_VEHICLE_MOD => InputArgumentValue(args[1]) == 0 ? _appearancePreviewMod : -1,
        Hash.GET_NUM_VEHICLE_MODS or Hash.GET_VEHICLE_LIVERY_COUNT => 20,
        Hash.GET_VEHICLE_WINDOW_TINT or Hash.GET_VEHICLE_LIVERY => -1,
        Hash.GET_VEHICLE_WHEEL_TYPE or Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX => 0,
        _ => throw new InvalidOperationException("Unexpected appearance integer read " + hash)
    };
    private static bool AppearanceReadBool(Hash hash, InputArgument[] args) => hash switch
    {
        Hash.GET_IS_VEHICLE_PRIMARY_COLOUR_CUSTOM or Hash.GET_IS_VEHICLE_SECONDARY_COLOUR_CUSTOM or Hash.GET_VEHICLE_MOD_VARIATION or
        Hash.GET_VEHICLE_NEON_ENABLED or Hash.IS_TOGGLE_MOD_ON or Hash.DOES_EXTRA_EXIST or Hash.IS_VEHICLE_EXTRA_TURNED_ON => false,
        _ => throw new InvalidOperationException("Unexpected appearance boolean read " + hash)
    };
    private static string AppearanceReadString(Hash hash, InputArgument[] args) => hash == Hash.GET_VEHICLE_NUMBER_PLATE_TEXT ? "FRIENDS" :
        throw new InvalidOperationException("Unexpected appearance string read " + hash);
    private static void AppearanceWrite(Hash hash, InputArgument[] args)
    {
        if (hash is Hash.GET_VEHICLE_COLOURS or Hash.GET_VEHICLE_EXTRA_COLOURS or Hash.GET_VEHICLE_NEON_COLOUR or Hash.GET_VEHICLE_TYRE_SMOKE_COLOR)
        {
            for (int index = 1; index < args.Length; index++)
                Marshal.WriteInt32(new IntPtr(unchecked((long)(ulong)typeof(InputArgument).GetField("_data", Instance)!.GetValue(args[index])!)), 0);
            return;
        }
        _appearanceNativeWrites++;
        if (hash == Hash.SET_VEHICLE_MOD && InputArgumentValue(args[1]) == 0) _appearancePreviewMod = InputArgumentValue(args[2]);
    }
}
