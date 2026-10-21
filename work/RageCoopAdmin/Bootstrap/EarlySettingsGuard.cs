using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Serialization;
using HarmonyLib;
using RageCoop.Server;

// This must be installed by the startup hook: resources load after Main reads Settings.
internal static class EarlySettingsGuard
{
    private const string PatchId = "friendslegacy.ragecoop.earlysettings";
    private static Harmony? harmony;

    internal static MethodInfo FindTarget()
    {
        Assembly server = typeof(Settings).Assembly;
        if (server.GetName().Version != new Version(1, 5, 12, 15))
            throw Unsupported("host version");
        MethodInfo main = server.GetType("RageCoop.Server.Program", true)!.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(string[]) }, null) ?? throw Unsupported("Program.Main");
        if (main.ReturnType != typeof(void)) { throw Unsupported("Program.Main return type"); }
        if (PatchProcessor.GetOriginalInstructions(main).Count(IsSettingsRead) != 1)
            throw Unsupported("Program.Main settings read call");
        return main;
    }

    internal static void Install()
    {
        if (harmony != null) { return; }
        MethodInfo target = FindTarget();
        var installing = new Harmony(PatchId);
        try
        {
            installing.Patch(target, transpiler: new HarmonyMethod(typeof(EarlySettingsGuard), nameof(RewriteSettingsRead)));
            harmony = installing;
        }
        catch
        {
            installing.Unpatch(target, HarmonyPatchType.All, PatchId);
            throw;
        }
    }

    internal static void Uninstall()
    {
        if (harmony == null) { return; }
        harmony.Unpatch(FindTarget(), HarmonyPatchType.All, PatchId);
        harmony = null;
    }

    private static bool IsSettingsRead(CodeInstruction instruction) => instruction.opcode == OpCodes.Call &&
        instruction.operand is MethodInfo method && method.IsGenericMethod &&
        method.DeclaringType == typeof(Settings).Assembly.GetType("RageCoop.Server.Util", true) && method.Name == "Read" &&
        method.GetGenericArguments().SequenceEqual(new[] { typeof(Settings) }) && method.ReturnType == typeof(Settings) &&
        method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string) });

    private static IEnumerable<CodeInstruction> RewriteSettingsRead(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (IsSettingsRead(instruction))
            {
                instruction.operand = AccessTools.Method(typeof(EarlySettingsGuard), nameof(ReadSettings));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) { throw Unsupported("Program.Main settings read call"); }
    }

    internal static Settings ReadSettings(string file)
    {
        if (file != "Settings.xml") { throw Unsupported("settings filename"); }
        return ReadFile(Path.Combine(AppContext.BaseDirectory, file));
    }

    internal static Settings ReadFile(string path)
    {
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException)
        {
#if PUBLIC_RELEASE
            CreateInitialFile(path);
            // Read the actual atomically published file, including another creator's
            // complete file if it won the race. A failed reopen must remain an error.
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
#else
            bool created = CreateInitialFile(path);
            throw new InvalidOperationException(created
                ? "Settings.xml отсутствовал. Создан начальный файл; проверьте настройки и перезапустите сервер."
                : "Settings.xml появился при первоначальной настройке; проверьте его и перезапустите сервер.");
#endif
        }
        using (input)
        {
            try
            {
                using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1024 * 1024
                });
                return new XmlSerializer(typeof(Settings)).Deserialize(reader) as Settings ??
                    throw new InvalidDataException("Settings.xml не содержит настройки сервера. Исходный файл сохранён.");
            }
            catch (InvalidOperationException)
            {
                // XmlSerializer's nested errors can contain the user's values.
                throw new InvalidDataException("Settings.xml содержит неверный XML или значения настроек. Исходный файл сохранён.");
            }
            catch (XmlException)
            {
                throw new InvalidDataException("Settings.xml содержит неверный XML. Исходный файл сохранён.");
            }
        }
    }

    internal static bool CreateInitialFile(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException("Settings.xml parent directory is unavailable.");
        string temporary = Path.Combine(directory, ".Settings.xml." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            using (var output = new FileStream(temporary, options))
            {
                var initial = new Settings();
#if PUBLIC_RELEASE
                initial.Port = 4499;
                initial.AnnounceSelf = false;
                initial.AutoUpdate = false;
                initial.UseZeroTier = false;
                initial.UseP2P = false;
                initial.UseVoice = false;
                initial.KickGodMode = false;
                initial.SpamLimit = 1024;
                initial.KickSpamming = true;
                initial.MaxPlayers = 8;
#endif
                using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = true }))
                    new XmlSerializer(typeof(Settings)).Serialize(writer, initial);
                output.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
            {
                try { File.Move(temporary, path, overwrite: false); return true; }
                catch (IOException) when (File.Exists(path)) { return false; }
            }
            // link publishes the complete, flushed inode only when the destination
            // is absent. File.Move(overwrite:false) races on the installed macOS runtime.
            if (Link(temporary, path) == 0) { return true; }
            int error = Marshal.GetLastPInvokeError();
            if (error == 17) { return false; } // POSIX EEXIST: another creator won.
            throw new IOException("Не удалось атомарно создать Settings.xml.", new Win32Exception(error));
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    private static InvalidOperationException Unsupported(string detail) =>
        new("Unsupported RageCoop early settings contract: " + detail + ".");
}
