using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using GTA;
using GTA.Math;
using GTA.Native;
using HarmonyLib;
using RageCoop.Client;
using RageCoopAdmin.Client;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string PatchId = "friendslegacy.ragecoop.enginemotion";
    private static readonly List<MethodInfo> NativeTargets = new List<MethodInfo>();
    private static Vector3 _position, _velocity;
    private static int _velocityWrites, _positionWrites, _quaternionWrites, _angularWrites;
    private static float _travelTime;
    private static float _frameSeconds;
    private static bool _dead, _ragdoll;

    private static int Main()
    {
        var natives = new Harmony("friendslegacy.check.enginemotion");
        try
        {
            CheckInstalledContract();
            InstallNativeBoundary(natives);
            CheckOriginalDefects();
            Assert(EngineMotionGuard.Install() && EngineMotionGuard.Install(), "The exact installed motion patch installs once.");
            CheckVehicleVelocity();
            CheckCorrectionStability();
            CheckPedCorrection();
            CheckAirbornePrediction();
            EngineMotionGuard.Uninstall();
            Assert(new[] { EngineMotionGuard.FindVehicleTarget()!, EngineMotionGuard.FindPedTarget()!, EngineMotionGuard.FindSmoothTarget()! }
                .All(target => Harmony.GetPatchInfo(target)?.Owners.Contains(PatchId) != true), "Unloading removes all three owned motion patches.");
            CheckOriginalDefects();
            Console.WriteLine("Actual installed motion methods passed; GTA boundary effects were simulated, live driving was not tested.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            EngineMotionGuard.Uninstall();
            foreach (MethodInfo target in NativeTargets) { natives.Unpatch(target, HarmonyPatchType.All, natives.Id); }
        }
    }

    private static void CheckInstalledContract()
    {
        MethodInfo vehicle = EngineMotionGuard.FindVehicleTarget()!, ped = EngineMotionGuard.FindPedTarget()!;
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(vehicle);
        Assert(EngineMotionGuard.TryFindVelocityGate(original, vehicle, out _, out _, out _, out _), "The exact installed deadband block and typed locals match.");
        CodeInstruction[] patched = EngineMotionGuard.ApplyVehicleVelocity(original, vehicle).ToArray();
        Assert(patched.Count(code => code.operand is MethodInfo method && method.Name == "set_Position") == 1 &&
            patched.Count(code => code.opcode == OpCodes.Ldc_R4 && code.operand is float value && value == 10f) == 2 &&
            patched.Count(code => code.operand is MethodInfo method && method.Name == "set_LocalRotationVelocity") == 2,
            "The teleport threshold, original gains and both angular branches survive unchanged.");
        List<CodeInstruction> airborne = PatchProcessor.GetOriginalInstructions(ped);
        Assert(EngineMotionGuard.FindAirborneTargets(airborne).Length == 2, "Exactly two airborne position setters use the erroneous one-second target.");
        CodeInstruction[] predicted = EngineMotionGuard.PredictAirbornePosition(airborne).ToArray();
        Assert(predicted.Length == airborne.Count && predicted.Count(code => code.operand is MethodInfo method && method.Name == "Predict") == 2 &&
            predicted.Count(code => code.operand is MethodInfo method && method.Name == "set_PositionNoOffset") == 2,
            "Only the two target expressions change; original position smoothing remains.");
        MethodInfo smooth = EngineMotionGuard.FindSmoothTarget()!;
        List<CodeInstruction> pedestrian = PatchProcessor.GetOriginalInstructions(smooth);
        Assert(EngineMotionGuard.TryFindPedCorrection(pedestrian, smooth, out _, out _),
            "The installed pedestrian correction uses the supported expression and typed locals.");
        CodeInstruction[] limited = EngineMotionGuard.LimitPedCorrection(pedestrian, smooth).ToArray();
        Assert(limited.Length == pedestrian.Count + 3 && limited.Count(code => code.operand is MethodInfo method && method.Name == "IsOff") == 1 &&
            limited.Count(code => code.operand is MethodInfo method && method.Name == "set_Velocity") == 1,
            "Only the pedestrian correction is limited; the original teleport, heading and ragdoll branches remain.");
        bool rejected = false;
        try { EngineMotionGuard.ApplyVehicleVelocity(new[] { new CodeInstruction(OpCodes.Ret) }, vehicle).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unknown vehicle IL is rejected.");
        rejected = false;
        try { EngineMotionGuard.PredictAirbornePosition(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unknown airborne IL is rejected.");
        rejected = false;
        try { EngineMotionGuard.LimitPedCorrection(new[] { new CodeInstruction(OpCodes.Ret) }, smooth).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Unknown pedestrian IL is rejected.");
    }

    private static void CheckOriginalDefects()
    {
        Reset();
        _velocity = new Vector3(30f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, Vector3.Zero));
        Assert(_velocityWrites == 0 && _velocity.X == 30f, "The original engine demonstrably ignores a stop at an already matched position.");
        Reset();
        _travelTime = 0.04f;
        DisplayPed(PedRecord(false, new Vector3(0f, 0f, -50f)));
        Assert(Near(_position.Z, -25f), "The original freefall method moves halfway toward one full second of velocity.");
        Reset();
        _frameSeconds = 1f / 30f;
        _position = new Vector3(-9f, 0f, 0f);
        SmoothPed(PedRecord(false, Vector3.Zero));
        Assert(Near(_velocity.X, 405f) && Near(_velocity.X * _frameSeconds, 13.5f) && _positionWrites == 0,
            "The actual original non-ragdoll method crosses a nine-meter target by 4.5 meters at 30 FPS before IsOff can teleport.");
    }

    private static void CheckVehicleVelocity()
    {
        foreach (float error in new[] { 0f, 0.02f, 0.03f })
        {
            Reset();
            _position = new Vector3(-error, 0f, 0f);
            _velocity = new Vector3(30f, 0f, 0f);
            DisplayVehicle(VehicleRecord(Vector3.Zero, Vector3.Zero));
            Assert(_velocityWrites == 1 && _velocity == Vector3.Zero && _positionWrites == 0,
                "A matching/subthreshold stop applies authoritative speed without adding positional correction.");
        }
        Reset();
        Vector3 speed = new Vector3(12f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, speed));
        Assert(_velocityWrites == 1 && _velocity == speed, "A new nonzero speed applies even at zero position error.");
        Reset();
        _position = new Vector3(-2f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, speed));
        Assert(_velocity == new Vector3(16f, 0f, 0f), "Ordinary moving correction retains the original squared-distance rule.");
        Reset();
        _position = new Vector3(-0.5f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, Vector3.Zero));
        Assert(_velocity == new Vector3(2.5f, 0f, 0f), "Safe stationary correction retains its original tenfold gain.");
        Reset();
        _position = new Vector3(-11f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, speed));
        Assert(_position == Vector3.Zero && _velocity == speed && _positionWrites == 1 && _quaternionWrites == 1 && _angularWrites == 0,
            "The existing over-ten-meter teleport still restores position, speed and rotation then returns.");
    }

    private static void CheckAirbornePrediction()
    {
        var age = Stopwatch.StartNew();
        Thread.Sleep(20);
        age.Stop();
        foreach (bool parachute in new[] { false, true })
        {
            foreach (float latency in new[] { 0f, 0.04f, 0.2f })
            {
                Reset();
                _travelTime = latency;
                Vector3 velocity = new Vector3(10f, 0f, -50f);
                SyncedPed ped = PedRecord(parachute, velocity);
                DisplayPed(ped);
                Assert(Near(_position.X, velocity.X * latency * 0.5f) && Near(_position.Z, velocity.Z * latency * 0.5f) &&
                    _positionWrites == 1 && _quaternionWrites == 1,
                    "Both airborne modes use actual packet travel time and preserve original half-step smoothing and rotation.");
            }
            Reset();
            SyncedPed aged = PedRecord(parachute, new Vector3(0f, 0f, -50f));
            aged.LastSyncedStopWatch = age;
            DisplayPed(aged);
            Assert(Near(_position.Z, -25f * 0.001f * age.ElapsedMilliseconds),
                "Both modes also retain the installed predictor's actual snapshot age.");
        }
    }

    private static void CheckCorrectionStability()
    {
        foreach (int fps in new[] { 30, 60, 144 })
        {
            foreach (float distance in new[] { 0.04f, 0.5f, 2f, 4f, 6f, 9f, 10f })
            {
                Reset();
                _frameSeconds = 1f / fps;
                _position = new Vector3(-distance, 0f, 0f);
                Vector3 original = new Vector3(distance * distance * 10f, 0f, 0f);
                DisplayVehicle(VehicleRecord(Vector3.Zero, Vector3.Zero));
                Assert(_velocity.X * _frameSeconds <= distance + 0.00001f && _velocity.X >= 0f && _positionWrites == 0,
                    "The real vehicle method cannot overshoot its predicted target through positional correction at 30/60/144 FPS.");
                if (original.X * _frameSeconds <= distance)
                {
                    Assert(_velocity == original, "The safe region preserves the original correction exactly.");
                }
            }
            Reset();
            _frameSeconds = 1f / fps;
            _position = new Vector3(-9f, 0f, 0f);
            SyncedVehicle stationary = VehicleRecord(Vector3.Zero, Vector3.Zero);
            float previous = 9f;
            for (int frame = 0; frame < 12; frame++)
            {
                DisplayVehicle(stationary);
                _position += _velocity * _frameSeconds;
                float remaining = -_position.X;
                Assert(remaining >= -0.00001f && remaining <= previous + 0.00001f,
                    "Repeated actual motion corrections approach a stopped target without crossing it or triggering a teleport.");
                previous = remaining;
            }
            Assert(_positionWrites == 0, "Stable recovery at all tested frame rates never enters the teleport path.");
        }
        Reset();
        _frameSeconds = 1f / 30f;
        _position = new Vector3(-9f, 0f, 0f);
        DisplayVehicle(VehicleRecord(Vector3.Zero, Vector3.Zero));
        Assert(Near(_velocity.X, 270f), "The original 810 m/s correction at nine meters is capped to one nine-meter relative step at 30 FPS.");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Reset();
            _frameSeconds = invalid;
            _position = new Vector3(-2f, 0f, 0f);
            Vector3 velocity = new Vector3(12f, 0f, 0f);
            DisplayVehicle(VehicleRecord(Vector3.Zero, velocity));
            Assert(_velocity == velocity, "No usable frame duration applies authoritative speed without an invented correction interval.");
        }
        Vector3 diagonal = new Vector3(30f, 40f, 0f);
        Vector3 limited = MotionCorrectionPolicy.Limit(diagonal, 1f, 0.1f);
        Assert(Near(limited.X, 6f) && Near(limited.Y, 8f), "The limiter preserves the full vector direction.");
    }

    private static void CheckPedCorrection()
    {
        foreach (int fps in new[] { 30, 60, 144 })
        {
            foreach (float distance in new[] { 0f, 0.02f, 0.5f, 2f, 4f, 6f, 9f, 15f, 30f })
            {
                Reset();
                _frameSeconds = 1f / fps;
                _position = new Vector3(-distance, 0f, 0f);
                Vector3 ownerVelocity = new Vector3(12f, 0f, 0f);
                SmoothPed(PedRecord(false, ownerVelocity));
                float correction = _velocity.X - ownerVelocity.X;
                Assert(correction * _frameSeconds <= distance + 0.00001f && correction >= 0f && _positionWrites == 0,
                    "The real living pedestrian correction never crosses its predicted target at 30/60/144 FPS.");
                if (5f * distance * _frameSeconds <= 1f)
                {
                    Assert(Near(correction, 5f * distance * distance), "Safe pedestrian correction preserves its original gain and owner velocity.");
                }
            }
            Reset();
            _frameSeconds = 1f / fps;
            _position = new Vector3(-9f, 0f, 0f);
            SyncedPed ped = PedRecord(false, Vector3.Zero);
            float previous = 9f;
            for (int frame = 0; frame < 12; frame++)
            {
                SmoothPed(ped);
                _position += _velocity * _frameSeconds;
                float remaining = -_position.X;
                Assert(remaining >= -0.00001f && remaining <= previous + 0.00001f,
                    "Repeated actual pedestrian steps approach the stopped target without crossing it.");
                previous = remaining;
            }
            Assert(_positionWrites == 0, "Stable pedestrian recovery preserves the normal non-teleport path.");
        }
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Reset();
            _frameSeconds = invalid;
            _position = new Vector3(-9f, 0f, 0f);
            Vector3 ownerVelocity = new Vector3(12f, 0f, 0f);
            SmoothPed(PedRecord(false, ownerVelocity));
            Assert(_velocity == ownerVelocity, "An unusable frame duration retains pedestrian owner velocity without invented correction time.");
        }
        Reset();
        _position = new Vector3(-33f, 0f, 0f);
        SmoothPed(PedRecord(false, Vector3.Zero));
        Assert(_position == Vector3.Zero && _positionWrites == 1 && _velocityWrites == 0,
            "The original accumulated IsOff teleport still handles the first 33-meter discrepancy.");
    }

    private static void InstallNativeBoundary(Harmony harmony)
    {
        void Patch(MethodInfo target, string prefix)
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program), prefix));
            NativeTargets.Add(target);
        }
        // Memory's static initializer scans the running game. Native angular
        // setters also require its matching host module. Replace their game
        // boundaries at call sites, retaining all real motion calculations.
        foreach (MethodInfo target in new[]
        {
            EngineMotionGuard.FindVehicleTarget()!, EngineMotionGuard.FindPedTarget()!, EngineMotionGuard.FindSmoothTarget()!,
            typeof(SyncedVehicle).GetMethod("GetCalibrationRotation", Instance)!,
            typeof(EngineMotionGuard).GetMethod("WriteVehicleVelocity", Static)!
        })
        {
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(Program), nameof(SubstituteNativeBoundary)) { priority = Priority.Last });
            NativeTargets.Add(target);
        }
        Patch(typeof(RageCoop.Client.Player).GetProperty("PacketTravelTime")!.GetGetMethod()!, nameof(ReadTravelTime));
    }

    private static SyncedVehicle VehicleRecord(Vector3 position, Vector3 velocity)
    {
        var record = (SyncedVehicle)FormatterServices.GetUninitializedObject(typeof(SyncedVehicle));
        EntityRecord(record, position, velocity);
        typeof(SyncedVehicle).GetProperty("MainVehicle", Instance)!.SetValue(record, NativeEntity<Vehicle>(950001));
        return record;
    }

    private static SyncedPed PedRecord(bool parachute, Vector3 velocity)
    {
        var record = (SyncedPed)FormatterServices.GetUninitializedObject(typeof(SyncedPed));
        EntityRecord(record, Vector3.Zero, velocity);
        typeof(SyncedPed).GetProperty("MainPed", Instance)!.SetValue(record, NativeEntity<Ped>(950002));
        FieldInfo flags = typeof(SyncedPed).GetField("Flags", Instance)!;
        flags.SetValue(record, Enum.Parse(flags.FieldType, parachute ? "IsParachuteOpen" : "IsInParachuteFreeFall"));
        if (parachute) { typeof(SyncedPed).GetProperty("ParachuteProp", Instance)!.SetValue(record, NativeEntity<Prop>(950003)); }
        return record;
    }

    private static void EntityRecord(SyncedEntity record, Vector3 position, Vector3 velocity)
    {
        typeof(SyncedEntity).GetProperty("Position", Instance)!.SetValue(record, position);
        typeof(SyncedEntity).GetProperty("Velocity", Instance)!.SetValue(record, velocity);
        typeof(SyncedEntity).GetProperty("Quaternion", Instance)!.SetValue(record, Quaternion.Identity);
        typeof(SyncedEntity).GetField("<Owner>k__BackingField", Instance)!.SetValue(record,
            FormatterServices.GetUninitializedObject(typeof(RageCoop.Client.Player)));
        // A stopped zero-age watch keeps timing deterministic; live code uses its
        // original restarted Stopwatch and the actual owner's travel-time getter.
        record.LastSyncedStopWatch = new Stopwatch();
    }

    private static T NativeEntity<T>(int handle) where T : Entity => (T)Activator.CreateInstance(typeof(T), Instance, null, new object[] { handle }, null)!;
    private static void DisplayVehicle(SyncedVehicle record) => EngineMotionGuard.FindVehicleTarget()!.Invoke(record, null);
    private static void SmoothPed(SyncedPed record) => EngineMotionGuard.FindSmoothTarget()!.Invoke(record, null);
    private static void DisplayPed(SyncedPed record) => EngineMotionGuard.FindPedTarget()!.Invoke(record, null);
    private static IEnumerable<CodeInstruction> SubstituteNativeBoundary(IEnumerable<CodeInstruction> original)
    {
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method)
            {
                string? replacement = null;
                if (method.DeclaringType?.FullName == "RageCoop.Client.Memory" && (method.Name == "ReadPosition" || method.Name == "ReadQuaternion"))
                {
                    replacement = method.Name;
                }
                else if (method.DeclaringType == typeof(Entity))
                {
                    replacement = method.Name switch
                    {
                        "set_Velocity" => nameof(WriteVelocity),
                        "set_Position" or "set_PositionNoOffset" => nameof(WritePosition),
                        "set_Quaternion" => nameof(WriteQuaternion),
                        "set_LocalRotationVelocity" => nameof(WriteAngular),
                        "get_IsDead" => nameof(ReadDead),
                        "get_Heading" => nameof(ReadHeading),
                        "set_Heading" => nameof(WriteHeading),
                        _ => null
                    };
                }
                else if (method.DeclaringType == typeof(Ped) && (method.Name == "get_IsRagdoll" || method.Name == "get_IsGettingUp"))
                {
                    replacement = method.Name == "get_IsRagdoll" ? nameof(ReadRagdoll) : nameof(ReadGettingUp);
                }
                else if (method.DeclaringType == typeof(Game) && method.Name == "get_LastFrameTime") { replacement = nameof(ReadFrameSeconds); }
                else if (method.DeclaringType == typeof(InputArgument) && method.Name == "op_Implicit" &&
                    method.GetParameters().Single().ParameterType == typeof(string)) { replacement = nameof(NativeText); }
                else if (method.DeclaringType == typeof(Function) && method.Name == "Call" && method.IsGenericMethod &&
                    method.GetGenericArguments().Single() == typeof(bool) && method.GetParameters().Length == 5) { replacement = nameof(AnimationPlaying); }
                if (replacement != null)
                {
                    code.opcode = OpCodes.Call;
                    code.operand = typeof(Program).GetMethod(replacement, Static)!;
                }
            }
            yield return code;
        }
    }
    private static bool ReadDead(Entity entity) => _dead;
    private static bool ReadRagdoll(Ped ped) => _ragdoll;
    private static bool ReadGettingUp(Ped ped) => false;
    private static float ReadHeading(Entity entity) => 0f;
    private static void WriteHeading(Entity entity, float value) { }
    private static Vector3 ReadPosition(Entity entity) => _position;
    private static Quaternion ReadQuaternion(Entity entity) => Quaternion.Identity;
    private static void WriteVelocity(Entity entity, Vector3 value) { _velocity = value; _velocityWrites++; }
    private static void WritePosition(Entity entity, Vector3 value) { _position = value; _positionWrites++; }
    private static void WriteQuaternion(Entity entity, Quaternion value) { _quaternionWrites++; }
    private static void WriteAngular(Entity entity, Vector3 value) { _angularWrites++; }
    private static bool ReadTravelTime(ref float __result) { __result = _travelTime; return false; }
    private static float ReadFrameSeconds() => _frameSeconds;
    private static InputArgument NativeText(string value) => null!;
    private static bool AnimationPlaying(Hash hash, InputArgument one, InputArgument two, InputArgument three, InputArgument four) => true;
    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.0001f;
    private static void Reset()
    {
        _position = _velocity = Vector3.Zero;
        _velocityWrites = _positionWrites = _quaternionWrites = _angularWrites = 0;
        _travelTime = 0f;
        _dead = _ragdoll = false;
        _frameSeconds = 1f / 60f;
    }
    private static void Assert(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
