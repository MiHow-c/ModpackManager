using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModpackManager.Core.Scanning;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Core.Packs;

/// <summary>
/// packwiz pack: mods are .pw.toml metafiles, every other file is listed in index.toml with its hash.
/// Every change ends with <see cref="Refresh"/>, the native equivalent of `packwiz refresh`.
/// </summary>
public sealed class PackwizStore(string root, AppPaths paths) : IPackStore
{
    // same defaults as packwiz (core/index.go ignoreDefaults)
    static readonly string[] DefaultIgnores = [".git/**", ".gitattributes", ".gitignore", ".DS_Store", "/*.zip", "*.mrpack", "packwiz.exe", "packwiz"];

    readonly Backups _backups = new(paths, root);

    public string Root => root;

    // shortcut: writing supports only an index in the pack root (packwiz's default); index paths are then root-relative
    string IndexFile => (ReadToml("pack.toml").TryGetValue("index", out var i) && i is TomlTable t ? t["file"] as string : null) is { } file
        ? file.Contains('/') || file.Contains('\\') ? throw new NotSupportedException($"index outside the pack root: {file}") : file
        : "index.toml";

    public (PackInfo Info, List<PackEntry> Entries) Scan() => PackwizReader.Read(root);

    public PackEntry? Find(string path) => Scan().Entries.FirstOrDefault(e => e.Path == path);

    public bool IsPackPath(string path) =>
        PackScanner.IsSafeRelativePath(path)
        && !path.EndsWith(".pw.toml", StringComparison.OrdinalIgnoreCase) // mods change only through mod operations
        && path is not ("pack.toml" or ".packwizignore") && path != IndexFile
        && !IsIgnored(path, IgnorePatterns());

    public Task<string> WriteModAsync(ResolvedMod mod, string? replacing, CancellationToken ct)
    {
        if (replacing is not null) _backups.Move(MetafileFor(replacing) ?? replacing); // the old version may be a metafile or a plain jar

        // like packwiz: client_only mods are client side, dedicated-server-only ones server side, the rest both
        var side = mod.Version.Environment switch { "client_only" => "client", "dedicated_server_only" => "server", _ => "both" };
        var (format, hash) = mod.File.Hashes.TryGetValue("sha512", out var sha512) ? ("sha512", sha512) : ("sha1", mod.File.Hashes["sha1"]);
        var meta = new TomlTable
        {
            ["name"] = mod.Project.Title,
            ["filename"] = mod.File.Filename,
            ["side"] = side,
            ["download"] = new TomlTable { ["url"] = mod.File.Url, ["hash-format"] = format, ["hash"] = hash },
            ["update"] = new TomlTable { ["modrinth"] = new TomlTable { ["mod-id"] = mod.Project.Id, ["version"] = mod.Version.Id } },
        };
        var slug = PackScanner.IsSafeRelativePath(mod.Project.Slug) && !mod.Project.Slug.Contains('/') ? mod.Project.Slug : mod.Project.Id;
        _backups.Write($"{mod.Folder}/{slug}.pw.toml", Encoding.UTF8.GetBytes(TomlSerializer.Serialize(meta)));
        Refresh();
        return Task.FromResult($"{mod.Folder}/{mod.File.Filename}");
    }

    public void PutFile(string path, byte[] content)
    {
        _backups.Write(path, content);
        Refresh();
    }

    public void Delete(string path)
    {
        _backups.Move(MetafileFor(path) ?? path);
        Refresh();
    }

    public void SetVersions(string mcVersion, Loader loader, string? loaderVersion)
    {
        var pack = ReadToml("pack.toml");
        var versions = new TomlTable { ["minecraft"] = mcVersion };
        if (loaderVersion is { Length: > 0 }) versions[loader.ToString().ToLowerInvariant()] = loaderVersion;
        pack["versions"] = versions;
        _backups.Write("pack.toml", Encoding.UTF8.GetBytes(TomlSerializer.Serialize(pack)));
    }

    /// <summary>
    /// Re-hashes (sha256) every non-ignored file in the pack, adds new files, drops missing ones,
    /// then stores the hash of index.toml in pack.toml.
    /// </summary>
    public void Refresh()
    {
        var indexFile = IndexFile;
        var oldIndex = ReadToml(indexFile);
        var old = (oldIndex.TryGetValue("files", out var f) && f is TomlTableArray a ? a : [])
            .Where(t => t["file"] is string)
            .GroupBy(t => (string)t["file"])
            .ToDictionary(g => g.Key, g => g.First());

        var ignore = IgnorePatterns();
        var files = new TomlTableArray();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
                     .Where(p => p is not (".packwizignore" or "pack.toml") && p != indexFile && !IsIgnored(p, ignore))
                     .Order(StringComparer.Ordinal))
        {
            var entry = new TomlTable { ["file"] = path, ["hash"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))) };
            if (path.EndsWith(".pw.toml", StringComparison.Ordinal)) entry["metafile"] = true;
            // keep per-file flags a pack author may have set
            if (old.TryGetValue(path, out var prev))
                foreach (var key in new[] { "alias", "preserve" })
                    if (prev.TryGetValue(key, out var value)) entry[key] = value;
            files.Add(entry);
        }

        var indexBytes = Encoding.UTF8.GetBytes(TomlSerializer.Serialize(new TomlTable { ["hash-format"] = "sha256", ["files"] = files }));
        _backups.Write(indexFile, indexBytes);

        var pack = ReadToml("pack.toml");
        pack["index"] = new TomlTable { ["file"] = indexFile, ["hash-format"] = "sha256", ["hash"] = Convert.ToHexStringLower(SHA256.HashData(indexBytes)) };
        _backups.Write("pack.toml", Encoding.UTF8.GetBytes(TomlSerializer.Serialize(pack)));
    }

    /// <summary>The .pw.toml whose target file is <paramref name="path"/>, or null when the path is a plain file.</summary>
    string? MetafileFor(string path)
    {
        var index = ReadToml(IndexFile);
        foreach (var t in index.TryGetValue("files", out var f) && f is TomlTableArray a ? a : [])
        {
            if (t["file"] is not string meta || !(t.TryGetValue("metafile", out var m) && m is true) || !PackScanner.IsSafeRelativePath(meta)) continue;
            var slash = meta.LastIndexOf('/');
            if (File.Exists(Path.Combine(root, meta)) && (slash < 0 ? "" : meta[..(slash + 1)]) + ReadToml(meta)["filename"] == path) return meta;
        }
        return null;
    }

    TomlTable ReadToml(string relPath) => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(Path.Combine(root, relPath)))!;

    List<(Regex Pattern, bool Negated)> IgnorePatterns()
    {
        var file = Path.Combine(root, ".packwizignore");
        var lines = DefaultIgnores.Concat(File.Exists(file) ? File.ReadAllLines(file) : []);
        return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.StartsWith('!') ? (GitignoreRegex(l[1..]), true) : (GitignoreRegex(l), false))
            .ToList();
    }

    /// <summary>gitignore semantics: the last matching pattern wins, '!' re-includes.</summary>
    static bool IsIgnored(string path, List<(Regex Pattern, bool Negated)> patterns)
    {
        var ignored = false;
        foreach (var (pattern, negated) in patterns)
            if (pattern.IsMatch(path)) ignored = !negated;
        return ignored;
    }

    // shortcut: covers *, ?, **, leading/trailing '/' and '!'; no [] classes or '\' escapes, add them if a pack needs it
    static Regex GitignoreRegex(string pattern)
    {
        var dirOnly = pattern.EndsWith('/');
        pattern = pattern.TrimEnd('/');
        var anchored = pattern.Contains('/'); // a slash anywhere but the end anchors the pattern to the pack root
        var body = Regex.Escape(pattern.TrimStart('/'))
            .Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        // a match on a directory also ignores everything inside it
        return new Regex((anchored ? "^" : "^(.*/)?") + body + (dirOnly ? "/" : "(/|$)"), RegexOptions.CultureInvariant);
    }
}
