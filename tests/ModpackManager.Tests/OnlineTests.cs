using System.Text.Json;
using ModpackManager.Core.Sources;

namespace ModpackManager.Tests;

/// <summary>Calls the real Modrinth API. Skipped unless MPM_ONLINE_TESTS=1; filter with Category=Online.</summary>
[Trait("Category", "Online")]
public class OnlineTests
{
    [Fact]
    public async Task Modrinth_finds_a_real_file_by_sha1()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MPM_ONLINE_TESTS") == "1", "Online test: set MPM_ONLINE_TESTS=1 to run.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();

        // take a real file hash from a well-known project, then look it up the way a scan does
        using var request = new HttpRequestMessage(HttpMethod.Get, ModrinthClient.BaseUrl + "project/fabric-api/version?include_changelog=false");
        request.Headers.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        using var response = await http.SendAsync(request, ct);
        using var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var version = versions.RootElement[0];
        var sha1 = version.GetProperty("files")[0].GetProperty("hashes").GetProperty("sha1").GetString()!;

        var found = await new ModrinthClient(http).GetVersionsByHashesAsync([sha1], ct);

        Assert.Equal(version.GetProperty("project_id").GetString(), found[sha1].ProjectId);
    }
}
