namespace ModpackManager.Core.Sync;

public enum DiffKind { Missing, Extra, DifferentVersion, ConfigChanged }

/// <summary>Missing: only on the host. Extra: only local. Otherwise both sides are set.</summary>
public sealed record DiffItem(DiffKind Kind, PackEntry? Host, PackEntry? Local)
{
    public string Path => (Host ?? Local)!.Path;
}

public static class ManifestDiff
{
    /// <summary>
    /// Pairs files by (source, project id), then by any shared hash, then by path, so a packwiz pack (metadata and
    /// download hashes) compares cleanly with an instance folder (real jars).
    /// </summary>
    public static List<DiffItem> Compare(IReadOnlyList<PackEntry> host, IReadOnlyList<PackEntry> local)
    {
        var result = new List<DiffItem>();
        var localLeft = local.ToList();
        var hostLeft = new List<PackEntry>();

        foreach (var h in host)
        {
            var l = h is { Source: SourceKind.Modrinth or SourceKind.CurseForge, ProjectId: not null }
                ? localLeft.FirstOrDefault(x => x.Source == h.Source && x.ProjectId == h.ProjectId)
                : null;
            if (l is null)
            {
                hostLeft.Add(h);
                continue;
            }
            localLeft.Remove(l);
            if (l.VersionId != h.VersionId && !SharesHash(h.Hashes, l.Hashes)) result.Add(new DiffItem(DiffKind.DifferentVersion, h, l));
        }

        foreach (var h in hostLeft.ToList())
            if (localLeft.FirstOrDefault(x => SharesHash(h.Hashes, x.Hashes)) is { } l)
            {
                localLeft.Remove(l);
                hostLeft.Remove(h);
            }

        foreach (var h in hostLeft.ToList())
            if (localLeft.FirstOrDefault(x => x.Path == h.Path) is { } l)
            {
                localLeft.Remove(l);
                hostLeft.Remove(h);
                var modLike = h.Kind is EntryKind.Mod or EntryKind.ResourcePack or EntryKind.ShaderPack;
                result.Add(new DiffItem(modLike ? DiffKind.DifferentVersion : DiffKind.ConfigChanged, h, l));
            }

        result.AddRange(hostLeft.Select(h => new DiffItem(DiffKind.Missing, h, null)));
        result.AddRange(localLeft.Select(l => new DiffItem(DiffKind.Extra, null, l)));
        return result.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
    }

    public static bool SharesHash(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Any(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
}
