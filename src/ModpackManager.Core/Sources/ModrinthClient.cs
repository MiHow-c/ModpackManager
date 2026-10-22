using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ModpackManager.Core.Ops;

namespace ModpackManager.Core.Sources;

public sealed record ModrinthFile(Dictionary<string, string> Hashes, string Url, string Filename, bool Primary, long Size);

public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string VersionNumber,
    List<ModrinthFile> Files,
    List<string>? Loaders = null,
    List<string>? GameVersions = null,
    string? Environment = null,
    DateTimeOffset DatePublished = default)
{
    public ModrinthFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

/// <summary>ClientSide/ServerSide: required, optional, unsupported or unknown.</summary>
public sealed record ModrinthProject(
    string Id, string Slug, string Title, string ProjectType,
    List<string>? Categories = null, string? ClientSide = null, string? ServerSide = null);

public sealed record ModrinthSearchHit(string ProjectId, string Slug, string Title, string? Description, string? Author, long Downloads);

/// <summary>Modrinth API v2 (https://docs.modrinth.com/api/). No token needed for reads; 300 requests/min per IP.</summary>
public sealed class ModrinthClient(HttpClient http)
{
    public const string BaseUrl = "https://api.modrinth.com/v2/";

    // Modrinth asks for a User-Agent that identifies the application, not just the HTTP library
    public static readonly string UserAgent =
        $"ModpackManager/{typeof(ModrinthClient).Assembly.GetName().Version?.ToString(3)} (Minecraft modpack manager)";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Looks up all hashes in one request; hashes without a match are absent from the result.</summary>
    public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyCollection<string> sha1s, CancellationToken ct = default)
    {
        if (sha1s.Count == 0) return [];
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "version_files")
        {
            Content = JsonContent.Create(new { hashes = sha1s, algorithm = "sha1" }),
        }, ct);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, ModrinthVersion>>(Json, ct) ?? [];
    }

    /// <summary>Mods compatible with the pack's MC version and loader (Quilt packs also see Fabric mods).</summary>
    public async Task<List<ModrinthSearchHit>> SearchModsAsync(string query, string? mcVersion, Loader? loader, CancellationToken ct = default)
    {
        List<string[]> facets = [["project_type:mod"]];
        if (mcVersion is not null) facets.Add([$"versions:{mcVersion}"]);
        if (loader is { } l) facets.Add(l == Loader.Quilt ? ["categories:quilt", "categories:fabric"] : [$"categories:{LoaderName(l)}"]);
        var url = $"search?limit=30&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        return (await GetAsync<SearchResponse>(url, ct)).Hits;
    }

    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(string projectId, string? mcVersion, Loader? loader, CancellationToken ct = default)
    {
        var url = $"project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false";
        if (loader is { } l)
            url += "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(l == Loader.Quilt ? new[] { "quilt", "fabric" } : [LoaderName(l)]));
        if (mcVersion is not null) url += "&game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion }));
        return await GetAsync<List<ModrinthVersion>>(url, ct);
    }

    public Task<List<ModrinthVersion>> GetVersionsAsync(IReadOnlyCollection<string> versionIds, CancellationToken ct = default) =>
        GetAsync<List<ModrinthVersion>>($"versions?ids={Uri.EscapeDataString(JsonSerializer.Serialize(versionIds))}", ct);

    public Task<List<ModrinthProject>> GetProjectsAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct = default) =>
        GetAsync<List<ModrinthProject>>($"projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(projectIds))}", ct);

    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        GetAsync<ModrinthVersion>($"version/{Uri.EscapeDataString(versionId)}", ct);

    public Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        GetAsync<ModrinthProject>($"project/{Uri.EscapeDataString(projectId)}", ct);

    /// <summary>Downloads a version file and verifies it against the hash Modrinth published.</summary>
    public async Task<byte[]> DownloadAsync(ModrinthFile file, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new OperationException(OpError.NoFile, file.Url);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var ok = file.Hashes.TryGetValue("sha512", out var sha512)
            ? Convert.ToHexStringLower(SHA512.HashData(bytes)) == sha512.ToLowerInvariant()
            : file.Hashes.TryGetValue("sha1", out var sha1) && Convert.ToHexStringLower(SHA1.HashData(bytes)) == sha1.ToLowerInvariant();
        return ok ? bytes : throw new OperationException(OpError.HashMismatch, file.Filename);
    }

    public static string LoaderName(Loader loader) => loader.ToString().ToLowerInvariant();

    async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUrl + relativeUrl), ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new JsonException("Empty Modrinth response");
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
            {
                if (response.IsSuccessStatusCode) return response;
                response.Dispose();
                throw new HttpRequestException($"Modrinth returned {(int)response.StatusCode}", null, response.StatusCode);
            }
            var wait = response.Headers.TryGetValues("X-Ratelimit-Reset", out var values) && int.TryParse(values.First(), out var s) ? s : 5;
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 60)), ct);
        }
    }

    sealed record SearchResponse(List<ModrinthSearchHit> Hits);
}
