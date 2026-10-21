using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RageCoop.Server;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ragecoop-settings-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MethodInfo target = EarlySettingsGuard.FindTarget();
            EarlySettingsGuard.Install();
            EarlySettingsGuard.Install();
            var instructions = PatchProcessor.GetCurrentInstructions(target).ToList();
            Assert(instructions.Count(instruction => instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(EarlySettingsGuard) && method.Name == nameof(EarlySettingsGuard.ReadSettings)) == 1 &&
                !instructions.Any(instruction => instruction.operand is MethodInfo method &&
                    method.DeclaringType?.FullName == "RageCoop.Server.Util" && method.Name == "Read") &&
                instructions.FindIndex(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(EarlySettingsGuard)) <
                instructions.FindIndex(instruction => instruction.operand is ConstructorInfo constructor && constructor.DeclaringType == typeof(Server)),
                "the real installed Program.Main calls the safe Settings reader exactly once before constructing the server");
            CheckExisting(directory);
            CheckInvalid(directory);
#if PUBLIC_RELEASE
            if (args.Length != 1) { throw new ArgumentException("Pass the built public CoopAdmin.Startup.dll to check its actual settings reader."); }
            CheckPublicInitialSetup(directory, args[0]);
#else
            if (args.Length != 0) { throw new ArgumentException("The private settings check accepts no arguments."); }
            CheckInitialSetup(directory);
#endif
            EarlySettingsGuard.Uninstall();
            EarlySettingsGuard.Uninstall();
            Assert(Harmony.GetPatchInfo(target)?.Owners.Contains("friendslegacy.ragecoop.earlysettings") != true,
                "uninstall removes the early Settings patch");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            EarlySettingsGuard.Uninstall();
            foreach (string file in Directory.GetFiles(directory)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(directory, true);
        }
    }

    private static void CheckExisting(string directory)
    {
        string path = Path.Combine(directory, "existing.xml");
        byte[] bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Keep formatting and unrecognized extension -->\n" +
            "<Settings><Name>Existing name</Name><Port>4567</Port><AutoUpdate>false</AutoUpdate><Extension>keep</Extension></Settings>\n");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        DateTime originalWrite = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Settings settings = EarlySettingsGuard.ReadFile(path);
        Assert(settings.Name == "Existing name" && settings.Port == 4567 && !settings.AutoUpdate,
            "the real Settings XmlSerializer reads valid read-only files with their existing values");
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == originalWrite,
            "successful Settings loading preserves every byte and the modification time");
        Assert(!EarlySettingsGuard.CreateInitialFile(path) && File.ReadAllBytes(path).SequenceEqual(bytes),
            "initial setup cannot replace a pre-existing read-only Settings file");
        File.SetAttributes(path, FileAttributes.Normal);
    }

    private static void CheckInvalid(string directory)
    {
        string[] invalid =
        {
            "<Settings><Name>private-marker</Name>",
            "<Settings><Port>private-marker</Port></Settings>",
            "<!DOCTYPE Settings [<!ENTITY secret 'private-marker'>]><Settings><Name>&secret;</Name></Settings>",
            "<WrongRoot><Name>private-marker</Name></WrongRoot>",
            "<Settings xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/>"
        };
        for (int index = 0; index < invalid.Length; index++)
        {
            string path = Path.Combine(directory, "invalid-" + index + ".xml");
            byte[] bytes = Encoding.UTF8.GetBytes(invalid[index]);
            File.WriteAllBytes(path, bytes);
            try { EarlySettingsGuard.ReadFile(path); throw new InvalidOperationException("Invalid Settings unexpectedly loaded."); }
            catch (InvalidDataException ex)
            {
                Assert(!ex.ToString().Contains("private-marker", StringComparison.Ordinal) && File.ReadAllBytes(path).SequenceEqual(bytes),
                    "invalid Settings stop loading without revealing values or replacing the source (case " + index + ")");
            }
        }
    }

    private static void CheckInitialSetup(string directory)
    {
        string path = Path.Combine(directory, "fresh.xml");
        try { EarlySettingsGuard.ReadFile(path); throw new InvalidOperationException("Fresh defaults unexpectedly started the server."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("перезапустите", StringComparison.Ordinal)) { }
        byte[] created = File.ReadAllBytes(path);
        Settings settings = EarlySettingsGuard.ReadFile(path);
        Assert(settings.Port == new Settings().Port && File.ReadAllBytes(path).SequenceEqual(created),
            "missing Settings creates a complete initial file and explicitly stops the first launch for review");
        if (!OperatingSystem.IsWindows())
            Assert(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "initial Settings is created with owner read/write access only");
        for (int round = 0; round < 8; round++)
        {
            string racing = Path.Combine(directory, "racing-" + round + ".xml");
            using var start = new ManualResetEventSlim();
            Task<bool>[] creators = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return EarlySettingsGuard.CreateInitialFile(racing);
            })).ToArray();
            start.Set();
            Task.WaitAll(creators);
            Settings winner = EarlySettingsGuard.ReadFile(racing);
            int published = creators.Count(task => task.Result);
            int temporary = Directory.GetFiles(directory, ".Settings.xml.*.tmp").Length;
            if (published != 1 || winner.Port != new Settings().Port || temporary != 0)
                throw new InvalidOperationException("Initial setup race: published=" + published + ", temporary=" + temporary + ", round=" + round);
        }
        Assert(true, "concurrent first setup publishes one complete file and removes its temporary files across eight rounds of twelve creators");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException(label); }
        Console.WriteLine("PASS " + label);
    }
}
