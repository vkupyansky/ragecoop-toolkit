using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LemonUI.Elements;
using LemonUI.Menus;

internal static partial class Program
{
    private static void CheckVehicleCardComposition()
    {
        var boundary = new Harmony("friendslegacy.check.vehiclecardboundary");
        Type rowType = Resource.GetType("RageCoopAdmin.Client.VehicleMenuListItem", true)!;
        Type missingType = Resource.GetType("RageCoopAdmin.Client.VehicleMenuItem", true)!;
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        FieldInfo titleField = typeof(NativeItem).GetField("title", Instance)!;
        FieldInfo actionField = typeof(NativeListItem).GetField("text", Instance)!;
        string[] actions = { "Создать", "В избранное", "Убрать из списка", "Недоступно", "Удалить" };
        try
        {
            // Keep the actual resource fitter and installed SDK row layout.
            // Native glyph metrics and graphics conversion are isolated here;
            // the proportional metric checks geometry, not GTA's rendered font.
            foreach (Type element in new[] { typeof(ScaledText), typeof(ScaledTexture), typeof(ScaledRectangle) })
                boundary.Patch(element.GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!,
                    prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(ScaledTexture).GetMethod("Request", Instance)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(ScaledText).GetProperty("Width")!.GetGetMethod()!,
                prefix: new HarmonyMethod(typeof(Program), nameof(VehicleCardTextWidth)));
            Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
            boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(PointF) }, null)!,
                prefix: new HarmonyMethod(typeof(Program), nameof(KeepLayoutPoint)));

            Type catalog = Resource.GetType("RageCoopAdmin.Client.AddonVehicleCatalog", true)!;
            object[] vehicles = ((IEnumerable)catalog.GetMethod("LoadEmbedded", Static)!.Invoke(null, Array.Empty<object>())!).Cast<object>().ToArray();
            bool shortened = false;
            foreach (float width in new[] { 433f, 390f, 300f })
            {
                foreach (object vehicle in vehicles)
                {
                    Type definition = vehicle.GetType();
                    string name = (string)definition.GetProperty("Name")!.GetValue(vehicle)!;
                    string card = (string)definition.GetMethod("CardDescription", Instance)!.Invoke(vehicle, new object[] { true })!;
                    var row = (NativeListItem<string>)Activator.CreateInstance(rowType, Instance, null,
                        new object[] { name, card, actions }, null)!;
                    foreach (bool selected in new[] { false, true })
                    {
                        row.Recalculate(new PointF(27f, 44f), new SizeF(width, 37.4f), selected);
                        string stableTitle = row.Title;
                        for (int action = 0; action < row.Items.Count; action++)
                        {
                            row.SelectedIndex = action;
                            CheckVehicleCardGap(row, titleField, actionField, name, width);
                            if (row.Title != name) shortened = true;
                            if (row.Title != stableTitle) throw new InvalidOperationException("Changing an action moved the car title.");
                        }
                        row.SelectedIndex = 0;
                        row.Items = new System.Collections.Generic.List<string> { actions[0], actions[2] };
                        row.SelectedIndex = 1;
                        CheckVehicleCardGap(row, titleField, actionField, name, width);
                        row.Items = actions.ToList();
                    }
                    var missing = (NativeItem)Activator.CreateInstance(missingType, Instance, null,
                        new object[] { name, card }, null)!;
                    missing.Enabled = false;
                    missing.Recalculate(PointF.Empty, new SizeF(width, 37.4f), true);
                    ScaledText missingTitle = (ScaledText)titleField.GetValue(missing)!;
                    if (missingTitle.Width > width - 12f + 0.001f)
                        throw new InvalidOperationException("A missing-car title exceeds its row.");
                }
                Assert(true, "Actual resource rows and installed SDK arrows reserve all five vehicle actions at " + width + " scaled px, including a favorite label update.");
            }
            Assert(shortened, "Long vehicle names are actually shortened under proportional metrics while their complete names remain in the card.");
            foreach (string methodName in new[] { "AddVehicleItem", "RefreshFavoritesMenu", "RefreshVehiclePresets" })
            {
                MethodInfo method = script.GetMethod(methodName, Instance)!;
                Assert(PatchProcessor.GetOriginalInstructions(method).Any(code =>
                    code.operand is ConstructorInfo constructor && constructor.DeclaringType == rowType),
                    "The actual " + methodName + " builder uses the same measured vehicle row.");
            }
            Assert(PatchProcessor.GetOriginalInstructions(script.GetMethod("AddAddonVehicleItem", Instance)!).Any(code =>
                code.operand is ConstructorInfo constructor && constructor.DeclaringType == missingType),
                "The actual unavailable add-on builder uses the measured plain vehicle row.");
            Assert(vehicles.All(vehicle => ((string)vehicle.GetType().GetMethod("CardDescription", Instance)!.Invoke(vehicle, new object[] { true })!)
                .Split(new[] { "~n~" }, StringSplitOptions.None).Length == 4),
                "The built resource embeds all 17 structured cards with explicit SDK line breaks.");
            CheckSavedVehicleCardIdentity(boundary);
        }
        finally
        {
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static void CheckSavedVehicleCardIdentity(Harmony boundary)
    {
        string directory = Path.Combine(Path.GetTempPath(), "CoopVehicleCardCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PatchMenuStorageBoundaries(boundary);
            MethodInfo width = typeof(ScaledText).GetProperty("Width")!.GetGetMethod()!;
            boundary.Unpatch(width, HarmonyPatchType.Prefix, boundary.Id);
            boundary.Patch(width, prefix: new HarmonyMethod(typeof(Program), nameof(VehicleCardTextWidth)));
            var host = new MenuStorageHost(directory);
            const string fullName = "Мой Mercedes-AMG E63 S 4Matic+ W213";
            host.SetPresets(host.Preset("Первая", 101), host.Preset(fullName, 202));
            host.SavePresets();
            host.Call("BuildVehicleToolsMenu");
            NativeMenu garage = host.Menu("_presetsMenu");
            garage.SelectedItem = garage.Items.Single(item => item.Tag as string == fullName);
            var selected = (NativeListItem<string>)garage.SelectedItem;
            selected.Recalculate(PointF.Empty, new SizeF(300f, 37.4f), true);
            Assert(selected.Title != fullName && selected.Description.Contains(fullName),
                "A long saved-car title is measured and shortened while its full custom name remains in the card.");
            host.Presets.Insert(0, host.Preset("Новая", 303));
            host.SavePresets();
            host.Call("RefreshVehiclePresets");
            Assert(garage.SelectedItem.Tag as string == fullName && garage.SelectedIndex == 3,
                "The actual garage preserves saved-car identity after truncation and a row insertion, independently of displayed Title.");
            string path = Path.Combine(directory, "vehicle-presets.xml");
            byte[] before = File.ReadAllBytes(path);
            selected = (NativeListItem<string>)garage.SelectedItem;
            selected.SelectedIndex = 1;
            host.Activate(selected);
            NativeMenu confirmation = host.Menu("_presetDeleteMenu");
            NativeItem deletion = confirmation.Items[1];
            deletion.Recalculate(PointF.Empty, new SizeF(300f, 37.4f), true);
            var deleteTitle = (ScaledText)typeof(NativeItem).GetField("title", Instance)!.GetValue(deletion)!;
            Assert(confirmation.SelectedIndex == 0 && deletion.Description.Contains(fullName) &&
                deleteTitle.Width <= 288.001f && before.SequenceEqual(File.ReadAllBytes(path)),
                "The named delete confirmation also fits its row and retains the complete saved name before a separate confirmation.");
            host.Activate(confirmation.Items[0]);
            Assert(before.SequenceEqual(File.ReadAllBytes(path)), "Cancelling deletion of a shortened saved-car row preserves the actual XML.");
            selected.SelectedIndex = 1;
            host.Activate(selected);
            host.Activate(confirmation.Items[1]);
            Assert(host.StoredPresetNames().SequenceEqual(new[] { "Новая", "Первая" }),
                "Confirming a shortened saved-car row removes exactly its full-name identity through the actual store.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckVehicleCardGap(NativeListItem<string> row, FieldInfo titleField, FieldInfo actionField, string name, float width)
    {
        ScaledText title = (ScaledText)titleField.GetValue(row)!;
        ScaledText action = (ScaledText)actionField.GetValue(row)!;
        float rightEdge = title.Position.X + title.Width;
        if (rightEdge + 12f > row.LeftArrow.Position.X + 0.001f ||
            action.Position.X + action.Width > row.RightArrow.Position.X + row.RightArrow.Size.Width + 5.001f ||
            !row.Description.Contains(name))
            throw new InvalidOperationException("Vehicle title/action overlap at " + width + " px: " + name + " / " + row.SelectedItem);
    }

    private static bool VehicleCardTextWidth(ScaledText __instance, ref float __result)
    {
        __result = __instance.Text.Sum(character => character is 'W' or 'M' or 'Ж' or 'Ш' ? 13f :
            character is 'i' or 'l' or ' ' ? 4f : 9f) * __instance.Scale / 0.345f;
        return false;
    }
}
