using CoopControl.Shared;
using GTA;
using GTA.Native;

namespace RageCoopAdmin.Client;

internal static class NativeVehicleAppearance
{
    internal static VehicleAppearance Capture(Vehicle vehicle)
    {
        var value = new VehicleAppearance { ModelHash = vehicle.Model.Hash };
        using var first = new OutputArgument();
        using var second = new OutputArgument();
        Function.Call(Hash.GET_VEHICLE_COLOURS, vehicle.Handle, first, second);
        value.Primary = first.GetResult<int>();
        value.Secondary = second.GetResult<int>();
        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, vehicle.Handle, first, second);
        value.Pearl = first.GetResult<int>();
        value.RimColor = second.GetResult<int>();
        if (Function.Call<bool>(Hash.GET_IS_VEHICLE_PRIMARY_COLOUR_CUSTOM, vehicle.Handle))
        {
            value.PrimaryRgb = ReadRgb(Hash.GET_VEHICLE_CUSTOM_PRIMARY_COLOUR, vehicle);
        }

        if (Function.Call<bool>(Hash.GET_IS_VEHICLE_SECONDARY_COLOUR_CUSTOM, vehicle.Handle))
        {
            value.SecondaryRgb = ReadRgb(Hash.GET_VEHICLE_CUSTOM_SECONDARY_COLOUR, vehicle);
        }

        value.WheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, vehicle.Handle);
        value.FrontCustomTires = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, vehicle.Handle, 23);
        value.RearCustomTires = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, vehicle.Handle, 24);
        value.Tint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, vehicle.Handle);
        value.Plate = Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, vehicle.Handle).Trim();
        value.PlateStyle = Function.Call<int>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, vehicle.Handle);
        value.Livery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, vehicle.Handle);
        value.NeonRgb = ReadRgb(Hash.GET_VEHICLE_NEON_COLOUR, vehicle);
        value.SmokeRgb = ReadRgb(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, vehicle);
        for (int side = 0; side < 4; side++)
        {
            if (Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, vehicle.Handle, side))
            {
                value.NeonMask |= 1 << side;
            }
        }

        for (int mod = 0; mod < 50; mod++)
        {
            value.Mods[mod] = Function.Call<int>(Hash.GET_VEHICLE_MOD, vehicle.Handle, mod);
        }

        for (int toggle = 17; toggle <= 22; toggle++)
        {
            if (Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, vehicle.Handle, toggle))
            {
                value.ToggleMask |= 1 << (toggle - 17);
            }
        }

        for (int extra = 0; extra < 15; extra++)
        {
            if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, vehicle.Handle, extra))
            {
                value.ExtrasPresent |= 1 << extra;
                if (Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, vehicle.Handle, extra))
                {
                    value.ExtrasEnabled |= 1 << extra;
                }
            }
        }

        VehicleAppearanceCodec.Validate(value);
        return value;
    }

    internal static void Apply(Vehicle vehicle, VehicleAppearance value)
    {
        if (vehicle.Model.Hash != value.ModelHash)
        {
            return;
        }

        Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle.Handle, 0);
        Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, vehicle.Handle, value.WheelType);
        for (int mod = 0; mod < value.Mods.Length; mod++)
        {
            if (mod >= 17 && mod <= 22)
            {
                continue;
            }

            int index = value.Mods[mod];
            int available = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, vehicle.Handle, mod);
            if (index < available)
            {
                Function.Call(Hash.SET_VEHICLE_MOD, vehicle.Handle, mod, index,
                    (mod == 23 && value.FrontCustomTires) || (mod == 24 && value.RearCustomTires));
            }
        }

        for (int toggle = 17; toggle <= 22; toggle++)
        {
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, vehicle.Handle, toggle, (value.ToggleMask & (1 << (toggle - 17))) != 0);
        }

        Function.Call(Hash.CLEAR_VEHICLE_CUSTOM_PRIMARY_COLOUR, vehicle.Handle);
        Function.Call(Hash.CLEAR_VEHICLE_CUSTOM_SECONDARY_COLOUR, vehicle.Handle);
        Function.Call(Hash.SET_VEHICLE_COLOURS, vehicle.Handle, value.Primary, value.Secondary);
        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, vehicle.Handle, value.Pearl, value.RimColor);
        if (value.PrimaryRgb >= 0)
        {
            ApplyRgb(Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR, vehicle, value.PrimaryRgb);
        }

        if (value.SecondaryRgb >= 0)
        {
            ApplyRgb(Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR, vehicle, value.SecondaryRgb);
        }

        Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, vehicle.Handle, value.Plate);
        Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, vehicle.Handle, value.PlateStyle);
        Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, vehicle.Handle, value.Tint);
        if (value.Livery < Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, vehicle.Handle))
        {
            Function.Call(Hash.SET_VEHICLE_LIVERY, vehicle.Handle, value.Livery);
        }

        for (int side = 0; side < 4; side++)
        {
            Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, vehicle.Handle, side, (value.NeonMask & (1 << side)) != 0);
        }

        ApplyRgb(Hash.SET_VEHICLE_NEON_COLOUR, vehicle, value.NeonRgb);
        ApplyRgb(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, vehicle, value.SmokeRgb);
        for (int extra = 0; extra < 15; extra++)
        {
            if ((value.ExtrasPresent & (1 << extra)) != 0 && Function.Call<bool>(Hash.DOES_EXTRA_EXIST, vehicle.Handle, extra))
            {
                Function.Call(Hash.SET_VEHICLE_EXTRA, vehicle.Handle, extra, (value.ExtrasEnabled & (1 << extra)) == 0);
            }
        }
    }

    private static int ReadRgb(Hash native, Vehicle vehicle)
    {
        using var red = new OutputArgument();
        using var green = new OutputArgument();
        using var blue = new OutputArgument();
        Function.Call(native, vehicle.Handle, red, green, blue);
        return (red.GetResult<int>() << 16) | (green.GetResult<int>() << 8) | blue.GetResult<int>();
    }

    private static void ApplyRgb(Hash native, Vehicle vehicle, int rgb) =>
        Function.Call(native, vehicle.Handle, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
}
