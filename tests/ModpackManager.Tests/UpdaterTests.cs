using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ModpackManager.Core.Updates;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class UpdaterTests
{
    readonly string _dir = TestPacks.NewDir("update");
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    static byte[] Package() => TestPacks.JarBytes(("ModpackManager.App.exe", "new exe"), ("lib/New.dll", "new dll"));

    Updater UpdaterFor(byte[] package, string version, string? sha256 = null, string url = "https://example.com/pkg.zip")
    {
        var manifest = JsonSerializer.Serialize(new UpdateManifest(version, "notes",
            new() { [Updater.Rid] = new UpdateAsset(url, sha256 ?? Convert.ToHexStringLower(SHA256.HashData(package))) }), JsonSerializerOptions.Web);
        var http = new FakeHttp((request, _) => request.RequestUri!.AbsolutePath.EndsWith("update.json", StringComparison.Ordinal)
            ? FakeHttp.Json(manifest)
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        return new Updater(new HttpClient(http), new Uri("https://example.com/update.json"), _dir);
    }

    [Fact]
    public async Task Newer_release_for_this_platform_is_offered_and_older_or_equal_is_not()
    {
        var updater = UpdaterFor(Package(), "0.2.0");

        Assert.Equal(new Version(0, 2, 0), (await updater.CheckAsync(new Version(0, 1, 0, 0), _ct))!.Version);
        Assert.Null(await updater.CheckAsync(new Version(0, 2, 0, 0), _ct));
        Assert.Null(await updater.CheckAsync(new Version(1, 0, 0, 0), _ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdaterFor(Package(), "0.2.0", url: "http://example.com/pkg.zip").CheckAsync(new Version(0, 1), _ct));
    }

    [Fact]
    public async Task Package_is_staged_only_when_its_hash_matches()
    {
        var package = Package();
        var good = UpdaterFor(package, "0.2.0");
        var exe = await good.StageAsync((await good.CheckAsync(new Version(0, 1), _ct))!, "ModpackManager.App.exe", _ct);
        Assert.Equal("new exe", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(_dir, "staged", "lib", "New.dll")));

        Directory.Delete(Path.Combine(_dir, "staged"), recursive: true);
        var tampered = UpdaterFor(package, "0.2.0", sha256: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await tampered.StageAsync((await tampered.CheckAsync(new Version(0, 1), _ct))!, "ModpackManager.App.exe", _ct));
        Assert.False(Directory.Exists(Path.Combine(_dir, "staged")));
    }

    [Fact]
    public void Files_are_replaced_and_restored_when_one_cannot_be_written()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows file locking to force a failed copy.");
        var source = TestPacks.NewDir("new");
        var target = TestPacks.NewDir("installed");
        foreach (var name in new[] { "a.dll", "b.dll", "c.dll" })
        {
            TestPacks.Write(Path.Combine(source, name), "new");
            TestPacks.Write(Path.Combine(target, name), "old");
        }

        using (File.Open(Path.Combine(target, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => Updater.ReplaceFiles(source, target, Path.Combine(_dir, "rollback")));
        Assert.All(Directory.GetFiles(target), f => Assert.Equal("old", File.ReadAllText(f)));

        TestPacks.Write(Path.Combine(source, "lib", "d.dll"), "new");
        Updater.ReplaceFiles(source, target, Path.Combine(_dir, "rollback"));
        Assert.All(Directory.GetFiles(target, "*", SearchOption.AllDirectories), f => Assert.Equal("new", File.ReadAllText(f)));
    }
}
