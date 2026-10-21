using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

internal static partial class Program
{
    private static void CheckSpeedometerAssets()
    {
        const string prefix = "RageCoopAdmin.Client.Assets.Speedometer.";
        Type store = Resource.GetType("RageCoopAdmin.Client.SpeedometerAssets", true)!;
        MethodInfo prepare = store.GetMethod("PrepareAssets", Static)!;
        string root = Path.Combine(Path.GetTempPath(), "RageCoopHudCheck-" + Guid.NewGuid().ToString("N"));
        string Prepare(string path) => (string)prepare.Invoke(null, new object[] { path })!;
        var expected = new HashSet<string>(new[] { "base.png", "needle.png", "unit.png", "gear-N.png", "gear-R.png" });
        for (int i = 0; i < 10; i++) expected.Add("digit-" + i + ".png");
        for (int i = 1; i <= 8; i++) expected.Add("gear-" + i + ".png");
        string[] resources = Resource.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert(expected.SetEquals(resources.Select(name => name.Substring(prefix.Length))),
            "The actual client DLL contains exactly the 23 prepared HUD images, without depending on extra client files.");
        var original = new Dictionary<string, byte[]>();
        long rgbaBytes = 0;
        foreach (string resource in resources)
        {
            using Stream source = Resource.GetManifestResourceStream(resource)!;
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            original.Add(resource.Substring(prefix.Length), bytes.ToArray());
            bytes.Position = 0;
            using Image image = Image.FromStream(bytes, useEmbeddedColorManagement: false, validateImageData: true);
            Assert(image.RawFormat.Guid == ImageFormat.Png.Guid && Image.IsAlphaPixelFormat(image.PixelFormat) &&
                image.Width > 0 && image.Width <= 512 && image.Height > 0 && image.Height <= 512,
                "Every embedded HUD sprite is a valid transparent PNG with a bounded texture size.");
            rgbaBytes += image.Width * image.Height * 4;
        }
        Assert(rgbaBytes < 4 * 1024 * 1024 && original.Sum(pair => pair.Value.Length) < 200 * 1024,
            "The prepared images stay below four MiB of raw RGBA and 200 KiB of transfer, rather than loading the original 2048 pixel sheets.");
        try
        {
            string folder = Prepare(root);
            Assert(folder == Path.Combine(root, "RageCoop", "CoopControl", "Hud", "fss-1.3-v1"),
                "The HUD uses one versioned cache independent of the server resource's private data directory.");
            void CheckFiles()
            {
                Assert(expected.SetEquals(Directory.GetFiles(folder).Select(Path.GetFileName)),
                    "Asset preparation leaves precisely the intended images and no temporary files.");
                foreach (KeyValuePair<string, byte[]> pair in original)
                    Assert(File.ReadAllBytes(Path.Combine(folder, pair.Key)).SequenceEqual(pair.Value),
                        "Each extracted image exactly matches the real built client resource.");
            }
            CheckFiles();
            DateTime unchanged = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            foreach (string name in expected) File.SetLastWriteTimeUtc(Path.Combine(folder, name), unchanged);
            Assert(Prepare(root) == folder, "Reconnection selects the same texture paths.");
            Assert(expected.All(name => File.GetLastWriteTimeUtc(Path.Combine(folder, name)) == unchanged),
                "Unchanged cached graphics are not rewritten on reconnection.");
            File.WriteAllText(Path.Combine(folder, "base.png"), "damaged cache");
            Prepare(root);
            CheckFiles();
            Assert(File.GetLastWriteTimeUtc(Path.Combine(folder, "needle.png")) == unchanged,
                "Repairing a damaged image does not rewrite other cached graphics.");

            Directory.Delete(folder, recursive: true);
            Parallel.For(0, 8, _ => Prepare(root));
            CheckFiles();
            Assert(Directory.GetDirectories(Path.GetDirectoryName(folder)!).Length == 1,
                "Concurrent first preparation remains idempotent and does not create extra texture-cache versions.");

            string blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "file, not a directory");
            bool rejected = false;
            try { Prepare(blocked); }
            catch (TargetInvocationException exception) when (exception.InnerException is IOException) { rejected = true; }
            Assert(rejected, "An unavailable graphics cache reports an I/O failure instead of returning fake success.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
