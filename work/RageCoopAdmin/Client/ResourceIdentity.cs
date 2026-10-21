using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RageCoop.Client.Scripting;

namespace RageCoopAdmin.Client;

// The host derives DataFolder from the downloaded archive's filename. Content
// addressing must change only the archive identity, never the player's profile.
internal static class ResourceIdentity
{
    private const string Name = "CoopControl";
    private const int HashLength = 64;
    private static readonly PropertyInfo DataFolder = typeof(ClientResource).GetProperty(nameof(ClientResource.DataFolder)) ??
        throw new InvalidOperationException("The installed RageCoop resource data-folder contract is unavailable.");

    internal static void Normalize(ClientResource resource)
    {
        string parent = DataParent(resource);
        string canonical = Path.Combine(parent, Name);
        if (DataFolder.PropertyType != typeof(string) || DataFolder.GetSetMethod(true) == null)
            throw new InvalidOperationException("The installed RageCoop resource data folder cannot be assigned.");
        Directory.CreateDirectory(canonical);
        DataFolder.SetValue(resource, canonical);
    }

    // Called only after the resource has built its menus and subscribed its
    // handlers successfully. These archives are disposable download cache;
    // no settings file or nonempty data directory is removed.
    internal static void CleanupObsolete(ClientResource resource, Action<string> warning)
    {
        try
        {
            string parent = DataParent(resource);
            if (!string.Equals(Path.GetFullPath(resource.DataFolder), Path.Combine(parent, Name), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The resource data folder has not been normalized.");
            string root = Path.GetDirectoryName(parent) ?? throw new InvalidOperationException("The resource cache folder is unavailable.");
            string current = resource.Name + ".res";
            foreach (string path in Directory.EnumerateFiles(root, Name + ".*.res", SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileName(path);
                if (filename.Length != Name.Length + 1 + HashLength + 4 ||
                    !filename.EndsWith(".res", StringComparison.Ordinal) ||
                    !IsHashedName(Path.GetFileNameWithoutExtension(filename)) ||
                    string.Equals(filename, current, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(path);
            }
            if (IsHashedName(resource.Name))
            {
                string created = Path.Combine(parent, resource.Name);
                if (Directory.Exists(created) && (File.GetAttributes(created) & FileAttributes.ReparsePoint) == 0 &&
                    !Directory.EnumerateFileSystemEntries(created).Any()) Directory.Delete(created, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
        {
            warning("Coop Control: obsolete download cache could not be cleaned: " + ex.GetType().Name);
        }
    }

    private static string DataParent(ClientResource resource)
    {
        if (resource == null || (resource.Name != Name && !IsHashedName(resource.Name)) ||
            string.IsNullOrWhiteSpace(resource.DataFolder) || !Path.IsPathRooted(resource.DataFolder))
            throw new InvalidOperationException("Unsupported CoopControl resource identity.");
        string directory = Path.GetFullPath(resource.DataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(directory);
        string? parent = Path.GetDirectoryName(directory);
        if ((leaf != Name && !string.Equals(leaf, resource.Name, StringComparison.Ordinal)) ||
            parent == null || !string.Equals(Path.GetFileName(parent), "data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsupported CoopControl resource data-folder layout.");
        return parent;
    }

    private static bool IsHashedName(string? name) => name != null && name.Length == Name.Length + 1 + HashLength &&
        name.StartsWith(Name + ".", StringComparison.Ordinal) && name.Skip(Name.Length + 1).All(character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'));
}
