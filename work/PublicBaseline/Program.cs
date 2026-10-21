using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string RemovedResource = "costura.system.diagnostics.diagnosticsource.dll.compressed";
    private const string RemovedAssembly = "System.Diagnostics.DiagnosticSource";
    private const string MetadataResource = "costura.metadata";
    private const string RemovedPayloadSha256 = "d22133818a30313e0becf010d78a556a56b34ea361dbd33588c9817631fed540";
    private static readonly Input[] Inputs =
    [
        new("client", "RageCoop.Client.dll", "client/scripts/RageCoop/RageCoop.Client.dll", "0fde6879b5cf6dd5eeb2b91684aca67215bec39c21baa37f1ec0d653163d517a"),
        new("client", "RageCoop.Core.dll", "client/scripts/RageCoop/RageCoop.Core.dll", "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256"),
        new("client", "Lidgren.Network.dll", "client/scripts/RageCoop/Lidgren.Network.dll", "0cf348189180207818848fe669b60b03d9176596fa5b4bb02533ffcb04e599c3"),
        new("server", "RageCoop.Server.dll", "server/RageCoop.Server.dll", "cfafbe010ce5003ef67e957c05e870dd1b0ab3268339b1fa140abe72d126e711"),
        new("server", "RageCoop.Core.dll", "server/RageCoop.Core.dll", "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256"),
        new("server", "ScriptHookVDotNet.dll", "server/ScriptHookVDotNet.dll", "53c67854b3fe7a9b6b51e1be5ddb99b47b119652c39e216165265f7271971ba1"),
        new("server", "ScriptHookVDotNet3.dll", "server/ScriptHookVDotNet3.dll", "3b2cd1a998d278f584c287bb05bbe14d1699ce0e4f117b62038f25fa19427951"),
        new("server", "RageCoop.Server.deps.json", "server/RageCoop.Server.deps.json", "b3cae23af707fb5269f21d3baee914459125577754306aba670861b617d6dcf4"),
        new("server", "RageCoop.Server.runtimeconfig.json", "server/RageCoop.Server.runtimeconfig.json", "ded3aeb5856a11db0b654a785574490cab55839ebfb17efe9e39b89618fc5b91")
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: PublicBaseline <compatible-client-directory> <compatible-server-directory> <new-output-directory>");
            return 2;
        }

        try
        {
            Prepare(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException or ArgumentException or OverflowException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void Prepare(string client, string server, string output)
    {
        if (Path.Exists(output)) throw new IOException("The output directory must not exist.");
        string parent = Path.GetDirectoryName(output) ?? throw new IOException("The output needs a parent directory.");
        if (!Directory.Exists(parent)) throw new IOException("The output parent directory must exist.");
        if (Directory.GetParent(output) is null) throw new IOException("The filesystem root cannot be an output.");

        var sources = Inputs.Select(input =>
        {
            string path = Path.Combine(input.Scope == "client" ? client : server, input.Name);
            byte[] bytes = File.ReadAllBytes(path);
            if (Hash(bytes) != input.Sha256) throw new InvalidDataException("Unsupported source SHA256: " + input.Scope + "/" + input.Name);
            return (Input: input, Bytes: bytes);
        }).ToArray();

        byte[] original = sources[0].Bytes;
        byte[] transformed = Transform(original);
        Verification verification = Verify(original, transformed);
        string staging = Path.Combine(parent, ".public-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<object>();
            foreach (var source in sources)
            {
                byte[] bytes = source.Input == Inputs[0] ? transformed : source.Bytes;
                string target = Path.Combine(staging, source.Input.Target.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes);
                byte[] written = File.ReadAllBytes(target);
                if (!written.AsSpan().SequenceEqual(bytes)) throw new IOException("Written artifact differs: " + source.Input.Target);
                files.Add(new { path = source.Input.Target, sourceSha256 = source.Input.Sha256, sha256 = Hash(written), size = written.Length });
            }

            var manifest = new
            {
                version = 1,
                compatibility = "RageCoop 1.5.12.15 / SHVDN 3.7.0-nightly.188",
                provenance = "Pinned compatible local binaries; exact upstream build revision has not been established. This is not a claim of byte identity with a current upstream download.",
                transformation = new { resource = RemovedResource, metadata = MetadataResource, removedAssemblySha256 = RemovedPayloadSha256, operation = "Remove the unused manifest resource and its Costura metadata row; zero its unreferenced payload; preserve every method body, metadata heap, non-resource table and remaining resource." },
                verification,
                files
            };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            Directory.Move(staging, output);
            Console.WriteLine("Prepared nine pinned baseline files; raw IL, metadata and resource verification passed.");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static byte[] Transform(byte[] original)
    {
        using var pe = new PEReader(new MemoryStream(original, false));
        MetadataReader reader = pe.GetMetadataReader();
        if (pe.PEHeaders.CorHeader!.StrongNameSignatureDirectory.Size != 0 || pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size != 0 || pe.PEHeaders.PEHeader.CheckSum != 0)
            throw new InvalidDataException("Signed or checksummed images are outside this transformation contract.");
        var resources = Resources(pe, reader);
        Resource removed = resources.Single(resource => resource.Name == RemovedResource);
        Resource metadata = resources.Single(resource => resource.Name == MetadataResource);
        AssertUnused(reader);
        foreach (Resource resource in resources)
        {
            if (reader.GetManifestResource(resource.Handle).GetCustomAttributes().Count != 0)
                throw new InvalidDataException("Resource custom attributes are outside this transformation contract.");
            if (!resource.Name.EndsWith(".dll.compressed", StringComparison.Ordinal)) continue;
            byte[] assembly = Inflate(resource.Bytes);
            if (resource == removed)
            {
                if (Hash(assembly) != RemovedPayloadSha256) throw new InvalidDataException("Unexpected DiagnosticSource payload.");
                continue;
            }
            using var dependency = new PEReader(new MemoryStream(assembly, false));
            AssertUnused(dependency.GetMetadataReader());
        }

        byte[] replacementMetadata = RemoveMetadataRow(metadata.Bytes);
        byte[] result = (byte[])original.Clone();
        result.AsSpan(removed.FileOffset, 4 + removed.Bytes.Length).Clear();
        result.AsSpan(metadata.FileOffset, 4 + metadata.Bytes.Length).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(metadata.FileOffset, 4), replacementMetadata.Length);
        replacementMetadata.CopyTo(result, metadata.FileOffset + 4);

        int metadataOffset = RvaOffset(pe, pe.PEHeaders.CorHeader.MetadataDirectory.RelativeVirtualAddress);
        int table = metadataOffset + reader.GetTableMetadataOffset(TableIndex.ManifestResource);
        int rowSize = reader.GetTableRowSize(TableIndex.ManifestResource);
        int removedRow = table + (MetadataTokens.GetRowNumber(removed.Handle) - 1) * rowSize;
        int tablesEnd = Enum.GetValues<TableIndex>().Select(index => metadataOffset + reader.GetTableMetadataOffset(index) + reader.GetTableRowCount(index) * reader.GetTableRowSize(index)).Max();
        result.AsSpan(removedRow + rowSize, tablesEnd - removedRow - rowSize).CopyTo(result.AsSpan(removedRow));
        result.AsSpan(tablesEnd - rowSize, rowSize).Clear();
        int countOffset = ResourceCountOffset(result, metadataOffset);
        if (BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(countOffset, 4)) != resources.Count)
            throw new InvalidDataException("Unexpected resource table row count.");
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(countOffset, 4), checked((uint)(resources.Count - 1)));
        return result;
    }

    private static Verification Verify(byte[] original, byte[] transformed)
    {
        using var source = new PEReader(new MemoryStream(original, false));
        using var output = new PEReader(new MemoryStream(transformed, false));
        MetadataReader before = source.GetMetadataReader();
        MetadataReader after = output.GetMetadataReader();
        int beforeOffset = RvaOffset(source, source.PEHeaders.CorHeader!.MetadataDirectory.RelativeVirtualAddress);
        int afterOffset = RvaOffset(output, output.PEHeaders.CorHeader!.MetadataDirectory.RelativeVirtualAddress);
        int tables = 0;
        foreach (TableIndex index in Enum.GetValues<TableIndex>())
        {
            if (index == TableIndex.ManifestResource) continue;
            int count = before.GetTableRowCount(index);
            int size = before.GetTableRowSize(index);
            if (count != after.GetTableRowCount(index) || size != after.GetTableRowSize(index) ||
                !original.AsSpan(beforeOffset + before.GetTableMetadataOffset(index), count * size).SequenceEqual(transformed.AsSpan(afterOffset + after.GetTableMetadataOffset(index), count * size)))
                throw new InvalidDataException("A non-resource metadata table changed: " + index);
            tables++;
        }
        foreach (HeapIndex index in Enum.GetValues<HeapIndex>())
        {
            int size = before.GetHeapSize(index);
            if (size != after.GetHeapSize(index) || !original.AsSpan(beforeOffset + before.GetHeapMetadataOffset(index), size).SequenceEqual(transformed.AsSpan(afterOffset + after.GetHeapMetadataOffset(index), size)))
                throw new InvalidDataException("A metadata heap changed: " + index);
        }

        int bodies = 0;
        int ilBytes = 0;
        foreach (MethodDefinitionHandle handle in before.MethodDefinitions)
        {
            MethodDefinition oldMethod = before.GetMethodDefinition(handle);
            MethodDefinition newMethod = after.GetMethodDefinition(handle);
            if (oldMethod.RelativeVirtualAddress != newMethod.RelativeVirtualAddress) throw new InvalidDataException("A method RVA changed.");
            if (oldMethod.RelativeVirtualAddress == 0) continue;
            MethodBodyBlock oldBody = source.GetMethodBody(oldMethod.RelativeVirtualAddress);
            MethodBodyBlock newBody = output.GetMethodBody(newMethod.RelativeVirtualAddress);
            int oldOffset = RvaOffset(source, oldMethod.RelativeVirtualAddress);
            int newOffset = RvaOffset(output, newMethod.RelativeVirtualAddress);
            if (oldBody.Size != newBody.Size || !original.AsSpan(oldOffset, oldBody.Size).SequenceEqual(transformed.AsSpan(newOffset, newBody.Size)))
                throw new InvalidDataException("A raw method body changed: " + before.GetString(oldMethod.Name));
            bodies++;
            ilBytes += oldBody.GetILContent().Length;
        }

        var oldResources = Resources(source, before);
        var newResources = Resources(output, after);
        if (newResources.Count != oldResources.Count - 1 || newResources.Any(resource => resource.Name == RemovedResource)) throw new InvalidDataException("The excluded resource is still present.");
        foreach (Resource resource in oldResources.Where(resource => resource.Name != RemovedResource))
        {
            Resource current = newResources.Single(candidate => candidate.Name == resource.Name);
            ManifestResource oldEntry = before.GetManifestResource(resource.Handle);
            ManifestResource newEntry = after.GetManifestResource(current.Handle);
            byte[] expected = resource.Name == MetadataResource ? RemoveMetadataRow(resource.Bytes) : resource.Bytes;
            if (!expected.AsSpan().SequenceEqual(current.Bytes) || oldEntry.Offset != newEntry.Offset || oldEntry.Attributes != newEntry.Attributes || oldEntry.Implementation != newEntry.Implementation)
                throw new InvalidDataException("A retained resource changed unexpectedly: " + resource.Name);
        }
        Resource removed = oldResources.Single(resource => resource.Name == RemovedResource);
        if (transformed.AsSpan().IndexOf(removed.Bytes) >= 0 || transformed.AsSpan(removed.FileOffset, 4 + removed.Bytes.Length).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("The excluded payload remains in the image.");
        if (before.GetGuid(before.GetModuleDefinition().Mvid) != after.GetGuid(after.GetModuleDefinition().Mvid)) throw new InvalidDataException("The MVID changed.");
        return new(bodies, ilBytes, tables, oldResources.Count, newResources.Count, after.GetGuid(after.GetModuleDefinition().Mvid).ToString(), "Every raw method body, non-resource metadata table, metadata heap, assembly/reference identity and retained resource was compared independently after editing.");
    }

    private static void AssertUnused(MetadataReader reader)
    {
        if (reader.AssemblyReferences.Any(handle => reader.GetString(reader.GetAssemblyReference(handle).Name) == RemovedAssembly))
            throw new InvalidDataException("DiagnosticSource is referenced; removal is not allowed.");
    }

    private static List<Resource> Resources(PEReader pe, MetadataReader reader)
    {
        int start = RvaOffset(pe, pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        byte[] image = pe.GetEntireImage().GetContent().ToArray();
        var result = new List<Resource>();
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
        {
            ManifestResource resource = reader.GetManifestResource(handle);
            if (!resource.Implementation.IsNil) throw new InvalidDataException("External resources are outside this transformation contract.");
            int offset = checked(start + (int)resource.Offset);
            int length = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset, 4));
            if (length < 0) throw new InvalidDataException("Invalid resource length.");
            result.Add(new(handle, reader.GetString(resource.Name), offset, image.AsSpan(offset + 4, length).ToArray()));
        }
        return result;
    }

    private static byte[] RemoveMetadataRow(byte[] bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        string prefix = RemovedResource + "|";
        int start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0 || (start != 0 && text[start - 1] != '\n') || text.IndexOf(prefix, start + prefix.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("The Costura metadata row is not unique.");
        int end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length - 1;
        return new UTF8Encoding(false).GetBytes(text.Remove(start, end - start + 1));
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        using var compressed = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        compressed.CopyTo(output);
        return output.ToArray();
    }

    private static int ResourceCountOffset(byte[] image, int root)
    {
        int cursor = checked(root + 16 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(root + 12, 4)));
        cursor = (cursor + 3) & ~3;
        int streams = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor + 2, 2));
        cursor += 4;
        for (int i = 0; i < streams; i++)
        {
            int relative = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(cursor, 4));
            int end = Array.IndexOf(image, (byte)0, cursor + 8);
            if (end < 0) throw new InvalidDataException("Invalid metadata stream header.");
            string name = Encoding.ASCII.GetString(image, cursor + 8, end - cursor - 8);
            cursor = (end + 4) & ~3;
            if (name is not "#~" and not "#-") continue;
            int tableStream = checked(root + relative);
            ulong valid = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(tableStream + 8, 8));
            int table = (int)TableIndex.ManifestResource;
            if ((valid & (1UL << table)) == 0) throw new InvalidDataException("The resource table is missing.");
            int preceding = System.Numerics.BitOperations.PopCount(valid & ((1UL << table) - 1));
            return tableStream + 24 + preceding * 4;
        }
        throw new InvalidDataException("The metadata table stream is missing.");
    }

    private static int RvaOffset(PEReader pe, int rva)
    {
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(section => rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData);
        return checked(section.PointerToRawData + rva - section.VirtualAddress);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Input(string Scope, string Name, string Target, string Sha256);
    private sealed record Resource(ManifestResourceHandle Handle, string Name, int FileOffset, byte[] Bytes);
    private sealed record Verification(int MethodBodies, int RawIlBytes, int UnchangedMetadataTables, int ResourcesBefore, int ResourcesAfter, string Mvid, string Scope);
}
