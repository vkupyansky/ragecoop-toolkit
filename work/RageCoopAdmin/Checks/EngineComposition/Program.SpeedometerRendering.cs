using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

internal static partial class Program
{
    private static readonly Dictionary<(int texture, int index), HudDraw> HudInstances = new Dictionary<(int, int), HudDraw>();
    private static int _hudClock;

    private static void CheckSpeedometerRendering()
    {
        Type assets = Resource.GetType("RageCoopAdmin.Client.SpeedometerAssets", true)!;
        Type menu = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        MethodInfo drawDigits = menu.GetMethod("DrawSpeedometerDigits", Static)!;
        var boundary = new Harmony("friendslegacy.check.speedometerrendering");
        object renderer = FormatterServices.GetUninitializedObject(assets);
        foreach (string name in new[] { "_visible", "_drawing" })
        {
            FieldInfo field = assets.GetField(name, Instance)!;
            field.SetValue(renderer, Activator.CreateInstance(field.FieldType));
        }
        assets.GetField("_digits", Instance)!.SetValue(renderer, Enumerable.Range(0, 10).ToArray());
        void Call(string method, params object[] arguments) => assets.GetMethod(method, Instance)!.Invoke(renderer, arguments);
        void Digits(string value, int leading = 0)
        {
            _hudClock += 16;
            Call("BeginFrame", 1280f, 16f / 9f);
            drawDigits.Invoke(null, new object?[] { renderer, value, leading, 220f, 206f, 47f, 220f, null, 16 });
            Call("EndFrame");
        }
        HudDraw[] Visible() => HudInstances.Values.Where(draw => draw.Expiry > _hudClock).ToArray();
        try
        {
            boundary.Patch(assets.GetMethod("Draw", Instance)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateHudDraw)));
            boundary.Patch(assets.GetMethod("Expire", Static)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateHudDraw)));
            Digits("008", 2);
            Assert(Visible().Length == 3 && Visible().Count(draw => draw.Red < 0.8f) == 2,
                "The real digit renderer displays three places and dims only leading zeroes.");
            Digits("009", 2);
            Assert(Visible().Length == 3 && !HudInstances.Any(pair => pair.Key.texture == 8 && pair.Value.Expiry > _hudClock),
                "008 to 009 expires the previous texture immediately instead of retaining it for the SDK's 100 ms lifetime.");
            Digits("088", 1);
            Digits("080", 1);
            Assert(Visible().Length == 3 && HudInstances.Count(pair => pair.Key.texture == 8 && pair.Value.Expiry > _hudClock) == 1,
                "088 to 080 expires only the displaced repeated digit instance.");
            foreach (string value in new[] { "1000", "777", "123456789012345", new string('9', 39), "000" })
            {
                Digits(value);
                HudDraw[] visible = Visible();
                Assert(visible.Length == value.Length && visible.All(draw => draw.Width > 0f && draw.Height > 0f &&
                    draw.X - draw.Width * 0.5f >= -0.00001f &&
                    draw.X + draw.Width * 0.5f <= 220f / 1280f + 0.00001f &&
                    draw.Y - draw.Height * draw.Aspect * 0.5f >= 206f / 720f - 0.00001f &&
                    draw.Y + draw.Height * draw.Aspect * 0.5f <= 253f / 720f + 0.00001f),
                    "Changing speed width stays inside the HUD without clipping digits or overlapping the gauge.");
            }
            Call("Hide");
            Call("Hide");
            Assert(Visible().Length == 0, "Hiding is immediate and idempotent without waiting for sprite expiry.");
            Digits("077", 1);
            Call("BeginFrame", 1280f, 16f / 9f);
            Call("Draw", 5, 0, PointF.Empty, new SizeF(10f, 10f), 0f, false, null!);
            Call("Hide");
            Assert(Visible().Length == 0, "An interrupted frame hides both previous and newly submitted texture instances.");
        }
        finally
        {
            boundary.UnpatchAll(boundary.Id);
            HudInstances.Clear();
            _hudClock = 0;
        }
    }

    private static IEnumerable<CodeInstruction> IsolateHudDraw(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction code in instructions)
        {
            if (code.operand is MethodInfo method && method.Name == "DrawTexture" &&
                method.DeclaringType?.FullName == "RageCoopAdmin.Client.SpeedometerAssets")
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(nameof(CaptureHudDraw), Static)!;
            }
            yield return code;
        }
    }

    private static void CaptureHudDraw(int texture, int index, int level, int time,
        float width, float height, float centreX, float centreY, float x, float y,
        float rotation, float aspectRatio, float red, float green, float blue, float alpha)
    {
        Assert(texture >= 0 && index >= 0 && index <= 63 && time >= 0,
            "Each draw satisfies the installed ScriptHookV texture, instance and lifetime contract.");
        HudInstances[(texture, index)] = new HudDraw(_hudClock + time, width, height, x, y, aspectRatio, red);
    }

    private readonly struct HudDraw
    {
        internal HudDraw(int expiry, float width, float height, float x, float y, float aspect, float red)
        {
            Expiry = expiry; Width = width; Height = height; X = x; Y = y; Aspect = aspect; Red = red;
        }
        internal int Expiry { get; }
        internal float Width { get; }
        internal float Height { get; }
        internal float X { get; }
        internal float Y { get; }
        internal float Aspect { get; }
        internal float Red { get; }
    }
}
