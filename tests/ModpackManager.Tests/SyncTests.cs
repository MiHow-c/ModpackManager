using System.Security.Cryptography;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Packs;
using ModpackManager.Core.Remote;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Sync;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class SyncTests
{
    readonly AppPaths _paths = new(TestPacks.NewDir("data"));
    readonly FakeModrinth _modrinth = new();
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    SourceIdentifier Identifier => new(_modrinth.Client, null, _paths);

    async Task<List<PackEntry>> Identified(string root) =>
        (await Identifier.IdentifyAsync(root, PackScanner.Scan(root).Entries, _ct)).Entries;

    static (DiffKind, string)[] Summary(IEnumerable<DiffItem> diff) => diff.Select(d => (d.Kind, d.Path)).ToArray();

    /// <summary>Host: coolmod 2.0, other, config a=2. Local: coolmod 1.0, an unknown extra jar, config a=1. Both: same.toml.</summary>
    (string Host, string Local) TwoFolders()
    {
        var v1 = _modrinth.Add("P1", "V1", "coolmod-1.0.jar", "coolmod");
        var v2 = _modrinth.Add("P1", "V2", "coolmod-2.0.jar", "coolmod");
        var other = _modrinth.Add("P2", "W1", "other-1.0.jar", "other");
        var host = TestPacks.NewDir("host");
        File.WriteAllBytes(TestPacks.Write(Path.Combine(host, "mods", "coolmod-2.0.jar"), ""), v2.Bytes);
        File.WriteAllBytes(TestPacks.Write(Path.Combine(host, "mods", "other-1.0.jar"), ""), other.Bytes);
        TestPacks.Write(Path.Combine(host, "config", "a.toml"), "x = 2\n");
        TestPacks.Write(Path.Combine(host, "config", "same.toml"), "s = 1\n");
        var local = TestPacks.NewDir("local");
        File.WriteAllBytes(TestPacks.Write(Path.Combine(local, "mods", "coolmod-1.0.jar"), ""), v1.Bytes);
        TestPacks.Jar(Path.Combine(local, "mods", "extra.jar"), ("readme.txt", "unknown"));
        TestPacks.Write(Path.Combine(local, "config", "a.toml"), "x = 1\n");
        TestPacks.Write(Path.Combine(local, "config", "same.toml"), "s = 1\n");
        return (host, local);
    }

    [Fact]
    public async Task Folder_comparison_finds_missing_extra_other_versions_and_changed_configs()
    {
        var (host, local) = TwoFolders();

        var diff = ManifestDiff.Compare(await Identified(host), await Identified(local));

        Assert.Equal(
        [
            (DiffKind.ConfigChanged, "config/a.toml"),
            (DiffKind.DifferentVersion, "mods/coolmod-2.0.jar"),
            (DiffKind.Extra, "mods/extra.jar"),
            (DiffKind.Missing, "mods/other-1.0.jar"),
        ], Summary(diff));
    }

    [Fact]
    public async Task Packwiz_host_compares_with_an_instance_folder()
    {
        var host = TestPacks.Packwiz(); // sodium: Modrinth AANobbMI/abc123, jei: CurseForge, config/a.toml = "x = 1"
        var newer = _modrinth.Add("AANobbMI", "def456", "sodium-2.0.jar", "sodium");
        var jeiBytes = TestPacks.JarBytes(("readme.txt", "jei"));
        // the packwiz CurseForge entry carries the sha1 of the real jar, which is how the folder copy pairs with it
        var jeiMeta = Path.Combine(host, "mods", "jei.pw.toml");
        File.WriteAllText(jeiMeta, File.ReadAllText(jeiMeta).Replace("bb22", Convert.ToHexStringLower(SHA1.HashData(jeiBytes))));
        TestPacks.Write(Path.Combine(host, "config", "b.toml"), "y = 1\n");
        new PackwizStore(host, _paths).Refresh();

        var local = TestPacks.NewDir("local");
        File.WriteAllBytes(TestPacks.Write(Path.Combine(local, "mods", "sodium.jar"), ""), newer.Bytes);
        File.WriteAllBytes(TestPacks.Write(Path.Combine(local, "mods", "jei.jar"), ""), jeiBytes);
        TestPacks.Write(Path.Combine(local, "config", "a.toml"), "x = 2\n");
        TestPacks.Jar(Path.Combine(local, "mods", "extra.jar"), ("readme.txt", "unknown"));

        var diff = ManifestDiff.Compare(await Identified(host), await Identified(local));

        Assert.Equal(
        [
            (DiffKind.ConfigChanged, "config/a.toml"),
            (DiffKind.Missing, "config/b.toml"),
            (DiffKind.Extra, "mods/extra.jar"),
            (DiffKind.DifferentVersion, "mods/sodium-1.0.jar"),
        ], Summary(diff));
    }

    [Fact]
    public async Task Pull_makes_the_local_folder_match_the_host_and_backs_up_extras()
    {
        var (host, local) = TwoFolders();
        var hostSession = new LocalPackSession(PackEditor.Open(host, _paths, _modrinth.Client, Identifier));
        var diff = ManifestDiff.Compare((await hostSession.GetManifestAsync(_ct)).Entries, await Identified(local));

        var result = await new Puller(_modrinth.Client, null, new HttpClient(_modrinth.Http))
            .PullAsync(hostSession, diff, new FolderStore(local, _paths, _modrinth.Client), _ct);

        Assert.Empty(result.Skipped);
        Assert.Empty(ManifestDiff.Compare((await hostSession.GetManifestAsync(_ct)).Entries, await Identified(local)));
        Assert.Equal("x = 2\n", File.ReadAllText(Path.Combine(local, "config", "a.toml")));
        Assert.Contains(_modrinth.Http.Requests, r => r.Request.RequestUri!.Host == "cdn.modrinth.com"); // mods come from the source
        var backups = Directory.EnumerateFiles(Path.Combine(_paths.PackDir(local), "backups"), "*", SearchOption.AllDirectories).Select(Path.GetFileName);
        Assert.Contains("extra.jar", backups);
        Assert.Contains("coolmod-1.0.jar", backups);
    }

    [Fact]
    public async Task Pull_from_packwiz_downloads_from_the_source_and_skips_what_nobody_can_serve()
    {
        var host = TestPacks.Packwiz();
        var sodium = _modrinth.Add("AANobbMI", "abc123", "sodium-1.0.jar", "sodium");
        var hostSession = new LocalPackSession(PackEditor.Open(host, _paths, _modrinth.Client, Identifier));
        var local = TestPacks.NewDir("local");
        var diff = ManifestDiff.Compare((await hostSession.GetManifestAsync(_ct)).Entries, []);

        var result = await new Puller(_modrinth.Client, null, new HttpClient(_modrinth.Http))
            .PullAsync(hostSession, diff, new FolderStore(local, _paths, _modrinth.Client), _ct);

        Assert.Equal(sodium.Bytes, File.ReadAllBytes(Path.Combine(local, "mods", "sodium-1.0.jar")));
        Assert.Equal("x = 1\n", File.ReadAllText(Path.Combine(local, "config", "a.toml")));
        // CurseForge file without an API key, and a packwiz host has no jar to send
        Assert.Equal(["mods/jei-1.0.jar"], result.Skipped);
    }

    [Fact]
    public async Task Pull_rejects_host_paths_outside_the_pack_and_files_with_wrong_hashes()
    {
        var local = TestPacks.NewDir("local");
        var payload = "evil"u8.ToArray();
        var good = Convert.ToHexStringLower(SHA256.HashData(payload));
        PackEntry Entry(string path, string sha256) => new(path, PackScanner.KindOf(path), new Dictionary<string, string> { ["sha256"] = sha256 }, null, path);
        var diff = new[] { "../escape.txt", "saves/world/level.dat", "options.txt" }
            .Select(p => new DiffItem(DiffKind.Missing, Entry(p, good), null))
            .Append(new DiffItem(DiffKind.Missing, Entry("config/tampered.toml", new string('0', 64)), null))
            .ToList();

        var result = await new Puller(_modrinth.Client, null, new HttpClient(_modrinth.Http))
            .PullAsync(new StaticSession(payload), diff, new FolderStore(local, _paths, _modrinth.Client), _ct);

        Assert.Equal(diff.Select(d => d.Path), result.Skipped);
        Assert.False(File.Exists(Path.Combine(local, "..", "escape.txt")));
        Assert.Empty(Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories));
    }

    /// <summary>A host that serves the same bytes for every path, standing in for a malicious one.</summary>
    sealed class StaticSession(byte[] content) : IPackSession
    {
        public Principal Me => Principal.Owner;
        public Task<PackManifest> GetManifestAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetFileAsync(string path, CancellationToken ct = default) => Task.FromResult(content);
        public Task<long> ApplyAsync(Operation op, long baseRevision, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
