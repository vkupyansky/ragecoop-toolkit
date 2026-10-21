using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GTA;
using HarmonyLib;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Scaleform;

internal static partial class Program
{
    private static bool _navigationController = true;
    private static int _navigationHintFactories;

    private static void CheckMenuNavigationComposition()
    {
        var boundary = new Harmony("friendslegacy.check.menunavigationboundary");
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        MethodInfo Method(string name) => script.GetMethod(name, Instance | Static)!;
        object? Call(object instance, string name, params object[] args) => Method(name).Invoke(instance, args);
        try
        {
            // Builders, submenu links, selections and hint decisions stay real.
            // Menu graphics, model availability, the device and native glyph
            // lookup are the isolated game boundaries.
            boundary.Patch(Method("CreateMenu"), prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationCreateMenu)));
            boundary.Patch(Method("CreateMenuControlHint"), prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationHintBoundary)));
            boundary.Patch(Method("IsMenuUsingController"), prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationInputModeBoundary)));
            boundary.Patch(Method("IsVehicleModelAvailable"), prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationModelUnavailable)));
            boundary.Patch(typeof(ObjectPool).GetConstructor(Type.EmptyTypes)!, prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationInitializePool)));
            foreach (string name in new[] { "UpdateItems", "Recalculate", "UpdateDescription" })
                boundary.Patch(typeof(NativeMenu).GetMethod(name, Instance, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            foreach (Type element in new[] { typeof(LemonUI.Elements.ScaledText), typeof(LemonUI.Elements.ScaledTexture), typeof(LemonUI.Elements.ScaledRectangle) })
                boundary.Patch(element.GetMethod("Recalculate", Instance, null, Type.EmptyTypes, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(LemonUI.Elements.ScaledTexture).GetMethod("Request", Instance)!, prefix: new HarmonyMethod(typeof(Program), nameof(SkipMenuLayout)));
            boundary.Patch(typeof(LemonUI.Elements.ScaledText).GetProperty("Width")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(IsolateTextWidth)));
            Type extensions = typeof(NativeMenu).Assembly.GetType("LemonUI.Tools.Extensions", true)!;
            boundary.Patch(extensions.GetMethod("ToRelative", Static, null, new[] { typeof(System.Drawing.PointF) }, null)!, prefix: new HarmonyMethod(typeof(Program), nameof(KeepLayoutPoint)));
            boundary.Patch(Method("BuildWeaponsMenu"), transpiler: new HarmonyMethod(typeof(Program), nameof(MenuNavigationIsolateWeaponCatalog)));
            foreach (string name in new[] { "Hour", "Minute" })
                boundary.Patch(typeof(GTA.Chrono.GameClock).GetProperty(name)!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(MenuNavigationClock)));
            boundary.Patch(Engine.GetType("RageCoop.Client.Scripting.API", true)!.GetProperty("IsOnServer")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(Program), nameof(InputConnectionState)));
            boundary.Patch(Method("ToggleMainMenu"), transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));

            object instance = FormatterServices.GetUninitializedObject(script);
            foreach (string name in new[] { "_catalog", "_classMenus", "_weatherItems", "_pendingModels", "_presets", "_vehicleRows", "_unavailableFavoriteRows", "_favoriteHashes", "_pendingWeapons", "_interfaceSettings", "_timeSelection" })
            {
                FieldInfo field = script.GetField(name, Instance)!;
                field.SetValue(instance, Activator.CreateInstance(field.FieldType, true));
            }
            Type addonCatalog = Resource.GetType("RageCoopAdmin.Client.AddonVehicleCatalog", true)!;
            script.GetField("_addonVehicles", Instance)!.SetValue(instance, addonCatalog.GetMethod("LoadEmbedded", Static)!.Invoke(null, Array.Empty<object>()));
            script.GetField("_active", Instance)!.SetValue(instance, true);
            _inputConnected = true;
            Call(instance, "BuildMenus");
            var root = (NativeMenu)script.GetField("_mainMenu", Instance)!.GetValue(instance)!;
            var pool = (ObjectPool)script.GetField("_pool", Instance)!.GetValue(instance)!;
            Assert(root.Items.Select(item => item.Title).SequenceEqual(new[] { "Поделиться меткой", "Транспорт", "Телепорты", "Напарники", "Персонаж", "Оружие", "Мир", "Настройки" }),
                "The actual BuildMenus creates the approved eight root rows in their fixed order.");
            Assert(root.Items.Skip(1).All(item => item is NativeSubmenuItem) && !root.Items.Any(item => item.Title.Contains("Починить")),
                "All seven root destinations are real submenus and repair stays under Transport.");
            var vehicle = ((NativeSubmenuItem)root.Items[1]).Menu;
            Assert(vehicle.Items[0].Title == "Починить машину" && vehicle.Items[1].Title == "Поставить на колёса",
                "The actual Transport menu retains repair and flip as its first actions.");
            pool.HideAll();
            root.SelectedIndex = 5;
            Call(instance, "ToggleMainMenu");
            Assert(root.Visible && root.SelectedIndex == 0, "Opening the root starts from its first action rather than a stale submenu selection.");

            void Refresh() => Call(instance, "RefreshMenuControlHints");
            void Show(NativeMenu menu, NativeItem item)
            {
                pool.HideAll(); menu.SelectedItem = item; menu.Visible = true; Refresh();
            }
            void Hints(NativeMenu menu, params string[] expected) => Assert(MenuNavigationButtons(menu).Select(button => button.Description).SequenceEqual(expected),
                "The actual contextual footer matches the selected action: " + string.Join(" / ", expected));
            root.SelectedIndex = 1; Refresh();
            Hints(root, "Открыть", "Назад");
            int built = _navigationHintFactories;
            for (int frame = 0; frame < 20; frame++) Refresh();
            Assert(_navigationHintFactories == built, "Unchanged menu frames reuse their existing native glyph slots.");
            _navigationController = false; Refresh();
            Assert(_navigationHintFactories == built + 2 && MenuNavigationButtons(root).All(button => button.Raw.StartsWith("keyboard-", StringComparison.Ordinal)),
                "Changing the input device rebuilds the existing hints with that device's glyph lookup.");
            _navigationController = true;

            var settings = ((NativeSubmenuItem)root.Items[7]).Menu;
            Show(settings, settings.Items[0]); Hints(settings, "Вкл./выкл.", "Назад");
            var player = ((NativeSubmenuItem)root.Items[4]).Menu;
            var wanted = (NativeListItem<string>)script.GetField("_wantedItem", Instance)!.GetValue(instance)!;
            Show(player, wanted); Hints(player, "", "Звёзды", "Назад");
            var hour = (NativeListItem<string>)script.GetField("_hourItem", Instance)!.GetValue(instance)!;
            NativeMenu time = pool.OfType<NativeMenu>().Single(menu => menu.Items.Contains(hour));
            Show(time, hour); Hints(time, "", "Изменить", "Назад");
            var minute = (NativeListItem<string>)script.GetField("_minuteItem", Instance)!.GetValue(instance)!;
            Show(time, minute); Hints(time, "", "Изменить", "Назад");

            var actionMenu = BareInputMenu(0); pool.Add(actionMenu);
            var car = new NativeListItem<string>("Машина", "", "Создать", "В избранное");
            var weapon = new NativeListItem<string>("Оружие", "", "Взять", "В руки", "Патроны", "Убрать");
            var preset = new NativeListItem<string>("Сохранённая машина", "", "Создать", "Удалить");
            actionMenu.Add(car); actionMenu.Add(weapon); actionMenu.Add(preset);
            foreach (NativeListItem<string> row in new[] { car, weapon, preset })
            {
                Show(actionMenu, row);
                for (int index = 0; index < row.Items.Count; index++)
                {
                    row.SelectedIndex = index; Refresh(); Hints(actionMenu, row.SelectedItem, "", "Действие", "Назад");
                }
            }
            Show(actionMenu, car); car.SelectedIndex = 1; Refresh();
            car.Items[1] = "Убрать из списка"; Refresh(); Hints(actionMenu, "Убрать из списка", "", "Действие", "Назад");
            Assert(MenuNavigationButtons(actionMenu)[1].Raw == "controller-189" && MenuNavigationButtons(actionMenu)[2].Raw == "controller-190",
                "Both direction hints request real frontend left and right glyphs through the SDK factory.");
            car.Enabled = false; Refresh(); Hints(actionMenu, "Назад");

            Type inputType = Resource.GetType("RageCoopAdmin.Client.ResourceTextInput", true)!;
            NativeMenu textMenu = BareInputMenu(0);
            object textInput = Activator.CreateInstance(inputType, Instance, null, new object[] { textMenu }, null)!;
            script.GetField("_textInput", Instance)!.SetValue(instance, textInput);
            pool.Add(textMenu);
            pool.HideAll();
            inputType.GetMethod("UpdateInputMode", Instance)!.Invoke(textInput, new object[] { true });
            inputType.GetMethod("Begin", Instance)!.Invoke(textInput, new object[] { 24, "Код модели", new Action<string?>(_ => { }) });
            Refresh(); Hints(textMenu, "Добавить", "", "Буква", "Отмена");
            Assert(textMenu.SelectedIndex == 3 && textMenu.Items[0].AltTitle == "0/24", "The controller text prompt starts on letter entry and exposes its field length limit.");
            textMenu.SelectedIndex = 1; Refresh(); Hints(textMenu, "", "Язык", "Отмена");
            textMenu.SelectedIndex = 2; Refresh(); Hints(textMenu, "", "Регистр", "Отмена");
            textMenu.SelectedIndex = 0; Refresh(); Hints(textMenu, "Отмена");
            inputType.GetMethod("UpdateInputMode", Instance)!.Invoke(textInput, new object[] { false });
            Refresh(); Hints(textMenu, "Сохранить", "Отмена");
            Assert(textMenu.Items.Count == 4, "Keyboard text entry shows only the value, clear, save and cancel actions.");
            inputType.GetMethod("CloseSilently", Instance)!.Invoke(textInput, Array.Empty<object>());

            var catalog = (IList)script.GetField("_catalog", Instance)!.GetValue(instance)!;
            Type entry = script.GetNestedType("VehicleEntry", BindingFlags.NonPublic)!;
            for (int index = 0; index < 101; index++)
                catalog.Add(Activator.CreateInstance(entry, new object[] { index + 1000, "uxsearch" + index, "UX search " + index, 0 }));
            script.GetField("_catalogReady", Instance)!.SetValue(instance, true);
            Call(instance, "ShowVehicleSearchResults", "uxsearch");
            var results = (NativeMenu)script.GetField("_searchMenu", Instance)!.GetValue(instance)!;
            Assert(results.Visible && results.Items.Count == 101 && !results.Items[0].Enabled && results.Items[0].Title == "Найдено больше 100 моделей" &&
                results.Items.Skip(1).All(item => item is NativeListItem<string>),
                "The real search shows 100 matching models plus an explicit disabled overflow notice for a 101st match.");
            results.SelectedIndex = 0; Refresh(); Hints(results, "Назад");
            List<CodeInstruction> spawnByCode = PatchProcessor.GetOriginalInstructions(Method("SpawnByCode"));
            Assert(spawnByCode.Any(code => code.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(code.operand) == 24) &&
                spawnByCode.Count(code => code.Calls(Method("PromptUserInput"))) == 1,
                "The assembled model-code action opens the shared input with the actual 24-unit field contract.");
            List<CodeInstruction> factory = PatchProcessor.GetOriginalInstructions(Method("CreateMenuControlHint"));
            Assert(factory.Count(code => code.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(InstructionalButton) &&
                constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string), typeof(GTA.Control) })) == 1,
                "Production hints use the installed SDK control constructor rather than fixed keyboard or Xbox glyph strings.");
            Console.WriteLine("Actual root navigation and contextual hint checks passed; native glyph rendering remains isolated.");
        }
        finally
        {
            _navigationController = true;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(boundary.Id) == true).ToArray())
                boundary.Unpatch(method, HarmonyPatchType.All, boundary.Id);
        }
    }

    private static InstructionalButtons BareMenuNavigationButtons()
    {
        var buttons = (InstructionalButtons)FormatterServices.GetUninitializedObject(typeof(InstructionalButtons));
        typeof(InstructionalButtons).GetField("buttons", Instance)!.SetValue(buttons, new List<InstructionalButton>());
        return buttons;
    }

    private static List<InstructionalButton> MenuNavigationButtons(NativeMenu menu) =>
        (List<InstructionalButton>)typeof(InstructionalButtons).GetField("buttons", Instance)!.GetValue(menu.Buttons)!;

    private static bool MenuNavigationHintBoundary(string __0, GTA.Control __1, ref InstructionalButton __result)
    {
        _navigationHintFactories++;
        __result = new InstructionalButton(__0, (_navigationController ? "controller-" : "keyboard-") + (int)__1);
        return false;
    }

    private static bool MenuNavigationInputModeBoundary(ref bool __result) { __result = _navigationController; return false; }
    private static bool MenuNavigationModelUnavailable(ref bool __result) { __result = false; return false; }
    private static bool MenuNavigationClock(ref int __result) { __result = 12; return false; }
    private static IEnumerable<CodeInstruction> MenuNavigationIsolateWeaponCatalog(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction source in instructions)
        {
            var code = new CodeInstruction(source);
            if (code.operand is MethodInfo method && method.DeclaringType == typeof(Weapon) && method.Name == "GetAllWeaponHashesForHumanPeds")
            {
                code.opcode = OpCodes.Call;
                code.operand = typeof(Program).GetMethod(nameof(MenuNavigationNoWeapons), Static)!;
            }
            yield return code;
        }
    }
    private static WeaponHash[] MenuNavigationNoWeapons() => Array.Empty<WeaponHash>();
    private static bool MenuNavigationCreateMenu(string __1, ref NativeMenu __result)
    { __result = BareInputMenu(0); __result.Name = __1; __result.Description = string.Empty; return false; }
    private static bool MenuNavigationInitializePool(ObjectPool __instance)
    {
        typeof(ObjectPool).GetFields(Instance).Single(field => field.FieldType == typeof(List<IProcessable>)).SetValue(__instance, new List<IProcessable>());
        return false;
    }
}
