using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

internal static partial class Program
{
    private static void CheckSpeedometerTextureCache()
    {
        Type assets = Resource.GetType("RageCoopAdmin.Client.SpeedometerAssets", true)!;
        MethodInfo get = assets.GetMethod("GetOrCreateTextures", Static)!;
        using Process process = Process.GetCurrentProcess();
        int pid = process.Id;
        long started = process.StartTime.ToUniversalTime().Ticks;
        string root = Path.Combine(Path.GetTempPath(), "RageCoopHudIdsCheck-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "textures");
        Directory.CreateDirectory(directory);
        Dictionary<string, int> Get(string path, int processId, long ticks, Func<string, int> create) =>
            (Dictionary<string, int>)get.Invoke(null, new object[] { path, processId, ticks, create })!;
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (TargetInvocationException exception) when (exception.InnerException is IOException || exception.InnerException is InvalidDataException ||
                exception.InnerException is InvalidOperationException)
            { return true; }
        }
        int[] InNewDomain(int firstId)
        {
            AppDomain domain = AppDomain.CreateDomain("HUD texture cache check " + Guid.NewGuid().ToString("N"));
            try
            {
                var probe = (HudTextureCacheProbe)domain.CreateInstanceFromAndUnwrap(
                    typeof(HudTextureCacheProbe).Assembly.Location, typeof(HudTextureCacheProbe).FullName);
                return probe.Prepare(Resource.Location, directory, pid, started, firstId);
            }
            finally { AppDomain.Unload(domain); }
        }
        try
        {
            int[] first = InNewDomain(0);
            int[] reloaded = InNewDomain(1000);
            Assert(first[0] == 23 && first.Skip(1).Contains(0) && reloaded[0] == 0 &&
                first.Skip(1).SequenceEqual(reloaded.Skip(1)),
                "A real new AppDomain reuses all 23 native IDs, including valid ID zero, without creating textures after a script reload.");
            string record = Path.Combine(directory, "texture-ids.xml");
            byte[] valid = File.ReadAllBytes(record);
            DateTime unchanged = File.GetLastWriteTimeUtc(record);
            Assert(Get(directory, pid, started, _ => throw new InvalidOperationException("Unexpected SDK creation")).Count == 23 &&
                File.GetLastWriteTimeUtc(record) == unchanged,
                "Reusing completed texture identities does not rewrite the record or call the SDK.");
            int foreignCreated = 0;
            Assert(Rejects(() => Get(directory, int.MaxValue, started, _ => foreignCreated++)) && foreignCreated == 0,
                "A different process cannot replace a live GTA process's native identities.");

            var malformed = new List<Action<XElement>>
            {
                element => element.SetAttributeValue("version", "unknown"),
                element => element.SetAttributeValue("pending", "base.png"),
                element => element.SetAttributeValue("unexpected", "value"),
                element => element.Elements().First().SetAttributeValue("name", "../outside.png"),
                element => element.Elements().First().SetAttributeValue("id", -1),
                element => element.Elements().Skip(1).First().SetAttributeValue("id", element.Elements().First().Attribute("id")!.Value),
                element => element.Add(new XElement(element.Elements().First()))
            };
            foreach (Action<XElement> mutate in malformed)
            {
                XElement element = XElement.Parse(System.Text.Encoding.UTF8.GetString(valid));
                mutate(element);
                File.WriteAllText(record, element.ToString());
                int creations = 0;
                Assert(Rejects(() => Get(directory, pid, started, _ => creations++)) && creations == 0,
                    "Invalid, duplicated or unfinished current-process identities never trigger blind SDK reallocation.");
            }
            foreach (string invalid in new[]
            {
                "<broken>", new string('x', 16385),
                "<!DOCTYPE texture-cache [<!ENTITY external SYSTEM 'file:///not-a-real-file'>]><texture-cache>&external;</texture-cache>"
            })
            {
                File.WriteAllText(record, invalid);
                int creations = 0;
                Assert(Rejects(() => Get(directory, pid, started, _ => creations++)) && creations == 0,
                    "Damaged, excessive and DTD-bearing current-process cache data is rejected before native allocations.");
            }

            File.WriteAllText(record, "<old damaged record>");
            File.SetLastWriteTimeUtc(record, new DateTime(started - TimeSpan.TicksPerSecond, DateTimeKind.Utc));
            int recovered = 0;
            Assert(Get(directory, pid, started, _ => recovered++).Count == 23 && recovered == 23,
                "A damaged cache from before this process started can be rebuilt after GTA restarts.");

            string failed = Path.Combine(root, "failed");
            int attempts = 0;
            Assert(Rejects(() => Get(failed, pid, started, _ => { attempts++; throw new IOException("Isolated SDK failure"); })) && attempts == 1,
                "A native creation failure retains a pending identity.");
            Assert(Rejects(() => Get(failed, pid, started, _ => { attempts++; return 0; })) && attempts == 1,
                "Reloading after an ambiguous native failure does not allocate duplicate textures.");

            string previous = Path.Combine(root, "previous-process");
            int oldCreated = 100;
            Assert(Rejects(() => Get(previous, int.MaxValue, started - TimeSpan.TicksPerSecond,
                _ => { oldCreated++; throw new IOException("Previous isolated process stopped"); })),
                "A previous stopped process may leave an incomplete record.");
            int newCreated = 0;
            Assert(Get(previous, pid, started, _ => newCreated++).Count == 23 && newCreated == 23,
                "A new process fingerprint replaces stale identities and recovers its own fresh native objects.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class HudTextureCacheProbe : MarshalByRefObject
{
    public int[] Prepare(string assemblyPath, string directory, int pid, long started, int firstId)
    {
        Assembly resource = Assembly.LoadFrom(assemblyPath);
        MethodInfo method = resource.GetType("RageCoopAdmin.Client.SpeedometerAssets", true)!
            .GetMethod("GetOrCreateTextures", BindingFlags.Static | BindingFlags.NonPublic)!;
        int created = 0;
        var values = (Dictionary<string, int>)method.Invoke(null, new object[]
        {
            directory, pid, started, new Func<string, int>(_ => firstId + created++)
        })!;
        return new[] { created }.Concat(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)).ToArray();
    }
}
