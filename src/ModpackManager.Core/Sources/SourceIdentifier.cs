using System.Globalization;
using System.Text.Json;

namespace ModpackManager.Core.Sources;

public static class SourceLinks
{
    // both sites redirect id-based URLs to the project page, so no extra request for slugs is needed
    public static string Modrinth(string projectId) => $"https://modrinth.com/project/{projectId}";

    public static string CurseForge(string projectId) => $"https://www.curseforge.com/projects/{projectId}";
}

/// <summary>Positive lookup results keyed by SHA-1. Misses are not cached so unknown files get re-checked on the next scan.</summary>
public sealed class LookupCache
{
    public Dictionary<string, CachedSource> Entries { get; set; } = [];
}

public sealed record CachedSource(SourceKind Source, string ProjectId, string? VersionId);

public sealed record IdentifyResult(List<PackEntry> Entries, IReadOnlyList<SourceKind> FailedSources);

/// <summary>Order: cache, Modrinth by hash, CurseForge by fingerprint (with API key), manual link, URL from jar metadata, unknown.</summary>
public sealed class SourceIdentifier(ModrinthClient modrinth, CurseForgeClient? curseForge, AppPaths paths)
{
    public async Task<IdentifyResult> IdentifyAsync(string packRoot, IReadOnlyList<PackEntry> entries, CancellationToken ct = default)
    {
        var cache = JsonFile.Read<LookupCache>(paths.LookupCache);
        var manualLinks = JsonFile.Read<PackSettings>(paths.PackSettings(packRoot)).ManualLinks;
        var result = entries.ToList();
        var failed = new List<SourceKind>();
        var cacheChanged = false;

        var pending = Enumerable.Range(0, result.Count).Where(i => NeedsLookup(result[i])).ToList();
        foreach (var i in pending)
            if (cache.Entries.TryGetValue(result[i].Sha1!, out var hit))
                result[i] = Apply(result[i], hit);
        Prune();

        if (pending.Count > 0)
        {
            try
            {
                var found = await modrinth.GetVersionsByHashesAsync(pending.Select(i => result[i].Sha1!).Distinct().ToList(), ct);
                foreach (var i in pending)
                    if (found.TryGetValue(result[i].Sha1!, out var v))
                        Remember(i, new CachedSource(SourceKind.Modrinth, v.ProjectId, v.Id));
            }
            catch (Exception e) when (IsNetworkFailure(e, ct)) { failed.Add(SourceKind.Modrinth); }
            Prune();
        }

        if (curseForge is not null && pending.Count > 0)
        {
            try
            {
                var fingerprints = pending.ToDictionary(i => i, i => Murmur2.CurseForgeFingerprint(Path.Combine(packRoot, result[i].Path)));
                var found = await curseForge.MatchFingerprintsAsync(fingerprints.Values.Distinct().ToList(), ct);
                foreach (var (i, fingerprint) in fingerprints)
                    if (found.TryGetValue(fingerprint, out var f))
                        Remember(i, new CachedSource(SourceKind.CurseForge,
                            f.ModId.ToString(CultureInfo.InvariantCulture), f.Id.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (IsNetworkFailure(e, ct)) { failed.Add(SourceKind.CurseForge); }
            Prune();
        }

        // a link the user assigned by hand wins over one guessed from jar metadata
        foreach (var i in pending)
        {
            var e = result[i];
            if (manualLinks.TryGetValue(e.Sha1!, out var link)) result[i] = e with { Source = SourceKind.Manual, Url = link };
            else if (e.Mod?.Url is { } url) result[i] = e with { Source = SourceKind.Url, Url = url };
        }

        if (cacheChanged) JsonFile.Write(paths.LookupCache, cache);
        return new IdentifyResult(result, failed);

        void Prune() => pending.RemoveAll(i => result[i].Source != SourceKind.Unknown);

        void Remember(int i, CachedSource source)
        {
            result[i] = Apply(result[i], source);
            cache.Entries[result[i].Sha1!] = source;
            cacheChanged = true;
        }
    }

    /// <summary>Stores a manual link for a file (by SHA-1) in the pack's local settings.</summary>
    public void SetManualLink(string packRoot, string sha1, string url)
    {
        var file = paths.PackSettings(packRoot);
        var settings = JsonFile.Read<PackSettings>(file);
        settings.ManualLinks[sha1] = url;
        JsonFile.Write(file, settings);
    }

    static bool NeedsLookup(PackEntry e) =>
        e.Source == SourceKind.Unknown && e.Sha1 is not null
        && e.Kind is EntryKind.Mod or EntryKind.ResourcePack or EntryKind.ShaderPack
        && (e.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    static PackEntry Apply(PackEntry e, CachedSource s) => e with
    {
        Source = s.Source,
        ProjectId = s.ProjectId,
        VersionId = s.VersionId,
        Url = s.Source == SourceKind.Modrinth ? SourceLinks.Modrinth(s.ProjectId) : SourceLinks.CurseForge(s.ProjectId),
    };

    static bool IsNetworkFailure(Exception e, CancellationToken ct) =>
        e is HttpRequestException or JsonException || (e is OperationCanceledException && !ct.IsCancellationRequested);
}
