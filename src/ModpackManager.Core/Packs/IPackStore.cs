using ModpackManager.Core.Sources;

namespace ModpackManager.Core.Packs;

/// <summary>A Modrinth project file resolved from ids, ready to be written into <see cref="Folder"/> of a pack.</summary>
public sealed record ResolvedMod(ModrinthProject Project, ModrinthVersion Version, ModrinthFile File, string Folder);

/// <summary>Pack format: plain instance folder or packwiz. Paths are pack-relative and already validated by the caller.</summary>
public interface IPackStore
{
    string Root { get; }

    (PackInfo Info, List<PackEntry> Entries) Scan();

    /// <summary>Current entry at a path (unidentified), or null.</summary>
    PackEntry? Find(string path);

    /// <summary>Whether the path is pack content that may be read, written or deleted (inside the pack, not format metadata).</summary>
    bool IsPackPath(string path);

    /// <summary>Writes the mod, removing the entry at <paramref name="replacing"/> if given; returns the new path.</summary>
    Task<string> WriteModAsync(ResolvedMod mod, string? replacing, CancellationToken ct);

    void PutFile(string path, byte[] content);

    void Delete(string path);

    void SetVersions(string mcVersion, Loader loader, string? loaderVersion);
}
