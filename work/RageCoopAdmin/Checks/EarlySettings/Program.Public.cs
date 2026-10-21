#if PUBLIC_RELEASE
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RageCoop.Server;

internal static partial class Program
{
    private static void CheckPublicInitialSetup(string directory, string bootstrapPath)
    {
        Assembly bootstrap = Assembly.LoadFrom(Path.GetFullPath(bootstrapPath));
        Assert(bootstrap.GetName().Name == "CoopAdmin.Startup", "the public check loads the actual built startup hook");
        MethodInfo read = bootstrap.GetType("EarlySettingsGuard", throwOnError: true)!.GetMethod("ReadFile", BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The public startup hook has no Settings reader.");

        Settings Read(string path)
        {
            try
            {
                return read.Invoke(null, new object[] { path }) as Settings ?? throw new InvalidOperationException("The public reader returned no Settings.");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        bool Defaults(Settings settings) => settings.Port == 4499 && !settings.AnnounceSelf && !settings.AutoUpdate &&
            !settings.UseZeroTier && !settings.UseP2P && !settings.UseVoice && !settings.KickGodMode &&
            settings.SpamLimit == 1024 && settings.KickSpamming && settings.MaxPlayers == 8;

        string fresh = Path.Combine(directory, "public-fresh.xml");
        Settings first = Read(fresh);
        byte[] created = File.ReadAllBytes(fresh);
        Assert(Defaults(first), "the actual public bootstrap creates, reads and returns connection and gameplay defaults in its first call");
        Assert(Defaults(Read(fresh)) && File.ReadAllBytes(fresh).SequenceEqual(created),
            "repeating the actual public reader preserves the complete initial file");
        if (!OperatingSystem.IsWindows())
            Assert(File.GetUnixFileMode(fresh) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "the public initial Settings file permits only owner read/write access");

        byte[] custom = Encoding.UTF8.GetBytes("<!-- Preserve this existing configuration -->\n<Settings><Port>4611</Port>" +
            "<AnnounceSelf>true</AnnounceSelf><AutoUpdate>true</AutoUpdate><UseZeroTier>true</UseZeroTier>" +
            "<UseP2P>true</UseP2P><UseVoice>true</UseVoice><KickGodMode>true</KickGodMode>" +
            "<SpamLimit>73</SpamLimit><KickSpamming>false</KickSpamming><MaxPlayers>11</MaxPlayers></Settings>\n");
        File.WriteAllBytes(fresh, custom);
        Settings existing = Read(fresh);
        Assert(existing.Port == 4611 && existing.AnnounceSelf && existing.AutoUpdate && existing.UseZeroTier && existing.UseP2P && existing.UseVoice &&
            existing.KickGodMode && existing.SpamLimit == 73 && !existing.KickSpamming && existing.MaxPlayers == 11 &&
            File.ReadAllBytes(fresh).SequenceEqual(custom), "public first-start defaults do not replace existing explicit settings");

        string malformed = Path.Combine(directory, "public-malformed.xml");
        byte[] invalid = Encoding.UTF8.GetBytes("<Settings><Port>private-public-marker</Port></Settings>");
        File.WriteAllBytes(malformed, invalid);
        try { Read(malformed); throw new InvalidOperationException("The actual public reader accepted invalid Settings."); }
        catch (InvalidDataException error)
        {
            Assert(!error.ToString().Contains("private-public-marker", StringComparison.Ordinal) && File.ReadAllBytes(malformed).SequenceEqual(invalid),
                "the actual public bootstrap preserves malformed settings and reports no private value");
        }

        for (int round = 0; round < 2; round++)
        {
            string racing = Path.Combine(directory, "public-racing-" + round + ".xml");
            using var start = new ManualResetEventSlim();
            Task<Settings>[] readers = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return Read(racing);
            })).ToArray();
            start.Set();
            Task.WaitAll(readers);
            Assert(readers.All(task => Defaults(task.Result)) && Defaults(Read(racing)) && Directory.GetFiles(directory, ".Settings.xml.*.tmp").Length == 0,
                "concurrent actual public first readers return the complete published file (round " + round + ")");
        }
    }
}
#endif
