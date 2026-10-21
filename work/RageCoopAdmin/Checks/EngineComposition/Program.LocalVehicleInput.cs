using System;
using System.Reflection;
using System.Runtime.Serialization;
using GTA;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client.Scripting;

internal static partial class Program
{
    private static readonly Ped VehicleInputPed = SpawnNative<Ped>(850001);
    private static readonly Vehicle VehicleInputCar = SpawnNative<Vehicle>(850002);
    private static bool _vehicleInputInCar = true, _vehicleInputCarModel = true, _vehicleInputOnWheels = true;
    private static bool _vehicleInputWater, _vehicleInputPhoneTask, _vehicleInputPhoneCall;
    private static bool _vehicleInputPlayerControl = true;
    private static int _vehicleInputDriver = VehicleInputPed.Handle, _vehicleInputWrites;
    private static int _vehicleInputModelHash = 1234;
    private static float _vehicleInputWrittenSpeed;

    private static void CheckLocalVehicleInput()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        var boundary = new Harmony("friendslegacy.check.localvehicleinput");
        MethodInfo Method(string name) => script.GetMethod(name, Instance | Static)!;
        void Prefix(MethodInfo method, string name) => boundary.Patch(method, prefix: new HarmonyMethod(typeof(Program), name));
        try
        {
            foreach (string name in new[] { "BoostOwnVehicle", "IsPhoneInUse", "ProcessSpeedometer" })
                boundary.Patch(Method(name), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            Prefix(typeof(API).GetProperty(nameof(API.IsOnServer))!.GetGetMethod()!, nameof(InputConnectionState));
            Prefix(Method("IsInputBlocked"), nameof(VehicleInputFalse));
            object instance = FormatterServices.GetUninitializedObject(script);
            FieldInfo settings = script.GetField("_interfaceSettings", Instance)!;
            settings.SetValue(instance, Activator.CreateInstance(settings.FieldType, true));
            void Boost() => Method("BoostOwnVehicle").Invoke(instance, new object[] { 1f / 60f });

            _inputConnected = true;
            _vehicleInputWrites = 0;
            Boost();
            Assert(_vehicleInputWrites == 1 && Math.Abs(_vehicleInputWrittenSpeed - 51f) < 0.0001f,
                "The assembled boost adds the expected speed for the actual driver without a network pool record or SHVDN vehicle memory lookup.");
            _vehicleInputDriver++;
            Boost();
            _vehicleInputDriver = VehicleInputPed.Handle;
            _vehicleInputInCar = false; Boost(); _vehicleInputInCar = true;
            _inputConnected = false; Boost(); _inputConnected = true;
            _vehicleInputCarModel = false; Boost(); _vehicleInputCarModel = true;
            _vehicleInputOnWheels = false; Boost(); _vehicleInputOnWheels = true;
            _vehicleInputWater = true; Boost(); _vehicleInputWater = false;
            _vehicleInputPlayerControl = false; Boost(); _vehicleInputPlayerControl = true;
            _vehicleInputPhoneTask = true; Boost(); _vehicleInputPhoneTask = false;
            _vehicleInputPhoneCall = true; Boost(); _vehicleInputPhoneCall = false;
            Assert(_vehicleInputWrites == 1,
                "A passenger, player on foot, disconnected client, non-car, airborne or submerged vehicle, phone interaction or disabled player control cannot receive a boost speed write.");
            Boost();
            Assert(_vehicleInputWrites == 2, "The next eligible frame resumes acceleration without a cooldown.");

            bool Phone() => (bool)Method("IsPhoneInUse").Invoke(null, new object[] { VehicleInputPed })!;
            Assert(!Phone(), "A closed idle phone leaves the speedometer eligible.");
            foreach (bool call in new[] { false, true })
            {
                _vehicleInputPhoneTask = !call; _vehicleInputPhoneCall = call;
                Assert(Phone(), "Both using the phone and an active call hide the HUD.");
                script.GetField("_speedometerHasSpeed", Instance)!.SetValue(instance, true);
                script.GetField("_speedometerVehicleHandle", Instance)!.SetValue(instance, VehicleInputCar.Handle);
                Method("ProcessSpeedometer").Invoke(instance, new object[] { 1L });
                Assert(!(bool)script.GetField("_speedometerHasSpeed", Instance)!.GetValue(instance)! &&
                    (int)script.GetField("_speedometerVehicleHandle", Instance)!.GetValue(instance)! == 0 &&
                    script.GetField("_speedometerInitialization", Instance)!.GetValue(instance) == null,
                    "The actual HUD Tick hides and resets its visible state before any texture initialization while the phone is in use.");
            }
        }
        finally
        {
            _inputConnected = true;
            _vehicleInputInCar = _vehicleInputCarModel = _vehicleInputOnWheels = true;
            _vehicleInputWater = _vehicleInputPhoneTask = _vehicleInputPhoneCall = false;
            _vehicleInputPlayerControl = true;
            _vehicleInputDriver = VehicleInputPed.Handle;
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static bool VehicleInputFalse(ref bool __result) { __result = false; return false; }

    private static bool VehicleInputNativeBool(Hash hash, InputArgument[] arguments) => hash switch
    {
        Hash.DOES_ENTITY_EXIST => InputArgumentValue(arguments[0]) == VehicleInputCar.Handle,
        Hash.IS_THIS_MODEL_A_CAR => InputArgumentValue(arguments[0]) == _vehicleInputModelHash && _vehicleInputCarModel,
        Hash.IS_PLAYER_CONTROL_ON => _vehicleInputPlayerControl,
        Hash.IS_VEHICLE_ON_ALL_WHEELS => _vehicleInputOnWheels,
        Hash.IS_ENTITY_IN_WATER => _vehicleInputWater,
        Hash.IS_PED_RUNNING_MOBILE_PHONE_TASK => InputArgumentValue(arguments[0]) == VehicleInputPed.Handle && _vehicleInputPhoneTask,
        Hash.IS_MOBILE_PHONE_CALL_ONGOING => _vehicleInputPhoneCall,
        Hash.IS_CUTSCENE_ACTIVE => false,
        _ => throw new InvalidOperationException("Unexpected vehicle input native boolean read " + hash)
    };

    private static int VehicleInputNativeInt(Hash hash, InputArgument[] arguments) => hash switch
    {
        Hash.GET_VEHICLE_PED_IS_IN when InputArgumentValue(arguments[0]) == VehicleInputPed.Handle && InputArgumentValue(arguments[1]) == 0 =>
            _vehicleInputInCar ? VehicleInputCar.Handle : 0,
        Hash.GET_PED_IN_VEHICLE_SEAT when InputArgumentValue(arguments[0]) == VehicleInputCar.Handle &&
            InputArgumentValue(arguments[1]) == (int)VehicleSeat.Driver => _vehicleInputDriver,
        Hash.GET_ENTITY_MODEL when InputArgumentValue(arguments[0]) == VehicleInputCar.Handle => _vehicleInputModelHash,
        _ => throw new InvalidOperationException("Unexpected vehicle input native integer read " + hash)
    };

    private static float VehicleInputNativeFloat(Hash hash, InputArgument[] arguments) =>
        hash == Hash.GET_ENTITY_SPEED && InputArgumentValue(arguments[0]) == VehicleInputCar.Handle ? 50f :
            throw new InvalidOperationException("Unexpected vehicle input native float read " + hash);

    private static void VehicleInputNativeWrite(Hash hash, InputArgument[] arguments)
    {
        Assert(hash == Hash.SET_VEHICLE_FORWARD_SPEED && InputArgumentValue(arguments[0]) == VehicleInputCar.Handle,
            "Boost targets only the local driver's actual vehicle handle.");
        _vehicleInputWrittenSpeed = BitConverter.ToSingle(BitConverter.GetBytes(InputArgumentValue(arguments[1])), 0);
        _vehicleInputWrites++;
    }
}
