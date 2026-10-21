using System;
using System.Reflection;
using RageCoop.Client;

namespace RageCoopAdmin.Client;

// RageCoop reapplies these cached fields on every full remote vehicle update.
// Reset them with the native repair so the next update cannot immediately undo it.
internal static class VehicleRepairCache
{
    private static readonly PropertyInfo? Damage = BindProperty("DamageModel", "VehicleDamageModel");
    private static readonly PropertyInfo? Engine = BindProperty("EngineHealth", "Single");
    private static readonly PropertyInfo? LastEngine = BindProperty("LastEngineHealth", "Single");
    private static readonly PropertyInfo? Flags = BindProperty("Flags", "VehicleDataFlags");
    private static readonly ulong DeadFlag = ReadFlag("IsDead");
    private static readonly ulong FireFlag = ReadFlag("IsOnFire");
    private static readonly ulong RepairedFlag = ReadFlag("Repaired");

    internal static bool IsAvailable => Damage != null && Engine != null && LastEngine != null && Flags?.PropertyType.IsEnum == true &&
        DeadFlag != 0 && FireFlag != 0 && RepairedFlag != 0;

    internal static void Reset(SyncedVehicle vehicle, bool publish)
    {
        if (!IsAvailable) { throw new InvalidOperationException("Unsupported RageCoop repair state contract."); }
        Damage!.SetValue(vehicle, Activator.CreateInstance(Damage.PropertyType));
        Engine!.SetValue(vehicle, 1000f);
        ulong flags = Convert.ToUInt64(Flags!.GetValue(vehicle));
        Flags.SetValue(vehicle, Enum.ToObject(Flags.PropertyType, (flags & ~(DeadFlag | FireFlag)) | RepairedFlag));
        if (publish)
        {
            // SendVehicle rebuilds Flags and detects a repair only from an engine
            // increase. Cosmetic-only repairs also need that bit on the next full send.
            float previous = (float)LastEngine!.GetValue(vehicle)!;
            LastEngine.SetValue(vehicle, float.IsNaN(previous) || previous >= 1000f ? 999f : previous);
            vehicle.SendNextFrame = true;
            vehicle.SendFullNextFrame = true;
        }
    }

    private static PropertyInfo? BindProperty(string name, string expectedType)
    {
        PropertyInfo? property = typeof(SyncedVehicle).GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.PropertyType.Name == expectedType && property.GetSetMethod(true) != null &&
            property.GetGetMethod(true) != null ? property : null;
    }

    private static ulong ReadFlag(string name)
    {
        if (Flags?.PropertyType.IsEnum != true || !Enum.IsDefined(Flags.PropertyType, name)) { return 0; }
        return Convert.ToUInt64(Enum.Parse(Flags.PropertyType, name));
    }
}
