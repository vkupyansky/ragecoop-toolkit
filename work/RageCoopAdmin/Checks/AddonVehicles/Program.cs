using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LemonUI.Menus;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CoopAddonVehiclesCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckEmptyCatalog();
#if PUBLIC_RELEASE
            CheckPublicMenu(directory);
#else
            CheckCatalog();
            CheckVehicleRowLayout();
            CheckMenuAndFavorites(directory);
            CheckRetiredFavorites(Path.Combine(directory, "retired"));
#endif
            Console.WriteLine("Add-on vehicle checks passed (GTA and menu boundaries are isolated)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckEmptyCatalog()
    {
        const string json = "{\"version\":1,\"vehicles\":[]}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert(AddonVehicleCatalog.Load(stream).Count == 0, "a valid empty catalog requires no vehicle DLC");
        MustReject(json, document => document["version"] = 2, "an empty catalog still requires a supported version");
        MustReject(json, document => document["vehicles"] = null, "a null vehicle list is not an empty catalog");
        MustReject(json, document => document.AsObject().Remove("vehicles"), "a missing vehicle list is rejected");
    }

#if PUBLIC_RELEASE
    private static void CheckPublicMenu(string directory)
    {
        Assert(AddonVehicleCatalog.LoadEmbedded().Count == 0, "the public build embeds only the empty catalog");
        GameBoundary.Reset();
        var menu = new AdminMenuScript();
        menu.Start(directory);
        Assert(!menu.HasAddonMenu && menu.RegisteredMenuCount == 1 && menu.FavoriteRows.Count > 0,
            "an empty catalog omits the add-on submenu and retains favorites");
        const int stockHash = 12345;
        GameBoundary.Installed.Add(stockHash);
        Assert(menu.CanSpawn(stockHash), "stock vehicles remain available without an add-on catalog");
        menu.Toggle(stockHash);
        var reopened = new AdminMenuScript();
        reopened.Start(directory);
        Assert(reopened.HasFavorite(stockHash), "a stock vehicle favorite survives reopening the public menu");
    }
#endif

    private static void CheckCatalog()
    {
        var vehicles = AddonVehicleCatalog.LoadEmbedded();
        Assert(vehicles.Count == 17, "all 17 agreed vehicles are embedded");
        (string Code, string Query)[] selected =
        {
            ("bmwm3f80", "бмв м3 ф80"), ("rs6abt20", "ауди рс6"), ("c63a", "мерседес c63"),
            ("765lt", "макларен"), ("19sclass", "майбах"), ("flyspur24", "бентли спур"),
            ("lp770", "ламбо центенарио"), ("divo", "бугатти диво"), ("urusperf23", "ламборгини урус"),
            ("sl63amg22", "мерс сл63"), ("tw_bx6m", "бмв х6м"), ("tw_dcsh18", "додж хеллкэт"),
            ("m5g60w", "бмв м5 туринг"), ("tw_bxm22", "бмв хм"), ("g63sec20", "мерседес г63"),
            ("raptorsxr25", "форд раптор"), ("astdb12", "астон дб12")
        };
        Assert(vehicles.Select(vehicle => vehicle.SpawnCode).ToHashSet(StringComparer.Ordinal).SetEquals(selected.Select(vehicle => vehicle.Code)),
            "the agreed nine retained and eight new variants are embedded without retired models");
        Assert(vehicles.Select(Hash).Distinct().Count() == 17, "vehicle hashes are distinct");
        Assert(vehicles.Single(vehicle => vehicle.SpawnCode == "divo").DlcFolder == "rcveh_divo" &&
            vehicles.Single(vehicle => vehicle.SpawnCode == "astdb12").DlcFolder == "rcveh_astdb12",
            "installation folder names remain separate from spawn codes");
        Assert(vehicles.Single(vehicle => vehicle.SpawnCode == "astdb12").Matches("ASTON") &&
            vehicles.Single(vehicle => vehicle.SpawnCode == "bmwm3f80").Matches("bmwm3"),
            "real names and codes are searchable without case sensitivity");
        Assert(vehicles.Single(vehicle => vehicle.SpawnCode == "bmwm3f80").Matches("БМВ M3") &&
            vehicles.Single(vehicle => vehicle.SpawnCode == "bmwm3f80").Matches("бмв м3 ф80") &&
            !vehicles.Single(vehicle => vehicle.SpawnCode == "bmwm3f80").Matches("бмв ghost"),
            "Russian brand search supports mixed words and never combines different models");
        foreach ((string code, string query) in selected)
            Assert(vehicles.Single(vehicle => vehicle.SpawnCode == code).Matches(query), code + " matches Russian search: " + query);
        Assert(vehicles.Where(vehicle => vehicle.Matches("мерседес")).Count() == 4 &&
            !vehicles.Any(vehicle => vehicle.Matches("несуществующая")) &&
            !vehicles.Any(vehicle => vehicle.Matches(" -- ")),
            "Russian search filters the actual catalog and rejects empty/non-matching queries");
        Assert(VehicleSearch.Matches("Pegassi Zentorno", "zentorno", "ZENTORNO") &&
            VehicleSearch.Matches("Police Cruiser", "police", "police cruiser"),
            "the same search retains the standard GTA catalog");
        Assert(vehicles.All(vehicle => !vehicle.CardDescription(true).Contains("Не установлена") &&
            vehicle.CardDescription(false).Contains("перезапустите игру")), "cards distinguish installed and missing models");
        Assert(vehicles.All(vehicle => vehicle.CardDescription(true).Split(new[] { "~n~" }, StringSplitOptions.None).Length == 4 &&
            vehicle.CardDescription(true).StartsWith("~h~" + vehicle.Name + "~h~~n~", StringComparison.Ordinal) &&
            vehicle.CardDescription(true).Contains("Назначение:") && vehicle.CardDescription(true).Contains("Привод:") &&
            vehicle.CardDescription(true).Contains("Передачи:") && !vehicle.CardDescription(true).Contains("стрелк")),
            "every selected car has a structured four-line card with its full name and no repeated control paragraph");

        using Stream stream = typeof(AddonVehicleCatalog).Assembly.GetManifestResourceStream("RageCoopAdmin.Client.AddonVehicles.json")!;
        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        MustReject(json, document => document["version"] = 2, "unsupported catalog format rejected");
        MustReject(json, document => document["vehicles"]![1]!["spawnCode"] = "BMWM3F80", "duplicate codes rejected regardless of case");
        MustReject(json, document => document["vehicles"]![1]!["dlcFolder"] = "../outside", "unsafe DLC folders rejected");
        MustReject(json, document => document["vehicles"]![0]!.AsObject().Remove("purpose"), "missing purpose rejected");
        MustReject(json, document => document["vehicles"]![0]!["purpose"] = "~r~Гонки", "formatting codes rejected in purpose");
        MustReject(json, document => document["vehicles"]![0]!["drive"] = "unknown", "unknown drive rejected");
        MustReject(json, document => document["vehicles"]![0]!["gears"] = 0, "invalid gear count rejected");
        MustReject(json, document => document["vehicles"]![0]!["name"] = "~r~Name", "GTA formatting codes rejected in names");

        // Compare the displayed drive and gear data with the inspected author packages.
        string metadataDirectory = Path.GetFullPath("../VehicleModels/metadata");
        foreach (AddonVehicleDefinition vehicle in vehicles)
        {
            JsonArray metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(metadataDirectory, vehicle.Id + ".json")))!.AsArray();
            XElement[] documents = metadata.Where(row => row?["content"] is JsonValue &&
                    (row["path"]!.GetValue<string>().EndsWith("/vehicles.meta", StringComparison.Ordinal) ||
                     row["path"]!.GetValue<string>().EndsWith("/handling.meta", StringComparison.Ordinal)))
                .Select(row => XElement.Parse(row!["content"]!.GetValue<string>())).ToArray();
            XElement model = documents.SelectMany(document => document.Descendants("InitDatas").Elements("Item"))
                .Single(item => item.Element("modelName")?.Value == vehicle.SpawnCode);
            string handlingName = model.Element("handlingId")!.Value;
            XElement handling = documents.SelectMany(document => document.Descendants("HandlingData").Elements("Item"))
                .Single(item => item.Element("handlingName")?.Value == handlingName);
            decimal front = decimal.Parse(handling.Element("fDriveBiasFront")!.Attribute("value")!.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            string drive = front == 0 ? "rear" : front == 1 ? "front" : "all";
            int gears = int.Parse(handling.Element("nInitialDriveGears")!.Attribute("value")!.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert(vehicle.Drive == drive && vehicle.Gears == gears,
                vehicle.SpawnCode + " card uses the selected mod's drive and gear count");
        }
    }

    private static void CheckVehicleRowLayout()
    {
        static void Budget(bool condition, string label)
        { if (!condition) { throw new InvalidOperationException(label); } }
        string[] names = AddonVehicleCatalog.LoadEmbedded().Select(vehicle => vehicle.Name)
            .Concat(new[] { "Моя сохранённая машина с длинным названием", "WWW iii WWW iii WWW iii" }).ToArray();
        foreach (float width in new[] { 433f, 390f, 300f })
        {
            foreach (string name in names)
            {
                var row = new VehicleMenuListItem(name, "~h~" + name + "~h~", VehicleMenuText.Actions);
                foreach (bool selected in new[] { false, true })
                {
                    row.Recalculate(new PointF(27f, 44f), new SizeF(width, 37.4f), selected);
                    for (int action = 0; action < row.Items.Count; action++)
                    {
                        row.SelectedIndex = action;
                        float titleEnd = 27f + 6f + new LemonUI.Elements.ScaledText(row.Title, 0.345f).Width;
                        Budget(titleEnd + 12f <= row.LeftArrow.Position.X + 0.001f && row.Description.Contains(name),
                            "row budget retains a gap at " + width + " px for " + row.Items[action] + " / " + name);
                    }
                    // Favorites change the action after the initial layout.
                    row.SelectedIndex = 0;
                    row.Items = new System.Collections.Generic.List<string> { VehicleMenuText.Create, VehicleMenuText.RemoveFavorite };
                    row.SelectedIndex = 1;
                    Budget(27f + 6f + new LemonUI.Elements.ScaledText(row.Title, 0.345f).Width + 12f <= row.LeftArrow.Position.X + 0.001f,
                        "a favorite label update keeps its reserved gap without another relayout");
                    row.Items = VehicleMenuText.Actions.ToList();
                }
                var missing = new VehicleMenuItem(name, name) { Enabled = false };
                missing.Recalculate(PointF.Empty, new SizeF(width, 37.4f), selected: true);
                Budget(new LemonUI.Elements.ScaledText(missing.Title, 0.345f).Width <= width - 12f,
                    "a missing row uses the full available title width");
            }
            Console.WriteLine("PASS all 17 car rows, saved names and five actions retain their gap at " + width + " px before and after label changes");
        }
        var proportional = new LemonUI.Elements.ScaledText(string.Empty, 0.345f);
        Assert(VehicleMenuText.Fit(proportional, "iiiiiiii", 40f) == "iiiiiiii" &&
            VehicleMenuText.Fit(proportional, "WWWWWWWW", 40f).EndsWith("…", StringComparison.Ordinal),
            "equal-length names fit by measured width rather than character count");
        Assert(VehicleMenuText.Fit(proportional, "e\u0301e\u0301e\u0301", 30f) == "e\u0301…" &&
            VehicleMenuText.Fit(proportional, "Машина", 0f) == string.Empty,
            "truncation preserves combining characters and handles a zero width budget");
    }

    private static void CheckMenuAndFavorites(string directory)
    {
        GameBoundary.Reset();
        var vehicles = AddonVehicleCatalog.LoadEmbedded();
        int bmwHash = Hash(vehicles[0]);
        var empty = new AdminMenuScript();
        empty.Start(directory);
        Assert(empty.AddonRows.Count == 17 && empty.AddonRows.All(row => !row.Enabled &&
            row.AltTitle.Length == 0 && row.Description.Contains("Не установлена")),
            "missing models retain the installation reason in the card without a colliding row label");
        foreach (NativeItem row in empty.AddonRows) { row.Select(); row.Activate(); }
        empty.Preload(bmwHash);
        Assert(GameBoundary.Requested.Count == 0 && GameBoundary.Spawned.Count == 0 && !empty.HasPreload,
            "missing models never request streaming or send a spawn");
        Assert(!empty.CanSpawn(bmwHash) && GameBoundary.Notifications.Last().Contains(vehicles[0].Name),
            "missing spawn validation gives the real car name");
        empty.Toggle(bmwHash);

        GameBoundary.Installed.Add(bmwHash);
        var installed = new AdminMenuScript();
        installed.Start(directory);
        Assert(installed.HasFavorite(bmwHash), "an add-on favorite survives reopening");
        var bmw = (NativeListItem<string>)installed.AddonRows[0];
        Assert(bmw.Description == vehicles[0].CardDescription(installed: true),
            "installed selection shows the complete name and card");
        bmw.Select();
        Assert(installed.HasPreload && GameBoundary.Requested.Last() == bmwHash, "selecting an installed car preloads only that model");
        installed.ShowAddons(true);
        Assert(installed.SpawnMenuVisible, "the add-on menu retains the selected model preload");
        installed.AddonRows[1].Select();
        Assert(!installed.HasPreload && GameBoundary.Released.Last() == bmwHash,
            "selecting a missing car releases the previous preload");
        bmw.Select();
        bmw.Activate();
        Assert(GameBoundary.Spawned.SequenceEqual(new[] { bmwHash }), "installed row creates the selected model");
        bmw.SelectedIndex = 1;
        bmw.Activate();
        Assert(!installed.HasFavorite(bmwHash) && installed.FavoritesDirty && bmw.Items[1] == "В избранное",
            "the normal favorite action works in the add-on menu");
        installed.Toggle(bmwHash);
        installed.RefreshFavorites();
        var favorite = (NativeListItem<string>)installed.FavoriteRows[1];
        Assert(favorite.Description.Contains(vehicles[0].Name), "favorites use the real add-on name");

        GameBoundary.Installed.Clear();
        Assert(!installed.CanSpawn(bmwHash) && !installed.HasPreload,
            "a model that disappears is blocked and its preload released");
        installed.RefreshFavorites();
        NativeItem unavailable = installed.FavoriteRows[1];
        Assert(unavailable.Enabled && unavailable.Description.Contains(vehicles[0].Name) &&
            unavailable.Description.Contains("Не установлена"), "missing favorite keeps its name and can be removed");
        unavailable.Activate();
        Assert(installed.HasFavorite(bmwHash), "ordinary activation retains an unavailable favorite");
        ((NativeListItem<string>)unavailable).SelectedIndex = 1;
        unavailable.Activate();
        Assert(!installed.HasFavorite(bmwHash), "explicitly removing an unavailable favorite preserves the normal storage path");
        GameBoundary.Installed.Add(bmwHash);
        installed.Preload(bmwHash);
        installed.ShowAddons(false);
        Assert(!installed.SpawnMenuVisible, "leaving all spawn menus permits preload cleanup");
        installed.ClosePreload();
        Assert(!installed.HasPreload, "preload cleanup completes after closing the menu");
    }

    private static void CheckRetiredFavorites(string directory)
    {
        GameBoundary.Reset();
        string[] retiredCodes = { "pts21", "2024lixiangl9", "zeekr001", "ghost24", "trial", "s500", "benzcclass", "e63sf" };
        int[] retiredHashes = retiredCodes.Select(code => unchecked((int)RageCoop.Client.StringHash.AtStringHash(code))).ToArray();
        var store = new FavoriteVehicleStore(directory);
        store.Save(retiredHashes);
        string path = Path.Combine(directory, "vehicle-favorites.xml");
        byte[] original = File.ReadAllBytes(path);
        var menu = new AdminMenuScript();
        menu.Start(directory);
        menu.RefreshFavorites();
        NativeItem[] unavailable = menu.FavoriteRows.Skip(1).ToArray();
        Assert(unavailable.Length == retiredHashes.Length && unavailable.All(row => row.Enabled &&
            row is NativeListItem<string> actions && actions.Items.SequenceEqual(new[] { VehicleMenuText.Unavailable, VehicleMenuText.RemoveFavorite })) &&
            unavailable.Select(menu.FavoriteHash).ToHashSet().SetEquals(retiredHashes),
            "all eight retired models remain separate, removable favorite records when their DLC is absent");
        foreach (NativeItem row in unavailable) { row.Select(); row.Activate(); }
        menu.RefreshFavorites();
        Assert(original.SequenceEqual(File.ReadAllBytes(path)) && store.Load().SetEquals(retiredHashes) &&
            GameBoundary.Requested.Count == 0 && GameBoundary.Spawned.Count == 0 && !menu.HasPreload,
            "opening, rebuilding and activating retired favorites preserves their file and never starts missing models");

        int retainedHash = Hash(AddonVehicleCatalog.LoadEmbedded().Single(vehicle => vehicle.SpawnCode == "bmwm3f80"));
        GameBoundary.Installed.Add(retainedHash);
        menu.Toggle(retainedHash);
        Assert(store.Load().SetEquals(retiredHashes.Concat(new[] { retainedHash })),
            "adding a retained model preserves all retired favorite records");
        menu.RefreshFavorites();
        var removed = (NativeListItem<string>)menu.FavoriteRows.Single(row => row is NativeListItem<string> && menu.FavoriteHash(row) == retiredHashes[0]);
        removed.SelectedIndex = 1;
        removed.Activate();
        int[] expected = retiredHashes.Skip(1).Concat(new[] { retainedHash }).ToArray();
        var reopened = new AdminMenuScript();
        reopened.Start(directory);
        Assert(store.Load().SetEquals(expected) && expected.All(reopened.HasFavorite) && !reopened.HasFavorite(retiredHashes[0]),
            "explicit removal deletes only the chosen retired favorite and the remaining records survive reopening");
    }

    private static int Hash(AddonVehicleDefinition vehicle) => unchecked((int)RageCoop.Client.StringHash.AtStringHash(vehicle.SpawnCode));

    private static void MustReject(string json, Action<JsonObject> change, string label)
    {
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        change(document);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        bool rejected = false;
        try { AddonVehicleCatalog.Load(stream); }
        catch (Exception ex) when (ex is InvalidDataException || ex is SerializationException) { rejected = true; }
        Assert(rejected, label);
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        Console.WriteLine("PASS " + label);
    }
}
