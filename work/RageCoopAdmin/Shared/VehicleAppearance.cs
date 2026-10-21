using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace CoopControl.Shared;

// The same bounded contract is validated before persistence, before a request and on the server.
[XmlRoot("VehicleAppearance")]
public sealed class VehicleAppearance
{
    public int ModelHash { get; set; }
    public int Primary { get; set; }
    public int Secondary { get; set; }
    public int Pearl { get; set; }
    public int RimColor { get; set; }
    public int PrimaryRgb { get; set; } = -1;
    public int SecondaryRgb { get; set; } = -1;
    public int WheelType { get; set; }
    public bool FrontCustomTires { get; set; }
    public bool RearCustomTires { get; set; }
    public int Tint { get; set; } = -1;
    public int PlateStyle { get; set; }
    public string Plate { get; set; } = string.Empty;
    public int Livery { get; set; } = -1;
    public int NeonMask { get; set; }
    public int NeonRgb { get; set; } = 0xFFFFFF;
    public int SmokeRgb { get; set; } = 0xFFFFFF;
    public int ToggleMask { get; set; }
    public int ExtrasPresent { get; set; }
    public int ExtrasEnabled { get; set; }
    [XmlArrayItem("Mod")]
    public int[] Mods { get; set; } = Enumerable.Repeat(-1, 50).ToArray();
}

internal static class VehicleAppearanceCodec
{
    internal const int MaximumXmlCharacters = 12 * 1024;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(VehicleAppearance));

    internal static void Validate(VehicleAppearance value)
    {
        if (value.ModelHash == 0 || value.Primary < 0 || value.Primary > 255 ||
            value.Secondary < 0 || value.Secondary > 255 || value.Pearl < 0 || value.Pearl > 255 ||
            value.RimColor < 0 || value.RimColor > 255 || value.PrimaryRgb < -1 || value.PrimaryRgb > 0xFFFFFF ||
            value.SecondaryRgb < -1 || value.SecondaryRgb > 0xFFFFFF || value.WheelType < 0 || value.WheelType > 13 ||
            value.Tint < -1 || value.Tint > 6 || value.PlateStyle < 0 || value.PlateStyle > 5 ||
            value.Plate == null || value.Plate.Length > 8 || value.Plate.Any(c => c < 32 || c > 126 || c == '~') ||
            value.Livery < -1 || value.Livery > 200 || value.NeonMask < 0 || value.NeonMask > 15 ||
            value.NeonRgb < 0 || value.NeonRgb > 0xFFFFFF || value.SmokeRgb < 0 || value.SmokeRgb > 0xFFFFFF ||
            value.ToggleMask < 0 || value.ToggleMask > 63 || value.ExtrasPresent < 0 || value.ExtrasPresent > 32767 ||
            value.ExtrasEnabled < 0 || (value.ExtrasEnabled & ~value.ExtrasPresent) != 0 ||
            value.Mods == null || value.Mods.Length != 50 || value.Mods.Any(index => index < -1 || index > 200))
        {
            throw new InvalidDataException("The vehicle appearance has invalid values.");
        }
    }

    internal static string Encode(VehicleAppearance value)
    {
        Validate(value);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        Serializer.Serialize(writer, value);
        string xml = writer.ToString();
        if (xml.Length > MaximumXmlCharacters)
        {
            throw new InvalidDataException("The vehicle appearance is too large.");
        }

        return xml;
    }

    internal static VehicleAppearance Decode(string xml)
    {
        if (xml == null || xml.Length > MaximumXmlCharacters)
        {
            throw new InvalidDataException("The vehicle appearance is too large.");
        }

        using var text = new StringReader(xml);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlCharacters
        });
        if (Serializer.Deserialize(reader) is not VehicleAppearance value)
        {
            throw new InvalidDataException("The vehicle appearance format is unsupported.");
        }

        Validate(value);
        return value;
    }
}
