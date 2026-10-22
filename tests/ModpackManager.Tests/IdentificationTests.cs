using System.Net;
using System.Text.Json;
using ModpackManager.Core;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class IdentificationTests
{
    readonly string _root = TestPacks.NewDir("ident");
    readonly AppPaths _paths = new(TestPacks.NewDir("data"));
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    List<PackEntry> ScanThreeJars()
    {
        var mods = Path.Combine(_root, "mods");
        TestPacks.Jar(Path.Combine(mods, "known.jar"), ("fabric.mod.json", TestPacks.FabricJson("known", "1.0")));
        TestPacks.Jar(Path.Combine(mods, "withurl.jar"), ("fabric.mod.json", TestPacks.FabricJson("withurl", "1.0", "https://example.com/withurl")));
        TestPacks.Jar(Path.Combine(mods, "nothing.jar"), ("readme.txt", "no loader metadata"));
        return PackScanner.Scan(_root).Entries;
    }

    static PackEntry ByFile(IEnumerable<PackEntry> entries, string file) => entries.Single(e => e.Path == $"mods/{file}");

    [Fact]
    public async Task Modrinth_hash_hit_sets_source_and_a_miss_stays_unknown()
    {
        var entries = ScanThreeJars();
        var knownSha1 = ByFile(entries, "known.jar").Sha1!;
        var http = new FakeHttp((_, _) => FakeHttp.Json($$"""
            { "{{knownSha1}}": { "id": "V1", "project_id": "P1", "version_number": "1.0", "files": [] } }
            """));
        var identifier = new SourceIdentifier(new ModrinthClient(new HttpClient(http)), null, _paths);

        var result = await identifier.IdentifyAsync(_root, entries, _ct);

        var known = ByFile(result.Entries, "known.jar");
        Assert.Equal((SourceKind.Modrinth, "P1", "V1", "https://modrinth.com/project/P1"), (known.Source, known.ProjectId, known.VersionId, known.Url));
        var withUrl = ByFile(result.Entries, "withurl.jar");
        Assert.Equal((SourceKind.Url, "https://example.com/withurl"), (withUrl.Source, withUrl.Url));
        Assert.Equal(SourceKind.Unknown, ByFile(result.Entries, "nothing.jar").Source);
        Assert.Empty(result.FailedSources);

        // one batched request for the whole pack, with an identifying User-Agent
        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.modrinth.com/v2/version_files", request.RequestUri!.ToString());
        Assert.Contains("ModpackManager", request.Headers.UserAgent.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("sha1", json.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(entries.Select(e => e.Sha1).Order(), json.RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()).Order());
    }

    [Fact]
    public async Task Hits_are_cached_by_hash_and_manual_links_apply_to_unknown_files()
    {
        var entries = ScanThreeJars();
        var knownSha1 = ByFile(entries, "known.jar").Sha1!;
        var http = new FakeHttp((_, _) => FakeHttp.Json($$"""
            { "{{knownSha1}}": { "id": "V1", "project_id": "P1", "version_number": "1.0", "files": [] } }
            """));
        var identifier = new SourceIdentifier(new ModrinthClient(new HttpClient(http)), null, _paths);
        await identifier.IdentifyAsync(_root, entries, _ct);

        var nothingSha1 = ByFile(entries, "nothing.jar").Sha1!;
        identifier.SetManualLink(_root, nothingSha1, "https://example.com/manual");
        var second = await identifier.IdentifyAsync(_root, entries, _ct);

        Assert.DoesNotContain(knownSha1, http.Requests[1].Body!); // served from cache
        Assert.Equal(SourceKind.Modrinth, ByFile(second.Entries, "known.jar").Source);
        var manual = ByFile(second.Entries, "nothing.jar");
        Assert.Equal((SourceKind.Manual, "https://example.com/manual"), (manual.Source, manual.Url));
    }

    [Fact]
    public async Task CurseForge_fingerprint_is_used_after_a_Modrinth_miss()
    {
        var entries = ScanThreeJars();
        var fingerprint = Murmur2.CurseForgeFingerprint(Path.Combine(_root, "mods", "nothing.jar"));
        var http = new FakeHttp((request, _) => request.RequestUri!.Host == "api.curseforge.com"
            ? FakeHttp.Json($$"""
                { "data": { "exactMatches": [ { "id": 238222, "file": { "id": 5101366, "modId": 238222, "fileFingerprint": {{fingerprint}}, "downloadUrl": null, "fileName": "x.jar" } } ] } }
                """)
            : FakeHttp.Json("{}"));
        var identifier = new SourceIdentifier(new ModrinthClient(new HttpClient(http)), new CurseForgeClient(new HttpClient(http), "test-key"), _paths);

        var result = await identifier.IdentifyAsync(_root, entries, _ct);

        var nothing = ByFile(result.Entries, "nothing.jar");
        Assert.Equal((SourceKind.CurseForge, "238222", "5101366"), (nothing.Source, nothing.ProjectId, nothing.VersionId));
        var cfRequest = http.Requests.Single(r => r.Request.RequestUri!.Host == "api.curseforge.com");
        Assert.Equal("test-key", cfRequest.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://api.curseforge.com/v1/fingerprints/432", cfRequest.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Unreachable_Modrinth_is_reported_and_files_stay_unknown()
    {
        var entries = ScanThreeJars();
        var http = new FakeHttp((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var identifier = new SourceIdentifier(new ModrinthClient(new HttpClient(http)), null, _paths);

        var result = await identifier.IdentifyAsync(_root, entries, _ct);

        Assert.Equal([SourceKind.Modrinth], result.FailedSources);
        Assert.Equal(SourceKind.Unknown, ByFile(result.Entries, "known.jar").Source);
    }
}
