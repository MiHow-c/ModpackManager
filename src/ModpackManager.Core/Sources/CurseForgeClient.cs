using System.Net.Http.Json;

namespace ModpackManager.Core.Sources;

/// <summary>DownloadUrl is null when the author disallows third-party distribution; CF terms forbid caching it.</summary>
public sealed record CurseForgeFile(long Id, long ModId, long FileFingerprint, string? DownloadUrl, string FileName);

/// <summary>CurseForge API (https://docs.curseforge.com/rest-api/). Used only when CURSEFORGE_API_KEY is set.</summary>
public sealed class CurseForgeClient(HttpClient http, string apiKey)
{
    public const string BaseUrl = "https://api.curseforge.com/v1/";
    const int MinecraftGameId = 432;

    public static CurseForgeClient? FromEnvironment(HttpClient http) =>
        Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY") is { Length: > 0 } key ? new(http, key) : null;

    /// <summary>Exact fingerprint matches keyed by fingerprint.</summary>
    public async Task<Dictionary<long, CurseForgeFile>> MatchFingerprintsAsync(IReadOnlyCollection<uint> fingerprints, CancellationToken ct = default)
    {
        if (fingerprints.Count == 0) return [];
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}fingerprints/{MinecraftGameId}")
        {
            Content = JsonContent.Create(new { fingerprints }),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<FingerprintResponse>(ct);
        return body?.Data.ExactMatches.Select(m => m.File).DistinctBy(f => f.FileFingerprint).ToDictionary(f => f.FileFingerprint) ?? [];
    }

    /// <summary>File details, including the current download URL (null when third-party downloads are not allowed).</summary>
    public async Task<CurseForgeFile> GetFileAsync(long modId, long fileId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}mods/{modId}/files/{fileId}");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileResponse>(ct))?.Data ?? throw new HttpRequestException("Empty CurseForge response");
    }

    sealed record FileResponse(CurseForgeFile Data);

    sealed record FingerprintResponse(FingerprintData Data);

    sealed record FingerprintData(List<FingerprintMatch> ExactMatches);

    sealed record FingerprintMatch(long Id, CurseForgeFile File);
}
