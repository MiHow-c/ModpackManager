using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ModpackManager.Core.Sources;

namespace ModpackManager.Tests.Fixtures;

/// <summary>In-memory Modrinth: projects, versions with generated jars, hash lookup and CDN downloads.</summary>
sealed class FakeModrinth
{
    public sealed record Version(string ProjectId, string Id, string Filename, byte[] Bytes, string Environment = "client_and_server");

    public List<Version> Versions { get; } = [];

    public Dictionary<string, (string Slug, string Title, string Type, string[] Categories, string ClientSide, string ServerSide)> Projects { get; } = [];

    /// <summary>When set, the CDN serves different bytes than the published hash.</summary>
    public bool CorruptDownloads { get; set; }

    public FakeHttp Http { get; }

    public ModrinthClient Client => new(new HttpClient(Http));

    public FakeModrinth() => Http = new FakeHttp(Respond);

    /// <summary>Adds a project version backed by a tiny Fabric jar.</summary>
    public Version Add(string projectId, string versionId, string filename, string slug, string type = "mod", string environment = "client_and_server",
        string[]? categories = null, string clientSide = "required", string serverSide = "required")
    {
        Projects[projectId] = (slug, $"{slug} title", type, categories ?? [], clientSide, serverSide);
        var version = new Version(projectId, versionId, filename,
            TestPacks.JarBytes(("fabric.mod.json", TestPacks.FabricJson(slug, versionId))), environment);
        Versions.Add(version);
        return version;
    }

    HttpResponseMessage Respond(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host == "cdn.modrinth.com")
            return Versions.FirstOrDefault(v => v.Filename == segments[^1]) is { } file
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CorruptDownloads ? [1, 2, 3] : file.Bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);

        return segments switch
        {
            ["v2", "version_files"] => FakeHttp.Json(JsonSerializer.Serialize(
                JsonDocument.Parse(body!).RootElement.GetProperty("hashes").EnumerateArray()
                    .Select(h => h.GetString()!)
                    .Select(h => (Hash: h, Version: Versions.FirstOrDefault(v => Sha1(v.Bytes) == h)))
                    .Where(x => x.Version is not null)
                    .ToDictionary(x => x.Hash, x => VersionJson(x.Version!)))),
            ["v2", "version", var id] when Versions.FirstOrDefault(v => v.Id == id) is { } v => FakeHttp.Json(JsonSerializer.Serialize(VersionJson(v))),
            ["v2", "versions"] => FakeHttp.Json(JsonSerializer.Serialize(Ids(uri).Select(id => Versions.FirstOrDefault(v => v.Id == id))
                .OfType<Version>().Select(VersionJson))),
            ["v2", "projects"] => FakeHttp.Json(JsonSerializer.Serialize(Ids(uri).Where(Projects.ContainsKey)
                .Select(id => new { id, slug = Projects[id].Slug, title = Projects[id].Title, project_type = Projects[id].Type,
                    categories = Projects[id].Categories, client_side = Projects[id].ClientSide, server_side = Projects[id].ServerSide }))),
            ["v2", "project", var id] when Projects.TryGetValue(id, out var p) =>
                FakeHttp.Json(JsonSerializer.Serialize(new { id, slug = p.Slug, title = p.Title, project_type = p.Type })),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    static object VersionJson(Version v) => new
    {
        id = v.Id,
        project_id = v.ProjectId,
        version_number = v.Id,
        environment = v.Environment,
        files = new[]
        {
            new
            {
                hashes = new { sha1 = Sha1(v.Bytes), sha512 = Convert.ToHexStringLower(SHA512.HashData(v.Bytes)) },
                url = $"https://cdn.modrinth.com/data/{v.ProjectId}/versions/{v.Id}/{v.Filename}",
                filename = v.Filename,
                primary = true,
                size = v.Bytes.Length,
            },
        },
    };

    static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    /// <summary>The JSON array in ?ids=[...].</summary>
    static string[] Ids(Uri uri) => JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(uri.Query[(uri.Query.IndexOf("ids=", StringComparison.Ordinal) + 4)..]))!;
}
