namespace ModpackManager.Core;

public enum PackFormat { Folder, Packwiz }

public enum Loader { NeoForge, Forge, Fabric, Quilt }

public enum EntryKind { Mod, ResourcePack, ShaderPack, Config, Other }

public enum SourceKind { Unknown, Modrinth, CurseForge, Url, Manual }

public sealed record PackInfo(string Name, PackFormat Format, string? McVersion, Loader? Loader, string? LoaderVersion);

/// <summary>Metadata read from a jar (mods.toml / fabric.mod.json / quilt.mod.json).</summary>
public sealed record ModInfo(string ModId, string Name, string Version, IReadOnlyList<Loader> Loaders, string? McVersion, string? Url);

/// <summary>
/// One file of a pack. Path is relative, '/'-separated and points to where the file lives in the game
/// directory (for packwiz: mods/x.jar, not the .pw.toml).
/// </summary>
public sealed record PackEntry(
    string Path,
    EntryKind Kind,
    IReadOnlyDictionary<string, string> Hashes,
    long? Size,
    string Name,
    SourceKind Source = SourceKind.Unknown,
    string? ProjectId = null,
    string? VersionId = null,
    string? Url = null,
    ModInfo? Mod = null)
{
    public string? Sha1 => Hashes.GetValueOrDefault("sha1");
}

public sealed record PackManifest(long Revision, PackInfo Info, IReadOnlyList<PackEntry> Entries);
