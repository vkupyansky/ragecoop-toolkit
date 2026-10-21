using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using GTA;
using HarmonyLib;
using LemonUI;
using Lidgren.Network;
using RageCoop.Client;
using RageCoop.Client.Scripting;
using RageCoop.Core;

internal static partial class Program
{
    private static void CheckBoostReconnect()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        Type host = Engine.GetType("RageCoop.Client.Main", true)!;
        FieldInfo connection = Network.GetField("ServerConnection", Static)!;
        FieldInfo peer = Network.GetField("Peer", Static)!;
        FieldInfo playerId = host.GetField("LocalPlayerID", Static)!;
        object? previousConnection = connection.GetValue(null), previousPeer = peer.GetValue(null);
        object? previousPlayerId = playerId.GetValue(null);
        var logger = (Logger)FormatterServices.GetUninitializedObject(typeof(Logger));
        FieldInfo logBuffer = typeof(Logger).GetField("Buffer", Instance)!;
        logBuffer.SetValue(logger, ""); logger.LogLevel = 1; logger.Name = "isolated-boost";
        IDictionary ids = (IDictionary)Pool.GetField("VehiclesByID", Static)!.GetValue(null)!;
        IDictionary handles = (IDictionary)Pool.GetField("VehiclesByHandle", Static)!.GetValue(null)!;
        var boundary = new Harmony("friendslegacy.check.boostreconnect");
        MethodInfo Method(string name) => script.GetMethod(name, Instance | Static)!;
        long frame = Stopwatch.GetTimestamp();
        void Frame(object instance)
        {
            frame += Math.Max(1, Stopwatch.Frequency / 60);
            Method("ProcessQuickControls").Invoke(instance, new object[] { frame });
        }
        object InstanceForConnection()
        {
            object value = FormatterServices.GetUninitializedObject(script);
            foreach (FieldInfo field in script.GetFields(Instance).Where(field => field.Name is
                "_shortcutLatch" or "_gamepadChord" or "_cancelReleaseGuard" or "_shortcutMenuInputGuard" or
                "_cancelMenuInputGuard" or "_interfaceSettings" or "_suspendedMenuInputs" or "_textInputActivationGuard"))
                field.SetValue(value, Activator.CreateInstance(field.FieldType, true));
            object settings = script.GetField("_interfaceSettings", Instance)!.GetValue(value)!;
            settings.GetType().GetProperty("GamepadShortcuts")!.SetValue(settings, false);
            var menus = (ObjectPool)FormatterServices.GetUninitializedObject(typeof(ObjectPool));
            typeof(ObjectPool).GetFields(Instance).Single(field => field.FieldType == typeof(List<IProcessable>))
                .SetValue(menus, new List<IProcessable>());
            script.GetField("_pool", Instance)!.SetValue(value, menus);
            var resource = new ClientResource();
            typeof(ClientResource).GetProperty(nameof(ClientResource.Logger))!.SetValue(resource, logger);
            typeof(ClientScript).GetProperty(nameof(ClientScript.CurrentResource))!.SetValue(value, resource);
            return value;
        }
        NetConnection Connect(int localId)
        {
            // No sockets or listener threads are needed for the actual API
            // getter: it reads only this connection's visible status.
            var client = (NetPeer)FormatterServices.GetUninitializedObject(peer.FieldType);
            var current = (NetConnection)FormatterServices.GetUninitializedObject(typeof(NetConnection));
            typeof(NetConnection).GetField("m_visibleStatus", Instance)!.SetValue(current, NetConnectionStatus.Connected);
            typeof(NetConnection).GetField("m_peer", Instance)!.SetValue(current, client);
            peer.SetValue(null, client);
            connection.SetValue(null, current);
            playerId.SetValue(null, localId);
            Assert(API.IsOnServer, "The real API reads the current connected Lidgren connection without a connection-state stub.");
            return current;
        }
        try
        {
            Assert(ids.Count == 0 && handles.Count == 0, "Reconnect starts with isolated empty real vehicle tables.");
            // Keep the actual installed model classification getter, including
            // its native hash and argument conversion. Only GTA is isolated.
            boundary.Patch(typeof(Model).GetProperty(nameof(Model.IsCar))!.GetGetMethod()!,
                transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateBoostReconnectBoundaries)));
            foreach (string name in new[] { "IsPhoneInUse", "BoostOwnVehicle", "IsInputBlocked", "SuppressMenuGameActions", "ProcessQuickControls" })
                boundary.Patch(Method(name), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateBoostReconnectBoundaries)));
            _inputChat = _inputHostMenu = _inputPause = false;
            _vehicleInputInCar = _vehicleInputCarModel = _vehicleInputOnWheels = _vehicleInputPlayerControl = true;
            _vehicleInputWater = _vehicleInputPhoneTask = _vehicleInputPhoneCall = false;
            _vehicleInputDriver = VehicleInputPed.Handle;
            _vehicleInputModelHash = unchecked((int)GTA.StringHash.AtStringHash("bmwm3f80"));
            _vehicleInputWrites = 0;
            InputKeys.Clear(); InputKeys.Add(Keys.O);
            NetConnection first = Connect(851001);
            object before = InstanceForConnection();
            Frame(before); Frame(before);
            Assert(_vehicleInputWrites == 1 && _vehicleInputWrittenSpeed > 50f,
                "Held O reaches the real speed native for an add-on model through the complete assembled quick-control path.");

            var record = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
            typeof(SyncedVehicle).GetProperty("MainVehicle")!.SetValue(record, VehicleInputCar);
            typeof(SyncedEntity).GetField("_ownerID", Instance)!.SetValue(record, 851001);
            ids.Add(851002, record); handles.Add(VehicleInputCar.Handle, record);
            typeof(NetConnection).GetField("m_visibleStatus", Instance)!.SetValue(first, NetConnectionStatus.Disconnected);
            Assert(!API.IsOnServer, "The real API observes the disconnected prior connection.");
            Frame(before);
            Assert(_vehicleInputWrites == 1, "The prior connection cannot dispatch a boost after disconnect.");
            Pool.GetMethod("Cleanup", Static)!.Invoke(null, new object[] { true, true });
            Assert(ids.Count == 0 && handles.Count == 0 && _vehicleInputInCar,
                "The real pool cleanup clears both records while leaving the local driver's GTA vehicle in place.");
            NetConnection second = Connect(851101);
            Assert(!ReferenceEquals(first, second) && !ReferenceEquals(first.Peer, second.Peer),
                "Reconnect replaces both the session connection and peer.");
            object after = InstanceForConnection();
            Frame(after); Frame(after);
            Assert(_vehicleInputWrites == 2 && _vehicleInputWrittenSpeed > 50f,
                "A fresh resource still accelerates the existing driver vehicle after reconnect before its new pool record exists.");
            _vehicleInputInCar = false; Frame(after); _vehicleInputInCar = true; Frame(after);
            Assert(_vehicleInputWrites == 3,
                "Leaving and re-entering the same car after reconnect does not disable held O dispatch.");
            _vehicleInputPhoneTask = true; Frame(after); _vehicleInputPhoneTask = false;
            _vehicleInputPlayerControl = false; Frame(after); _vehicleInputPlayerControl = true;
            _inputChat = true; Frame(after); _inputChat = false;
            _inputHostMenu = true; Frame(after); _inputHostMenu = false;
            _inputPause = true; Frame(after); _inputPause = false;
            script.GetField("_gettingUserInput", Instance)!.SetValue(after, true); Frame(after);
            script.GetField("_gettingUserInput", Instance)!.SetValue(after, false);
            Assert(_vehicleInputWrites == 3,
                "Phone, disabled gameplay controls, chat, host menu, pause and text input block the complete boost flow after reconnect.");
            Frame(after);
            Assert(_vehicleInputWrites == 4, "The next gameplay frame resumes acceleration without a new key-down or cooldown.");
            logBuffer.SetValue(logger, "");
            script.GetField("_boostDiagnosticLastLog", Instance)!.SetValue(after, 0L);
            script.GetField("_boostDiagnosticReasons", Instance)!.SetValue(after, null);
            void Trace(long tick, string reason, float speed, float target) => Method("TraceKeyboardBoost").Invoke(after,
                new object?[] { tick, reason, VehicleInputCar.Handle, _vehicleInputModelHash, speed, target });
            long start = Math.Max(1, Stopwatch.GetTimestamp());
            Trace(start, "applied", 50f, 51f);
            for (int index = 1; index < 60; index++)
                Trace(start + Stopwatch.Frequency * index / 60, index % 2 == 0 ? "applied" : "not-on-all-wheels", 0f, 1f);
            string firstLog = (string)logBuffer.GetValue(logger)!;
            Assert(firstLog.Split(new[] { "held O state=" }, StringSplitOptions.None).Length == 2,
                "Frequent eligibility transitions produce at most one O diagnostic in a second.");
            Trace(start + Stopwatch.Frequency, "applied", 0f, 4f);
            string completeLog = (string)logBuffer.GetValue(logger)!;
            Assert(completeLog.Split(new[] { "held O state=" }, StringSplitOptions.None).Length == 3 &&
                completeLog.Contains("observed=applied,not-on-all-wheels") && completeLog.Contains("speed_mps=0; target_mps=4") &&
                completeLog.Contains("ownership=missing") && completeLog.Contains("model=" + _vehicleInputModelHash.ToString("X8")),
                "The next bounded diagnostic retains brief wheel-contact failures, speed, target, add-on model and missing ownership record.");
            Console.WriteLine("Actual resource boost/reconnect dispatch passed with new real Lidgren session and peer, real pool cleanup and add-on model classification; GTA physics and a live server were not exercised.");
        }
        finally
        {
            ids.Remove(851002); handles.Remove(VehicleInputCar.Handle);
            connection.SetValue(null, previousConnection); peer.SetValue(null, previousPeer); playerId.SetValue(null, previousPlayerId);
            InputKeys.Clear(); _inputChat = _inputHostMenu = _inputPause = false;
            _vehicleInputInCar = _vehicleInputCarModel = _vehicleInputOnWheels = _vehicleInputPlayerControl = true;
            _vehicleInputWater = _vehicleInputPhoneTask = _vehicleInputPhoneCall = false;
            _vehicleInputModelHash = 1234;
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static IEnumerable<CodeInstruction> IsolateBoostReconnectBoundaries(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction code in original)
        {
            if (code.operand is MethodInfo method &&
                ((method.DeclaringType == typeof(API) && method.Name == "get_IsOnServer") ||
                    (method.DeclaringType == typeof(Model) && method.Name == "get_IsCar")))
            {
                yield return code;
                continue;
            }
            foreach (CodeInstruction isolated in IsolateInputBoundaries(new[] { code })) yield return isolated;
        }
    }
}
