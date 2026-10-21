using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using GTA.Math;
using HarmonyLib;
using Lidgren.Network;
using RageCoop.Core;

namespace CoopControl.Shared;

// Both endpoints use the same installed Core. Keep its existing wire format,
// except for the asymmetric short/Vector2/voice encodings in that build.
internal static class EngineCodecGuard
{
    private const string PatchId = "friendslegacy.ragecoop.enginecodec";
    internal const int MaximumByteArrayLength = 1024 * 1024;
    internal const int MaximumCustomArguments = 256;
    private const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Assembly Core = typeof(PublicExtensions).Assembly;
    private static readonly Type Extensions = Core.GetType("RageCoop.Core.PacketExtensions", true)!;
    private static readonly Type CustomEvent = Core.GetType("RageCoop.Core.Packets+CustomEvent", true)!;
    private static readonly Type Voice = Core.GetType("RageCoop.Core.Packets+Voice", true)!;
    private static readonly Type CoreUtils = Core.GetType("RageCoop.Core.CoreUtils", true)!;
    private static readonly MethodInfo ReadInt32 = typeof(NetBuffer).GetMethod(nameof(NetBuffer.ReadInt32), Type.EmptyTypes)!;
    private static readonly MethodInfo ReadInt16 = typeof(NetBuffer).GetMethod(nameof(NetBuffer.ReadInt16), Type.EmptyTypes)!;
    private static readonly MethodInfo WriteBytes = typeof(NetBuffer).GetMethod(nameof(NetBuffer.Write), new[] { typeof(byte[]) })!;
    private static readonly MethodInfo WriteByteArray = Extensions.GetMethod("WriteByteArray", Methods, null, new[] { typeof(NetOutgoingMessage), typeof(byte[]) }, null)!;
    private static readonly MethodInfo Vector2ToVector3 = typeof(Vector2).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == "op_Implicit" && method.ReturnType == typeof(Vector3) && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Vector2));
    private static readonly MethodInfo WriteVector3 = Extensions.GetMethod("Write", Methods, null, new[] { typeof(NetOutgoingMessage), typeof(Vector3) }, null)!;
    private static readonly MethodInfo ReadEndpoint = CoreUtils.GetMethod("StringToEndPoint", Methods, null, new[] { typeof(string) }, null)!;
    private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
    private static readonly string[] ByteArrayReaderNames = { "ChatMessage", "FileTransferChunk", "Handshake", "PublicKeyResponse", "Voice" };
    private static readonly Dictionary<string, string> PacketReaderBodies = new Dictionary<string, string>
    {
        ["ChatMessage"] = "1e9a3979cc8ccae6eaf8e440732074c7cc89e82a6953ea219e15e49c67336110",
        ["FileTransferRequest"] = "9bf5ac81d7e53a99951b47f1da2649c440e49724910b43e5108d1884f7a646ef",
        ["FileTransferResponse"] = "737d0191f03ba26a72058fa69a9d1f874b2f4e50f3adfe682f50c6b54183a65a",
        ["FileTransferChunk"] = "e60c7332529d9d144ced5f2ed502c81797064728042a9e6ddef17cb2dacc0394",
        ["FileTransferComplete"] = "26cf107cd9a19b9ff6c17ea9ca648067a363c373461733aad0ff5abc2f5ce3a1",
        ["HolePunchInit"] = "d781144d1a7f69dad03e67307aed41d6c6e659abfde50146f52031317b1a809b",
        ["HolePunch"] = "545f45760fc455f0e8ad825ac2beffd6dd95c376b42bc67008747df0a6bebcc0",
        ["ConnectionRequest"] = "fb76438b958e08748602bf9a644d93b6cc9bead7a4ca5e786a6068f200caa47e",
        ["P2PConnect"] = "c03f109bd68b5196427be4356af26fb3b03cc933293eb336fa412c922d31f56f",
        ["PedSync"] = "7cae8dd757cb94280cbabc7b6e90ead6d37cf34cc699b72c227f8cae984a1fec",
        ["Handshake"] = "1ab32fe5bbcd613e7898e17999f9c187ea5dbd3f0737e60b19c5bf31fc56d07a",
        ["HandshakeSuccess"] = "8dcae458c16cc28e130ab2a50eac66161f76694021205072ad387b809fcd9708",
        ["PlayerConnect"] = "3ca27f3dee6fe956392d047053721de64e6b9f0b3cee96b5a640fd214e48908c",
        ["PlayerDisconnect"] = "279d18de59904fd9048aa2de4cbfe319a74cba2fa2b574d3db85f400c894f770",
        ["PlayerInfoUpdate"] = "5acf6c2c823c05b773a6d94d648940482e6166b8240366f51f9865f9a614c91a",
        ["PublicKeyResponse"] = "f8e48e01effa12d74a488cbb6bad782d1171c235a7fc7e0b3584db2a96227add",
        ["ProjectileSync"] = "ce64872fdb92f96534df521bfeebd2187d13a66c7f20c2e08f27e7bf0ec4da90",
        ["BulletShot"] = "dad77ce3ce214060fa3c62e5f3bae35ccd841dec6ccb3e6bffc573327218aeed",
        ["NozzleTransform"] = "51962832ae02ea98b9cedf5e6ec85388d2bc2eec2262efb02a0525812b06bf5f",
        ["OwnerChanged"] = "c98f0189cee8409770880d278b79bf098b902ce40b18bfa70b64960ccc87baf4",
        ["PedKilled"] = "769d51e42efd25d957841da6f5965433eb19d9b8c485490ca1bbb79095d9b8db",
        ["VehicleBulletShot"] = "7ce11df6e88550887ab72e65ab8f08224fb4dd8650950171cc770255e27daa0c",
        ["VehicleSync"] = "fa7972f2e314ad8eed1f25686c6550f42bc798928c3e2e3be033002e45ad0322",
        ["Voice"] = "7a73312aa3bc0948abd2682dc26b9c3820a7bf8414774f996502103c516a1e41"
    };
    private static readonly Dictionary<MethodInfo, MethodInfo> BoundedReads = CreateBoundedReads();
    private static readonly HashSet<Type> SnapshotPackets = new HashSet<Type>(new[]
    { "PedSync", "VehicleSync", "ProjectileSync", "PlayerInfoUpdate", "BulletShot", "VehicleBulletShot" }
        .Select(name => Core.GetType("RageCoop.Core.Packets+" + name, true)!));
    private static readonly Dictionary<MethodInfo, MethodInfo> FiniteReads = new[] { "Float", "Vector3", "Quaternion" }
        .ToDictionary(name => typeof(EngineCodecGuard).GetMethod("BoundedRead" + name, Methods)!,
            name => typeof(EngineCodecGuard).GetMethod("ReadFinite" + name, Methods)!);
    private static Harmony? _harmony;

    internal static MethodInfo FindByteArrayTarget() => Extensions.GetMethod("ReadByteArray", Methods, null, new[] { typeof(NetIncomingMessage) }, null)!;
    internal static MethodInfo FindCustomEventTarget() => CustomEvent.GetMethod("Deserialize", Methods, null, new[] { typeof(NetIncomingMessage) }, null)!;
    internal static MethodInfo FindVoiceSerializeTarget() => Voice.GetMethod("Serialize", Methods, null, new[] { typeof(NetOutgoingMessage) }, null)!;
    internal static MethodInfo FindObjectWriterTarget() => CoreUtils.GetMethod("GetBytesFromObject", Methods, null, new[] { typeof(object), typeof(NetOutgoingMessage) }, null)!;
    internal static MethodInfo[] FindByteArrayReaders() => ByteArrayReaderNames.Select(name => Core.GetType("RageCoop.Core.Packets+" + name, true)!
        .GetMethod("Deserialize", Methods, null, new[] { typeof(NetIncomingMessage) }, null)!).ToArray();

    internal static MethodInfo[] FindPacketReaders() => PacketReaderBodies.Keys.Select(name => Core.GetType("RageCoop.Core.Packets+" + name, true)!
        .GetMethod("Deserialize", Methods, null, new[] { typeof(NetIncomingMessage) }, null)!).ToArray();

    internal static void Install()
    {
        if (_harmony != null) { return; }
        if (!HasSupportedContract()) { throw Unsupported(); }
        var harmony = new Harmony(PatchId);
        try
        {
            Patch(harmony, FindByteArrayTarget(), prefix: nameof(BeforeByteArray));
            Patch(harmony, FindCustomEventTarget(), transpiler: nameof(RewriteCustomEvent));
            Patch(harmony, FindVoiceSerializeTarget(), transpiler: nameof(RewriteVoice));
            Patch(harmony, FindObjectWriterTarget(), transpiler: nameof(RewriteObjectWriter));
            // Client resource loading has already exercised file-transfer
            // readers. Rewrite their direct calls too, so an earlier JIT inline
            // cannot retain the unbounded helper inside a compiled caller.
            foreach (MethodInfo reader in FindPacketReaders())
                Patch(harmony, reader, transpiler: nameof(RewritePacketReader),
                    postfix: reader.DeclaringType == Voice ? nameof(ValidateVoice) : null);
        }
        catch
        {
            foreach (MethodInfo target in Targets) { harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
            Targets.Clear();
            throw;
        }
        _harmony = harmony;
    }

    internal static void Uninstall()
    {
        if (_harmony != null)
        {
            foreach (MethodInfo target in Targets) { _harmony.Unpatch(target, HarmonyPatchType.All, PatchId); }
        }
        Targets.Clear();
        _harmony = null;
    }

    private static void Patch(Harmony harmony, MethodInfo target, string? prefix = null, string? transpiler = null, string? postfix = null)
    {
        Targets.Add(target);
        harmony.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(EngineCodecGuard), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(EngineCodecGuard), postfix),
            transpiler: transpiler == null ? null : new HarmonyMethod(typeof(EngineCodecGuard), transpiler));
    }

    private static void ValidateVoice(object __instance)
    {
        int recorded = (int)Voice.GetProperty("Recorded")!.GetValue(__instance)!;
        byte[] buffer = (byte[])Voice.GetProperty("Buffer")!.GetValue(__instance)!;
        if (recorded < 0 || recorded > buffer.Length) { throw new InvalidDataException("Invalid RageCoop voice sample length."); }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static bool HasSupportedContract()
    {
        // The exact original IL is the version contract. Include both ends of
        // each asymmetric codec so an updated writer/reader cannot be guessed.
        return Core.GetName().Name == "RageCoop.Core" &&
            HasOriginalBody(FindByteArrayTarget(), "2cb7559518c05f7b9d30ff0b39a23d276eaafb61bc520b8083dedf437fdf1ef5") &&
            HasOriginalBody(WriteByteArray, "93f5e0ef907e71e6256758326ce36542418f235160c66819266b7bfd9e69ab30") &&
            HasOriginalBody(FindCustomEventTarget(), "38e86d18c4eb257c60de13ead8545678fc09bdcea80999792713ba1a05373094") &&
            HasOriginalBody(CustomEvent.GetMethod("Serialize", Methods, null, new[] { typeof(NetOutgoingMessage) }, null)!, "1f3a987e21531e59da7628e51e0bdbf0448313dda653550cd952031f5f3be73f") &&
            HasOriginalBody(FindVoiceSerializeTarget(), "6845bf727292ea3a7df3c28ef6079a4fac725d65a3d5991c8e1b98a105312d53") &&
            HasOriginalBody(Voice.GetMethod("Deserialize", Methods, null, new[] { typeof(NetIncomingMessage) }, null)!, "7a73312aa3bc0948abd2682dc26b9c3820a7bf8414774f996502103c516a1e41") &&
            HasOriginalBody(FindObjectWriterTarget(), "ff2d8652eacb8911e21a187de02ea8e58fe9d06f918b6600b6a4e7a0102a9691") &&
            HasOriginalBody(Extensions.GetMethod("ReadVector2", Methods)!, "c9307439bd60534efb3bcc3cfd9148d55ad89678afae229ed3dbae89dd8694c3") &&
            HasOriginalBody(Extensions.GetMethod("ReadVector3", Methods)!, "c720252a1fe5f986f930fa201ffd99d9db67e741985889bc4338fd5e09215ad8") &&
            HasOriginalBody(Extensions.GetMethod("ReadQuaternion", Methods)!, "aa62e3e375c0ed5ef3116eb6ec4773e111c699d0ed7e22bc3b5b59221abc936c") &&
            FindPacketReaders().All(method => HasOriginalBody(method, PacketReaderBodies[method.DeclaringType!.Name]));
    }

    private static bool BeforeByteArray(NetIncomingMessage __0, ref byte[] __result)
    {
        __result = ReadBoundedByteArray(__0);
        return false;
    }

    private static byte[] ReadBoundedByteArray(NetIncomingMessage message)
    {
        RequireBits(message, 32);
        int length = message.ReadInt32();
        if (length < 0 || length > MaximumByteArrayLength || length > RemainingBits(message) / 8)
        {
            throw new InvalidDataException("Invalid RageCoop byte-array length.");
        }
        return message.ReadBytes(length);
    }

    private static int ReadCustomHash(NetIncomingMessage message)
    {
        RequireBits(message, 64);
        return message.ReadInt32();
    }

    private static int ReadArgumentCount(NetIncomingMessage message)
    {
        RequireBits(message, 32);
        int count = message.ReadInt32();
        // Every argument needs at least its one-byte tag, including arguments
        // resolved by the engine's existing custom resolver.
        if (count < 0 || count > MaximumCustomArguments || count > RemainingBits(message) / 8)
        {
            throw new InvalidDataException("Invalid RageCoop custom-event argument count.");
        }
        return count;
    }

    internal static IEnumerable<CodeInstruction> RewriteCustomEvent(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        int[] reads = codes.Select((code, index) => new { code, index }).Where(item => item.code.Calls(ReadInt32)).Select(item => item.index).ToArray();
        if (reads.Length != 5 || reads.Any(index => index == 0 || codes[index - 1].opcode != OpCodes.Ldarg_1) ||
            codes[reads[1] + 1].opcode != OpCodes.Stloc_0 ||
            codes[reads[2] + 1].opcode != OpCodes.Box || !Equals(codes[reads[2] + 1].operand, typeof(int)))
        {
            throw Unsupported();
        }
        codes[reads[0]].opcode = OpCodes.Call;
        codes[reads[0]].operand = typeof(EngineCodecGuard).GetMethod(nameof(ReadCustomHash), Methods)!;
        codes[reads[1]].opcode = OpCodes.Call;
        codes[reads[1]].operand = typeof(EngineCodecGuard).GetMethod(nameof(ReadArgumentCount), Methods)!;
        codes[reads[2]].operand = ReadInt16;
        codes[reads[2] + 1].operand = typeof(short);
        return RewritePacketReads(codes);
    }

    internal static IEnumerable<CodeInstruction> RewritePacketReads(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        int count = 0;
        foreach (CodeInstruction code in codes)
        {
            if (code.operand is not MethodInfo source || !BoundedReads.TryGetValue(source, out MethodInfo? replacement)) { continue; }
            code.opcode = OpCodes.Call;
            code.operand = replacement;
            count++;
        }
        if (count == 0) { throw Unsupported(); }
        return codes;
    }

    private static IEnumerable<CodeInstruction> RewritePacketReader(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        if (__originalMethod.DeclaringType!.Name == "PedSync") { codes = ResetPedWeaponComponents(codes).ToList(); }
        if (__originalMethod.DeclaringType!.Name == "Handshake")
        {
            if (codes.Count(code => code.Calls(ReadEndpoint)) != 1) { throw Unsupported(); }
            foreach (CodeInstruction code in codes)
                if (code.Calls(ReadEndpoint)) { code.operand = typeof(EngineCodecGuard).GetMethod(nameof(ReadLiteralEndpoint), Methods)!; }
        }
        if (__originalMethod.DeclaringType!.Name == "HandshakeSuccess")
        {
            // The first integer allocates PlayerData[] before any element is read.
            int read = codes.FindIndex(code => code.Calls(ReadInt32));
            if (read < 0 || codes.Count(code => code.Calls(ReadInt32)) != 2) { throw Unsupported(); }
            codes[read].opcode = OpCodes.Call;
            codes[read].operand = typeof(EngineCodecGuard).GetMethod(nameof(ReadPlayerCount), Methods)!;
        }
        codes = RewritePacketReads(codes).ToList();
        if (SnapshotPackets.Contains(__originalMethod.DeclaringType!))
            foreach (CodeInstruction code in codes)
                if (code.operand is MethodInfo source && FiniteReads.TryGetValue(source, out MethodInfo? finite)) { code.operand = finite; }
        return codes;
    }

    internal static IEnumerable<CodeInstruction> ResetPedWeaponComponents(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        Type packet = Core.GetType("RageCoop.Core.Packets+PedSync", true)!;
        MethodInfo clothes = packet.GetProperty("Clothes")!.GetSetMethod()!;
        MethodInfo components = packet.GetProperty("WeaponComponents")!.GetSetMethod()!;
        MethodInfo presence = typeof(NetBuffer).GetMethod(nameof(NetBuffer.ReadBoolean), Type.EmptyTypes)!;
        int at = codes.FindIndex(code => code.Calls(clothes)) + 1;
        if (at < 1 || at + 3 >= codes.Count || codes.Count(code => code.Calls(clothes)) != 1 ||
            codes.Count(code => code.Calls(components)) != 1 || codes[at].opcode != OpCodes.Ldarg_1 ||
            !codes[at + 1].Calls(presence) || codes[at + 2].opcode != OpCodes.Brfalse_S || codes[at + 3].opcode != OpCodes.Ldarg_0)
            throw Unsupported();
        // Only full packets reach Clothes and the following presence bit. The
        // shared receive packet must forget the prior ped's components when
        // this full packet explicitly has none; short packets retain their cache.
        var reset = new CodeInstruction(OpCodes.Ldarg_0);
        reset.labels.AddRange(codes[at].labels); reset.blocks.AddRange(codes[at].blocks);
        codes[at].labels.Clear(); codes[at].blocks.Clear();
        codes.InsertRange(at, new[] { reset, new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Call, components) });
        return codes;
    }

    private static IPEndPoint ReadLiteralEndpoint(string value)
    {
        // Handshake.Serialize writes IPEndPoint.ToString(), never a hostname.
        // Keep DNS available for user-entered server addresses in CoreUtils.
        if (value == null || value.Length == 0 || value.Length > 80) { throw new InvalidDataException("Invalid RageCoop handshake endpoint."); }
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) { throw new InvalidDataException("Invalid RageCoop handshake endpoint."); }
        string address = value.Substring(0, separator), port = value.Substring(separator + 1);
        if (address[0] == '[' && address[address.Length - 1] == ']') { address = address.Substring(1, address.Length - 2); }
        else if (address.IndexOf(':') >= 0) { throw new InvalidDataException("Invalid RageCoop handshake endpoint."); }
        if (port.Any(character => character < '0' || character > '9') ||
            !int.TryParse(port, out int number) || number > 65535 || !IPAddress.TryParse(address, out IPAddress? ip))
            throw new InvalidDataException("Invalid RageCoop handshake endpoint.");
        return new IPEndPoint(ip, number);
    }

    private static Dictionary<MethodInfo, MethodInfo> CreateBoundedReads()
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>();
        foreach (string name in new[] { "ReadByte", "ReadInt16", "ReadUInt16", "ReadInt32", "ReadUInt32", "ReadInt64", "ReadUInt64", "ReadFloat", "ReadBoolean", "ReadString" })
        {
            replacements.Add(typeof(NetBuffer).GetMethod(name, Type.EmptyTypes)!, typeof(EngineCodecGuard).GetMethod("Bounded" + name, Methods)!);
        }
        replacements.Add(typeof(NetBuffer).GetMethod("ReadBytes", new[] { typeof(int) })!, typeof(EngineCodecGuard).GetMethod(nameof(ReadFixedBytes), Methods)!);
        replacements.Add(FindByteArrayTarget(), typeof(EngineCodecGuard).GetMethod(nameof(ReadBoundedByteArray), Methods)!);
        foreach (string name in new[] { "ReadVector2", "ReadVector3", "ReadQuaternion" })
            replacements.Add(Extensions.GetMethod(name, Methods)!, typeof(EngineCodecGuard).GetMethod("Bounded" + name, Methods)!);
        return replacements;
    }

    internal static byte BoundedReadByte(NetIncomingMessage message) { RequireBits(message, 8); return message.ReadByte(); }
    private static short BoundedReadInt16(NetIncomingMessage message) { RequireBits(message, 16); return message.ReadInt16(); }
    private static ushort BoundedReadUInt16(NetIncomingMessage message) { RequireBits(message, 16); return message.ReadUInt16(); }
    internal static int BoundedReadInt32(NetIncomingMessage message) { RequireBits(message, 32); return message.ReadInt32(); }
    private static uint BoundedReadUInt32(NetIncomingMessage message) { RequireBits(message, 32); return message.ReadUInt32(); }
    private static long BoundedReadInt64(NetIncomingMessage message) { RequireBits(message, 64); return message.ReadInt64(); }
    private static ulong BoundedReadUInt64(NetIncomingMessage message) { RequireBits(message, 64); return message.ReadUInt64(); }
    private static float BoundedReadFloat(NetIncomingMessage message) { RequireBits(message, 32); return message.ReadFloat(); }
    private static bool BoundedReadBoolean(NetIncomingMessage message) { RequireBits(message, 1); return message.ReadBoolean(); }
    private static byte[] ReadFixedBytes(NetIncomingMessage message, int count)
    {
        if (count < 0 || count > MaximumByteArrayLength || count > RemainingBits(message) / 8) { throw new InvalidDataException("Invalid RageCoop fixed-array length."); }
        return message.ReadBytes(count);
    }
    private static Vector2 BoundedReadVector2(NetIncomingMessage message)
    {
        RequireBits(message, 64);
        return new Vector2(message.ReadFloat(), message.ReadFloat());
    }
    private static Vector3 BoundedReadVector3(NetIncomingMessage message)
    {
        RequireBits(message, 96);
        return new Vector3(message.ReadFloat(), message.ReadFloat(), message.ReadFloat());
    }
    private static Quaternion BoundedReadQuaternion(NetIncomingMessage message)
    {
        RequireBits(message, 128);
        return new Quaternion(message.ReadFloat(), message.ReadFloat(), message.ReadFloat(), message.ReadFloat());
    }
    private static float ReadFiniteFloat(NetIncomingMessage message)
    {
        float value = BoundedReadFloat(message);
        if (!Finite(value)) { throw new InvalidDataException("Non-finite RageCoop snapshot value."); }
        return value;
    }
    private static Vector3 ReadFiniteVector3(NetIncomingMessage message)
    {
        Vector3 value = BoundedReadVector3(message);
        if (!Finite(value.X) || !Finite(value.Y) || !Finite(value.Z)) { throw new InvalidDataException("Non-finite RageCoop snapshot vector."); }
        return value;
    }
    private static Quaternion ReadFiniteQuaternion(NetIncomingMessage message)
    {
        Quaternion value = BoundedReadQuaternion(message);
        if (!Finite(value.X) || !Finite(value.Y) || !Finite(value.Z) || !Finite(value.W)) { throw new InvalidDataException("Non-finite RageCoop snapshot rotation."); }
        return value;
    }
    private static int ReadPlayerCount(NetIncomingMessage message)
    {
        int count = BoundedReadInt32(message);
        if (count < 0 || count > RemainingBits(message) / 40) { throw new InvalidDataException("Invalid RageCoop player count."); }
        return count;
    }
    internal static string BoundedReadString(NetIncomingMessage message)
    {
        uint length = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = BoundedReadByte(message);
            if (shift == 28 && part > 15) { throw new InvalidDataException("Invalid RageCoop string length."); }
            length |= (uint)(part & 127) << shift;
            if ((part & 128) != 0) { continue; }
            if (length > MaximumByteArrayLength || length > RemainingBits(message) / 8) { throw new InvalidDataException("Invalid RageCoop string length."); }
            if (length == 0) { return string.Empty; }
            // The installed UTF8 decoder's replacement behavior is unchanged.
            // ReadBytes also preserves strings starting after a one-bit bool.
            if ((message.Position & 7) != 0) { return Encoding.UTF8.GetString(message.ReadBytes((int)length)); }
            string result = Encoding.UTF8.GetString(message.Data, (int)(message.Position / 8), (int)length);
            message.Position += (long)length * 8;
            return result;
        }
        throw new InvalidDataException("Invalid RageCoop string length.");
    }

    internal static IEnumerable<CodeInstruction> RewriteVoice(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        int[] writes = codes.Select((code, index) => new { code, index }).Where(item => item.code.Calls(WriteBytes)).Select(item => item.index).ToArray();
        if (writes.Length != 1) { throw Unsupported(); }
        codes[writes[0]].opcode = OpCodes.Call;
        codes[writes[0]].operand = WriteByteArray;
        return codes;
    }

    internal static IEnumerable<CodeInstruction> RewriteObjectWriter(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        int[] conversions = codes.Select((code, index) => new { code, index }).Where(item => item.code.Calls(Vector2ToVector3)).Select(item => item.index).ToArray();
        if (conversions.Length != 1 || conversions[0] + 1 >= codes.Count || !codes[conversions[0] + 1].Calls(WriteVector3)) { throw Unsupported(); }
        // Retain both positions and their labels: the stack already holds the
        // outgoing message and Vector2, so its conversion must be omitted.
        codes[conversions[0]].opcode = OpCodes.Nop;
        codes[conversions[0]].operand = null;
        codes[conversions[0] + 1].operand = typeof(EngineCodecGuard).GetMethod(nameof(WriteVector2), Methods)!;
        return codes;
    }

    private static void WriteVector2(NetOutgoingMessage message, Vector2 value)
    {
        message.Write(value.X);
        message.Write(value.Y);
    }

    private static long RemainingBits(NetIncomingMessage message)
    {
        long position = message.Position;
        int length = message.LengthBits;
        if (message.Data == null || position < 0 || length < 0 || position > length || length > (long)message.Data.Length * 8)
        {
            throw new InvalidDataException("Invalid RageCoop message bounds.");
        }
        return length - position;
    }

    private static void RequireBits(NetIncomingMessage message, int count)
    {
        if (RemainingBits(message) < count) { throw new InvalidDataException("Truncated RageCoop message."); }
    }

    private static bool HasOriginalBody(MethodInfo method, string expected)
    {
        byte[]? body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) { return false; }
        using (SHA256 hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", string.Empty).ToLowerInvariant() == expected;
        }
    }

    private static InvalidOperationException Unsupported() => new InvalidOperationException("Unsupported RageCoop codec contract.");
}
