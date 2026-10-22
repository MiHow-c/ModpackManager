using System.IO.Compression;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Core.Scanning;

public static class JarMetadataReader
{
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Reads loader metadata from a jar; null when the jar has none or is not a valid zip.</summary>
    public static ModInfo? Read(string jarPath)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(jarPath); }
        catch (InvalidDataException) { return null; }

        using (zip)
        {
            ModInfo?[] found =
            [
                Try(() => FromModsToml(ReadText(zip, "META-INF/neoforge.mods.toml"), zip, isNeoForgeFile: true)),
                Try(() => FromModsToml(ReadText(zip, "META-INF/mods.toml"), zip, isNeoForgeFile: false)),
                Try(() => FromFabric(ReadText(zip, "fabric.mod.json"))),
                Try(() => FromQuilt(ReadText(zip, "quilt.mod.json"))),
            ];
            var infos = found.OfType<ModInfo>().ToList();
            // multi-loader jars: identity from the first descriptor, loaders from all of them
            return infos.Count == 0 ? null : infos[0] with { Loaders = infos.SelectMany(i => i.Loaders).Distinct().ToList() };
        }
    }

    static ModInfo? Try(Func<ModInfo?> parse)
    {
        try { return parse(); }
        catch (Exception e) when (e is TomlException or JsonException or InvalidCastException or InvalidDataException) { return null; }
    }

    static string? ReadText(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    static ModInfo? FromModsToml(string? text, ZipArchive zip, bool isNeoForgeFile)
    {
        if (text is null) return null;
        var toml = TomlSerializer.Deserialize<TomlTable>(text)!;
        if (!toml.TryGetValue("mods", out var mods) || Tables(mods).FirstOrDefault() is not { } mod || Str(mod, "modId") is not { } id)
            return null;

        var version = Str(mod, "version") ?? "";
        if (version.Contains("${file.jarVersion}")) version = ManifestVersion(zip) ?? version;

        var deps = toml.TryGetValue("dependencies", out var d) && d is TomlTable depTable
            ? depTable.SelectMany(kv => Tables(kv.Value)).ToList()
            : [];
        var mc = deps.FirstOrDefault(x => Str(x, "modId") == "minecraft") is { } mcDep ? Str(mcDep, "versionRange") : null;
        // NeoForge 1.20.1-1.20.4 still used META-INF/mods.toml; the neoforge dependency tells them apart
        var loader = isNeoForgeFile || deps.Any(x => Str(x, "modId") == "neoforge") ? Loader.NeoForge : Loader.Forge;

        return new ModInfo(id, Str(mod, "displayName") ?? id, version, [loader], mc, Str(mod, "displayURL") ?? Str(toml, "displayURL"));
    }

    static IEnumerable<TomlTable> Tables(object? value) => value switch
    {
        TomlTableArray array => array,
        TomlArray array => array.OfType<TomlTable>(),
        _ => [],
    };

    static string? Str(TomlTable table, string key) => table.TryGetValue(key, out var v) ? v as string : null;

    static string? ManifestVersion(ZipArchive zip)
    {
        const string key = "Implementation-Version:";
        return ReadText(zip, "META-INF/MANIFEST.MF")?
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?[key.Length..].Trim();
    }

    static ModInfo? FromFabric(string? text)
    {
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text, Lenient);
        var root = doc.RootElement;
        if (Get(root, "id") is not { } id) return null;
        var mc = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("depends", out var deps)
                 && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty("minecraft", out var v)
            ? Range(v)
            : null;
        return new ModInfo(id, Get(root, "name") ?? id, Get(root, "version") ?? "", [Loader.Fabric], mc, ContactUrl(root));
    }

    static ModInfo? FromQuilt(string? text)
    {
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text, Lenient);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("quilt_loader", out var q)
            || Get(q, "id") is not { } id)
            return null;

        q.TryGetProperty("metadata", out var meta);
        string? mc = null;
        if (q.TryGetProperty("depends", out var deps) && deps.ValueKind == JsonValueKind.Array)
            foreach (var dep in deps.EnumerateArray())
                if (Get(dep, "id") == "minecraft" && dep.TryGetProperty("versions", out var v))
                    mc = Range(v);

        return new ModInfo(id, Get(meta, "name") ?? id, Get(q, "version") ?? "", [Loader.Quilt], mc, ContactUrl(meta));
    }

    static string? Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? ContactUrl(JsonElement owner) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("contact", out var c) ? Get(c, "homepage") ?? Get(c, "sources") : null;

    static string? Range(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Array => string.Join(" || ", v.EnumerateArray().Select(x => x.ToString())),
        JsonValueKind.Object => v.GetRawText(),
        _ => null,
    };
}
