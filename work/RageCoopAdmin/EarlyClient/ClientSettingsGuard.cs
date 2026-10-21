using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;
using HarmonyLib;
using RageCoop.Client;

namespace RageCoop.EarlyClient;

// This script is constructed before RageCoop.Client.Main. Its prefix runs
// outside Main's destructive catch, including on the first local startup.
internal static class ClientSettingsGuard
{
    private const string Owner = "friendslegacy.ragecoop.earlysettings";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Main = typeof(Settings).Assembly.GetType("RageCoop.Client.Main", true)!;
    private static readonly Type Util = typeof(Settings).Assembly.GetType("RageCoop.Client.Util", true)!;
    private static readonly FieldInfo PathField = Util.GetField("SettingsPath", Static)!;
    private static readonly FieldInfo SettingsField = Main.GetField("Settings", Static)!;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Settings));
    private static Harmony? _harmony;

    internal static ConstructorInfo MainConstructor => Main.GetConstructor(Type.EmptyTypes)!;
    internal static MethodInfo ReadTarget => Util.GetMethod("ReadSettings", Static)!;
    internal static MethodInfo SaveTarget => Util.GetMethod("SaveSettings", Static)!;

    internal static bool HasSupportedContract() => PathField.FieldType == typeof(string) && SettingsField.FieldType == typeof(Settings) &&
        ReadTarget.ReturnType == typeof(Settings) && ReadTarget.GetParameters().Select(value => value.ParameterType).SequenceEqual(new[] { typeof(string) }) &&
        SaveTarget.ReturnType == typeof(bool) && SaveTarget.GetParameters().Select(value => value.ParameterType).SequenceEqual(new[] { typeof(string), typeof(Settings) }) &&
        Matches(MainConstructor, "83e9922ace7d2d8f3d6323c7d5294dab75f1c72efc2216a8dac96f243224a99e") &&
        Matches(ReadTarget, "016751cb700e4a560658d56bdc126e5d4c24d52498eec0f15a96c3a14c976ca4") &&
        Matches(SaveTarget, "1e3bc86ee59a126b982ca882ab70943f4517b4937c76edb135e74860e34395c5");

    internal static void Install()
    {
        if (_harmony != null) return;
        if (!HasSupportedContract()) throw new InvalidOperationException("Unsupported RageCoop client settings contract.");
        var harmony = new Harmony(Owner);
        try
        {
            harmony.Patch(MainConstructor,
                prefix: new HarmonyMethod(typeof(ClientSettingsGuard), nameof(BeforeMain)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(ClientSettingsGuard), nameof(PreventDefaultOverwrite)));
            harmony.Patch(ReadTarget, prefix: new HarmonyMethod(typeof(ClientSettingsGuard), nameof(ReadSettings)));
            harmony.Patch(SaveTarget, prefix: new HarmonyMethod(typeof(ClientSettingsGuard), nameof(SaveSettings)));
        }
        catch
        {
            foreach (MethodBase target in Targets()) harmony.Unpatch(target, HarmonyPatchType.All, Owner);
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
            foreach (MethodBase target in Targets()) _harmony.Unpatch(target, HarmonyPatchType.All, Owner);
        _harmony = null;
    }

    private static MethodBase[] Targets() => new MethodBase[] { MainConstructor, ReadTarget, SaveTarget };

    private static void BeforeMain()
    {
        string path = ResolvePath(null);
        if (File.Exists(path)) ReadExisting(path);
    }

    internal static IEnumerable<CodeInstruction> PreventDefaultOverwrite(IEnumerable<CodeInstruction> original)
    {
        MethodInfo fail = typeof(ClientSettingsGuard).GetMethod(nameof(RejectDefaultOverwrite), Static)!;
        int replaced = 0;
        foreach (CodeInstruction source in original)
        {
            var code = new CodeInstruction(source);
            if (code.Calls(SaveTarget)) { code.opcode = OpCodes.Call; code.operand = fail; replaced++; }
            yield return code;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported RageCoop client settings recovery call.");
    }

    // A file can change after the constructor prefix. Never let its original
    // catch turn any later read failure into an overwrite of the latest bytes.
    private static bool RejectDefaultOverwrite(string? path, Settings? settings) => throw ReadFailure();

    private static bool ReadSettings(string? __0, ref Settings __result)
    {
        string path = ResolvePath(__0);
        if (File.Exists(path)) { __result = ReadExisting(path); return false; }
        var initial = new Settings();
        WriteAtomic(path, initial, overwrite: false);
        // Another first startup may have published its settings first.
        __result = ReadExisting(path);
        return false;
    }

    private static bool SaveSettings(string? __0, Settings? __1, ref bool __result)
    {
        __result = false;
        try
        {
            string path = ResolvePath(__0);
            Settings value = __1 ?? SettingsField.GetValue(null) as Settings ??
                throw new InvalidDataException("RageCoop client settings are unavailable.");
            // A menu write must not replace an unreadable file either.
            if (File.Exists(path)) ReadExisting(path);
            WriteAtomic(path, value, overwrite: true);
            __result = true;
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
            error is InvalidOperationException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
        {
            // Preserve the installed bool contract; callers never receive raw
            // XML, a password or a private path in an error message.
        }
        return false;
    }

    private static string ResolvePath(string? path) => Path.GetFullPath(path ?? (string)PathField.GetValue(null)!);

    private static Settings ReadExisting(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return Serializer.Deserialize(reader) as Settings ?? throw ReadFailure();
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
            error is InvalidOperationException || error is XmlException || error is System.Security.SecurityException)
        {
            throw ReadFailure();
        }
    }

    private static InvalidDataException ReadFailure() => new InvalidDataException(
        "Не удалось прочитать RageCoop.Client.Settings.xml. Исходный файл сохранён. Проверьте XML и доступ к файлу, затем перезапустите GTA.");

    private static void WriteAtomic(string path, Settings settings, bool overwrite)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The client settings directory is unavailable.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".RageCoop.Client.Settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Serializer.Serialize(stream, settings);
                stream.Flush(flushToDisk: true);
            }
            if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
            else
            {
                try { File.Move(temporary, path); }
                catch (IOException) when (!overwrite && File.Exists(path)) { }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Matches(MethodBase method, string expected)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return false;
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
    }
}
