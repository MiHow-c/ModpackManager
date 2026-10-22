using System.Security.Cryptography;
using System.Text.Json;

namespace ModpackManager.Core.Scanning;

public static class PackScanner
{
    // shortcut: fixed list of synced folders, extend it if packs start using other top-level dirs (e.g. global_packs)
    public static readonly string[] TrackedRoots = ["mods", "config", "defaultconfigs", "kubejs", "resourcepacks", "shaderpacks", "scripts"];

    /// <summary>Scans a packwiz pack (pack.toml present) or a plain instance folder.</summary>
    public static (PackInfo Info, List<PackEntry> Entries) Scan(string root) =>
        File.Exists(Path.Combine(root, "pack.toml")) ? PackwizReader.Read(root) : ScanFolder(root);

    static (PackInfo, List<PackEntry>) ScanFolder(string root)
    {
        var entries = TrackedRoots
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Select(file => FileEntry(root, file))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
        return (FolderInfo(root), entries);
    }

    public static PackEntry FileEntry(string root, string file)
    {
        var path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
        var kind = KindOf(path);
        var mod = kind == EntryKind.Mod && file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? JarMetadataReader.Read(file) : null;
        return new PackEntry(path, kind, HashFile(file), new FileInfo(file).Length, mod?.Name ?? Path.GetFileName(file), Mod: mod);
    }

    public static EntryKind KindOf(string path) => path[..Math.Max(0, path.IndexOf('/'))] switch
    {
        "mods" => EntryKind.Mod,
        "resourcepacks" => EntryKind.ResourcePack,
        "shaderpacks" => EntryKind.ShaderPack,
        "config" or "defaultconfigs" => EntryKind.Config,
        _ => EntryKind.Other,
    };

    /// <summary>A pack-relative path that cannot escape the pack directory (no rooted paths, '..', '\' or ':').</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(s => s is not ("" or "." or ".."));

    /// <summary>SHA-1 (Modrinth lookup), SHA-256 (packwiz index) and SHA-512 (Modrinth/packwiz downloads) in one read.</summary>
    public static Dictionary<string, string> HashFile(string file)
    {
        using var stream = File.OpenRead(file);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);
        }
        return new()
        {
            ["sha1"] = Convert.ToHexStringLower(sha1.GetHashAndReset()),
            ["sha256"] = Convert.ToHexStringLower(sha256.GetHashAndReset()),
            ["sha512"] = Convert.ToHexStringLower(sha512.GetHashAndReset()),
        };
    }

    static PackInfo FolderInfo(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // Prism/MultiMC keep mmc-pack.json in the instance dir, one level above the game folder
        var instanceDir = new[] { full, Path.GetDirectoryName(full) }
            .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, "mmc-pack.json")));
        if (instanceDir is null) return new PackInfo(Path.GetFileName(full), PackFormat.Folder, null, null, null);

        string? mc = null, loaderVersion = null;
        Loader? loader = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(instanceDir, "mmc-pack.json")));
            foreach (var c in doc.RootElement.GetProperty("components").EnumerateArray())
            {
                var version = c.TryGetProperty("version", out var v) ? v.GetString() : null;
                switch (c.TryGetProperty("uid", out var uid) ? uid.GetString() : null)
                {
                    case "net.minecraft": mc = version; break;
                    case "net.neoforged": (loader, loaderVersion) = (Loader.NeoForge, version); break;
                    case "net.minecraftforge": (loader, loaderVersion) = (Loader.Forge, version); break;
                    case "net.fabricmc.fabric-loader": (loader, loaderVersion) = (Loader.Fabric, version); break;
                    case "org.quiltmc.quilt-loader": (loader, loaderVersion) = (Loader.Quilt, version); break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // unreadable mmc-pack.json: fall back to manual MC/loader selection
        }
        return new PackInfo(Path.GetFileName(instanceDir), PackFormat.Folder, mc, loader, loaderVersion);
    }
}
