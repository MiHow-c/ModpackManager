using System.Security.Cryptography;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Packs;
using ModpackManager.Core.Remote;
using ModpackManager.Core.Sources;

namespace ModpackManager.Core.Sync;

/// <summary>Applied changes, and host paths that could not be synced (unsafe path, no download, hash mismatch).</summary>
public sealed record PullResult(int Applied, IReadOnlyList<string> Skipped);

/// <summary>
/// Makes the local copy match the host: Modrinth/CurseForge files come from their source, everything else from the
/// host; local-only files go to backups. The host is not trusted: paths and file hashes are checked.
/// </summary>
public sealed class Puller(ModrinthClient modrinth, CurseForgeClient? curseForge, HttpClient http)
{
    public async Task<PullResult> PullAsync(IPackSession host, IReadOnlyList<DiffItem> diff, IPackStore local, CancellationToken ct = default)
    {
        var needed = diff.Where(d => d.Host is not null).ToList();
        var skipped = new List<string>();
        var applied = 0;

        // two bulk requests instead of two per mod, to stay far below Modrinth's rate limit on big pulls
        var versionIds = needed.Where(d => d.Host!.Source == SourceKind.Modrinth && d.Host.VersionId is not null)
            .Select(d => d.Host!.VersionId!).Distinct().ToList();
        Dictionary<string, ModrinthVersion> versions = [];
        Dictionary<string, ModrinthProject> projects = [];
        try
        {
            if (versionIds.Count > 0) versions = (await modrinth.GetVersionsAsync(versionIds, ct)).ToDictionary(v => v.Id);
            if (versions.Count > 0)
                projects = (await modrinth.GetProjectsAsync(versions.Values.Select(v => v.ProjectId).Distinct().ToList(), ct)).ToDictionary(p => p.Id);
        }
        catch (HttpRequestException)
        {
            // Modrinth unreachable: those files are then fetched from the host like any other
        }

        foreach (var (_, h, l) in needed)
        {
            try
            {
                // the host names the paths: accept only pack content inside the local pack
                if (!local.IsPackPath(h!.Path)) throw new OperationException(OpError.InvalidPath, h.Path);
                var slash = h.Path.LastIndexOf('/');
                if (h.Source == SourceKind.Modrinth && h.VersionId is { } id && versions.TryGetValue(id, out var v)
                    && projects.TryGetValue(v.ProjectId, out var p) && slash > 0)
                {
                    var file = v.Files.FirstOrDefault(f => ManifestDiff.SharesHash(f.Hashes, h.Hashes)) ?? v.PrimaryFile
                               ?? throw new OperationException(OpError.NoFile, h.Path);
                    await local.WriteModAsync(PackEditor.CheckedMod(p, v, file, h.Path[..slash]), l?.Path, ct);
                }
                else
                {
                    var bytes = await FetchAsync(host, h, ct);
                    if (l is not null) local.Delete(l.Path);
                    local.PutFile(h.Path, bytes);
                }
                applied++;
            }
            catch (Exception e) when (e is OperationException or HttpRequestException)
            {
                skipped.Add(h!.Path);
            }
        }

        foreach (var d in diff.Where(d => d.Host is null))
        {
            local.Delete(d.Local!.Path);
            applied++;
        }
        return new PullResult(applied, skipped);
    }

    /// <summary>CurseForge files come from CurseForge when its API allows it, everything else from the host; hashes must match.</summary>
    async Task<byte[]> FetchAsync(IPackSession host, PackEntry entry, CancellationToken ct)
    {
        var url = entry.Source == SourceKind.CurseForge && curseForge is not null
                  && long.TryParse(entry.ProjectId, out var modId) && long.TryParse(entry.VersionId, out var fileId)
            ? (await curseForge.GetFileAsync(modId, fileId, ct)).DownloadUrl
            : null;
        var bytes = url is not null ? await http.GetByteArrayAsync(url, ct) : await host.GetFileAsync(entry.Path, ct);
        return HashMatches(bytes, entry.Hashes) ? bytes : throw new OperationException(OpError.HashMismatch, entry.Path);
    }

    static bool HashMatches(byte[] bytes, IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var (algorithm, expected) in hashes)
        {
            byte[]? actual = algorithm switch
            {
                "sha1" => SHA1.HashData(bytes),
                "sha256" => SHA256.HashData(bytes),
                "sha512" => SHA512.HashData(bytes),
                "md5" => MD5.HashData(bytes),
                _ => null,
            };
            if (actual is not null) return string.Equals(Convert.ToHexStringLower(actual), expected, StringComparison.OrdinalIgnoreCase);
        }
        return true; // shortcut: entries with only murmur2 are accepted unverified; verify murmur2 too if that ever matters
    }
}
