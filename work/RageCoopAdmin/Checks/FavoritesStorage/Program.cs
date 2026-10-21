using System;
using System.IO;
using System.Linq;
using RageCoopAdmin.Client;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CoopFavoritesCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckStorage(directory);
            Console.WriteLine("Favorites storage checks passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckStorage(string directory)
    {
        var store = new FavoriteVehicleStore(directory);
        Assert(store.Load().Count == 0, "new profile starts empty");
        store.Save(new[] { -1216765807, 970598228 });
        Assert(new FavoriteVehicleStore(directory).Load().SetEquals(new[] { -1216765807, 970598228 }),
            "signed model hashes survive reopen");
        store.Save(new[] { 970598228 });
        Assert(store.Load().SetEquals(new[] { 970598228 }), "atomic replacement preserves new list");

        string path = Path.Combine(directory, "vehicle-favorites.xml");
        string previous = File.ReadAllText(path);
        MustReject(() => store.Save(new[] { 970598228, 970598228 }), "duplicate hashes rejected");
        Assert(File.ReadAllText(path) == previous, "invalid update preserves prior data");
        MustReject(() => store.Save(new[] { 0 }), "zero hash rejected");

        File.WriteAllText(path, "<FavoriteVehicles version=\"99\"><Models/></FavoriteVehicles>");
        string unsupported = File.ReadAllText(path);
        MustReject(() => store.Load(), "unsupported format rejected");
        Assert(File.ReadAllText(path) == unsupported, "failed read does not overwrite data");
        File.WriteAllText(path,
            "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///never-read-this\">]>" +
            "<FavoriteVehicles version=\"1\"><Models/></FavoriteVehicles>");
        MustReject(() => store.Load(), "DTD rejected");
        Assert(!Directory.GetFiles(directory, "*.tmp").Any(), "temporary writes cleaned");

        string blocked = Path.Combine(directory, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "vehicle-favorites.xml"));
        MustReject(() => new FavoriteVehicleStore(blocked).Save(new[] { 970598228 }), "failed commit reported");
        Assert(!Directory.GetFiles(blocked, "*.tmp").Any(), "failed commit cleans only its temporary file");
    }

    private static void Assert(bool value, string label)
    {
        if (!value)
        {
            throw new InvalidOperationException(label);
        }

        Console.WriteLine("PASS " + label);
    }

    private static void MustReject(Action action, string label)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
            ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.Xml.XmlException)
        {
            rejected = true;
        }

        Assert(rejected, label);
    }
}
