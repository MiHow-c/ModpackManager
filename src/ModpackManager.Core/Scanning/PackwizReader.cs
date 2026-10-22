using System.Globalization;
using ModpackManager.Core.Sources;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Core.Scanning;

/// <summary>Reads pack.toml + index.toml + *.pw.toml without downloading anything.</summary>
public static class PackwizReader
{
    static readonly (string Key, Loader Loader)[] LoaderKeys =
        [("neoforge", Loader.NeoForge), ("forge", Loader.Forge), ("fabric", Loader.Fabric), ("quilt", Loader.Quilt)];

    public static (PackInfo Info, List<PackEntry> Entries) Read(string root)
    {
        var pack = ReadToml(Path.Combine(root, "pack.toml"));
        var versions = pack.TryGetValue("versions", out var v) && v is TomlTable vt ? vt : new TomlTable();
        var (loader, loaderVersion) = LoaderKeys
            .Where(k => Str(versions, k.Key) is not null)
            .Select(k => ((Loader?)k.Loader, Str(versions, k.Key)))
            .FirstOrDefault();
        var info = new PackInfo(Str(pack, "name") ?? Path.GetFileName(Path.GetFullPath(root)), PackFormat.Packwiz,
            Str(versions, "minecraft"), loader, loaderVersion);

        var indexFile = (pack.TryGetValue("index", out var i) && i is TomlTable it ? Str(it, "file") : null) ?? "index.toml";
        if (!PackScanner.IsSafeRelativePath(indexFile)) throw new InvalidDataException($"Unsafe index path: {indexFile}");
        var indexPath = Path.Combine(root, indexFile);
        var indexDir = Path.GetDirectoryName(indexPath)!;
        var index = ReadToml(indexPath);

        var entries = new List<PackEntry>();
        foreach (var f in index.TryGetValue("files", out var files) && files is TomlTableArray arr ? arr : [])
        {
            // paths come from a file anyone could have edited: never let them point outside the pack
            if (Str(f, "file") is not { } file || !PackScanner.IsSafeRelativePath(file)) continue;
            var full = Path.Combine(indexDir, file);
            if (!File.Exists(full)) continue;
            var entry = f.TryGetValue("metafile", out var meta) && meta is true
                ? FromMetafile(file, full)
                : PackScanner.FileEntry(indexDir, full);
            if (entry is not null) entries.Add(entry);
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (info, entries);
    }

    static PackEntry? FromMetafile(string metaPath, string fullPath)
    {
        var meta = ReadToml(fullPath);
        if (Str(meta, "filename") is not { } filename) return null;
        var slash = metaPath.LastIndexOf('/');
        var path = (slash < 0 ? "" : metaPath[..(slash + 1)]) + filename;
        if (!PackScanner.IsSafeRelativePath(path)) return null;

        var download = meta.TryGetValue("download", out var d) && d is TomlTable dt ? dt : new TomlTable();
        var hashes = new Dictionary<string, string>();
        if (Str(download, "hash-format") is { } format && Id(download, "hash") is { } hash)
            hashes[format.ToLowerInvariant()] = hash.ToLowerInvariant();
        var url = Str(download, "url");

        var update = meta.TryGetValue("update", out var u) && u is TomlTable ut ? ut : new TomlTable();
        var (source, projectId, versionId, link) =
            update.TryGetValue("modrinth", out var m) && m is TomlTable mr && Id(mr, "mod-id") is { } mrId
                ? (SourceKind.Modrinth, mrId, Id(mr, "version"), SourceLinks.Modrinth(mrId))
            : update.TryGetValue("curseforge", out var c) && c is TomlTable cf && Id(cf, "project-id") is { } cfId
                ? (SourceKind.CurseForge, cfId, Id(cf, "file-id"), SourceLinks.CurseForge(cfId))
            : url is not null
                ? (SourceKind.Url, (string?)null, (string?)null, url)
                : (SourceKind.Unknown, null, null, null);

        return new PackEntry(path, PackScanner.KindOf(path), hashes, null, Str(meta, "name") ?? filename,
            source, projectId, versionId, link);
    }

    static TomlTable ReadToml(string path) => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;

    static string? Str(TomlTable table, string key) => table.TryGetValue(key, out var v) ? v as string : null;

    /// <summary>Ids are strings for Modrinth and integers for CurseForge.</summary>
    static string? Id(TomlTable table, string key) => table.TryGetValue(key, out var v)
        ? v switch { string s => s, long l => l.ToString(CultureInfo.InvariantCulture), _ => null }
        : null;
}
