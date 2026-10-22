using System.Text;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;
using ModpackManager.Tests.Fixtures;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Tests;

public class PackEditTests
{
    readonly AppPaths _paths = new(TestPacks.NewDir("data"));
    readonly FakeModrinth _modrinth = new();
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    PackEditor Open(string root) => PackEditor.Open(root, _paths, _modrinth.Client, new SourceIdentifier(_modrinth.Client, null, _paths));

    string BackupsDir(string root) => Path.Combine(_paths.PackDir(root), "backups");

    static TomlTable Toml(string path) => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;

    /// <summary>index.toml lists exactly the pack's files with correct sha256, and pack.toml holds index.toml's hash.</summary>
    static void AssertIndexMatchesFiles(string root)
    {
        var index = Toml(Path.Combine(root, "index.toml"));
        Assert.Equal("sha256", index["hash-format"]);
        var indexed = ((TomlTableArray)index["files"]).ToDictionary(f => (string)f["file"], f => (string)f["hash"]);
        foreach (var (file, hash) in indexed)
            Assert.Equal(TestPacks.Sha256(Path.Combine(root, file)), hash);

        var onDisk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(p => p is not ("pack.toml" or "index.toml" or ".packwizignore"));
        Assert.Equal(onDisk.Order(StringComparer.Ordinal), indexed.Keys.Order(StringComparer.Ordinal));

        var packIndex = (TomlTable)Toml(Path.Combine(root, "pack.toml"))["index"];
        Assert.Equal(TestPacks.Sha256(Path.Combine(root, "index.toml")), packIndex["hash"]);
    }

    [Fact]
    public async Task Packwiz_add_and_remove_mod_keeps_index_hashes_in_sync()
    {
        var root = TestPacks.Packwiz();
        var version = _modrinth.Add("P1", "V1", "coolmod-1.0.jar", "coolmod", environment: "client_only");
        var editor = Open(root);

        await editor.ApplyAsync(new AddMod("P1", "V1"), Principal.Owner, ct: _ct);

        var meta = Toml(Path.Combine(root, "mods", "coolmod.pw.toml"));
        Assert.Equal("coolmod-1.0.jar", meta["filename"]);
        Assert.Equal("client", meta["side"]);
        var download = (TomlTable)meta["download"];
        Assert.Equal(("sha512", $"https://cdn.modrinth.com/data/P1/versions/V1/coolmod-1.0.jar"), (download["hash-format"], download["url"]));
        Assert.Equal(TestPacks.Sha512(version.Bytes), download["hash"]);
        var modrinth = (TomlTable)((TomlTable)meta["update"])["modrinth"];
        Assert.Equal(("P1", "V1"), (modrinth["mod-id"], modrinth["version"]));
        Assert.True((bool)((TomlTableArray)Toml(Path.Combine(root, "index.toml"))["files"]).Single(f => (string)f["file"] == "mods/coolmod.pw.toml")["metafile"]);
        AssertIndexMatchesFiles(root);
        Assert.DoesNotContain(_modrinth.Http.Requests, r => r.Request.RequestUri!.Host == "cdn.modrinth.com"); // packwiz never downloads

        var added = PackScanner.Scan(root).Entries.Single(e => e.Path == "mods/coolmod-1.0.jar");
        Assert.Equal((SourceKind.Modrinth, "P1", "V1"), (added.Source, added.ProjectId, added.VersionId));

        await editor.ApplyAsync(new DeleteFile("mods/coolmod-1.0.jar"), Principal.Owner, ct: _ct);

        Assert.False(File.Exists(Path.Combine(root, "mods", "coolmod.pw.toml")));
        AssertIndexMatchesFiles(root);
        Assert.Contains(Directory.EnumerateFiles(BackupsDir(root), "coolmod.pw.toml", SearchOption.AllDirectories), _ => true);
    }

    [Fact]
    public async Task Packwiz_update_rewrites_the_metafile_and_refresh_respects_packwizignore()
    {
        var root = TestPacks.Packwiz();
        TestPacks.Write(Path.Combine(root, ".packwizignore"), "*.md\n!keep.md\nbuild/\n");
        foreach (var file in new[] { "README.md", "keep.md", "build/out.txt", ".git/config", "export.zip" })
            TestPacks.Write(Path.Combine(root, file), "x");
        _modrinth.Add("AANobbMI", "abc123", "sodium-1.0.jar", "sodium");
        _modrinth.Add("AANobbMI", "def456", "sodium-2.0.jar", "sodium");
        var editor = Open(root);

        await editor.ApplyAsync(new UpdateMod("mods/sodium-1.0.jar", "def456"), Principal.Owner, ct: _ct);

        var paths = PackScanner.Scan(root).Entries.Select(e => e.Path).ToList();
        Assert.Contains("mods/sodium-2.0.jar", paths);
        Assert.DoesNotContain("mods/sodium-1.0.jar", paths);
        var indexed = ((TomlTableArray)Toml(Path.Combine(root, "index.toml"))["files"]).Select(f => (string)f["file"]).ToList();
        Assert.Contains("keep.md", indexed);
        Assert.DoesNotContain("README.md", indexed);
        Assert.DoesNotContain("build/out.txt", indexed);
        Assert.DoesNotContain(".git/config", indexed);
        Assert.DoesNotContain("export.zip", indexed);
        foreach (var f in (TomlTableArray)Toml(Path.Combine(root, "index.toml"))["files"])
            Assert.Equal(TestPacks.Sha256(Path.Combine(root, (string)f["file"])), f["hash"]);
    }

    [Fact]
    public async Task Folder_add_update_and_delete_mod_keeps_old_files_in_backups()
    {
        var root = TestPacks.NewDir("folder-edit");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        var v1 = _modrinth.Add("P1", "V1", "coolmod-1.0.jar", "coolmod");
        var v2 = _modrinth.Add("P1", "V2", "coolmod-2.0.jar", "coolmod");
        _modrinth.Add("P2", "W1", "other-1.0.jar", "other");
        var editor = Open(root);

        await editor.ApplyAsync(new AddMod("P1", "V1"), Principal.Owner, ct: _ct);
        Assert.Equal(v1.Bytes, File.ReadAllBytes(Path.Combine(root, "mods", "coolmod-1.0.jar")));

        var duplicate = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new AddMod("P1", "V2"), Principal.Owner, ct: _ct));
        Assert.Equal(OpError.AlreadyInstalled, duplicate.Error);
        var otherProject = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new UpdateMod("mods/coolmod-1.0.jar", "W1"), Principal.Owner, ct: _ct));
        Assert.Equal(OpError.WrongProject, otherProject.Error);

        await editor.ApplyAsync(new UpdateMod("mods/coolmod-1.0.jar", "V2"), Principal.Owner, ct: _ct);
        Assert.Equal(v2.Bytes, File.ReadAllBytes(Path.Combine(root, "mods", "coolmod-2.0.jar")));
        Assert.False(File.Exists(Path.Combine(root, "mods", "coolmod-1.0.jar")));

        await editor.ApplyAsync(new DeleteFile("mods/coolmod-2.0.jar"), Principal.Owner, ct: _ct);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "mods")));

        var backedUp = Directory.EnumerateFiles(BackupsDir(root), "*.jar", SearchOption.AllDirectories).Select(Path.GetFileName).Order();
        Assert.Equal(["coolmod-1.0.jar", "coolmod-2.0.jar"], backedUp);
        Assert.DoesNotContain(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories), d => d.Contains("backups"));
    }

    [Fact]
    public async Task Download_with_a_wrong_hash_is_rejected_and_nothing_is_written()
    {
        var root = TestPacks.NewDir("folder-hash");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        _modrinth.Add("P1", "V1", "coolmod-1.0.jar", "coolmod");
        _modrinth.CorruptDownloads = true;

        var error = await Assert.ThrowsAsync<OperationException>(() => Open(root).ApplyAsync(new AddMod("P1", "V1"), Principal.Owner, ct: _ct));

        Assert.Equal(OpError.HashMismatch, error.Error);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "mods")));
    }

    [Fact]
    public async Task Resource_pack_projects_are_not_added_as_mods()
    {
        var root = TestPacks.NewDir("folder-type");
        _modrinth.Add("R1", "X1", "pack.zip", "faithful", type: "resourcepack");

        var error = await Assert.ThrowsAsync<OperationException>(() => Open(root).ApplyAsync(new AddMod("R1", "X1"), Principal.Owner, ct: _ct));

        Assert.Equal(OpError.NotAMod, error.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Config_edit_validates_syntax_and_backs_up_the_old_version(bool packwiz)
    {
        var root = packwiz ? TestPacks.Packwiz() : TestPacks.Folder();
        var config = Path.Combine(root, "config", "a.toml");
        var editor = Open(root);

        var bad = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new PutFile("config/a.toml", "x = = 1"u8.ToArray()), Principal.Owner, ct: _ct));
        Assert.Equal(OpError.SyntaxError, bad.Error);
        Assert.Equal("x = 1\n", File.ReadAllText(config));
        var badJson = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new PutFile("config/b.json", "{ \"a\": }"u8.ToArray()), Principal.Owner, ct: _ct));
        Assert.Equal(OpError.SyntaxError, badJson.Error);

        await editor.ApplyAsync(new PutFile("config/a.toml", "x = 2\n"u8.ToArray()), Principal.Owner, ct: _ct);
        await editor.ApplyAsync(new PutFile("config/c.json", "{ \"a\": 1, // comment\n }"u8.ToArray()), Principal.Owner, ct: _ct);
        await editor.ApplyAsync(new PutFile("kubejs/server_scripts/x.js", "// any text"u8.ToArray()), Principal.Owner, ct: _ct);

        Assert.Equal("x = 2\n", File.ReadAllText(config));
        var backup = Assert.Single(Directory.EnumerateFiles(BackupsDir(root), "a.toml", SearchOption.AllDirectories));
        Assert.Equal("x = 1\n", File.ReadAllText(backup));
        if (packwiz) AssertIndexMatchesFiles(root);
    }

    [Theory]
    [InlineData(false, "../escape.txt")]
    [InlineData(false, "saves/world/level.dat")]
    [InlineData(false, "options.txt")]
    [InlineData(true, "pack.toml")]
    [InlineData(true, "index.toml")]
    [InlineData(true, "mods/sodium.pw.toml")]
    [InlineData(true, "../escape.txt")]
    [InlineData(true, "export.zip")]
    public async Task Writes_outside_the_pack_or_to_format_files_are_rejected(bool packwiz, string path)
    {
        var root = packwiz ? TestPacks.Packwiz() : TestPacks.Folder();
        var editor = Open(root);

        var put = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new PutFile(path, "x"u8.ToArray()), Principal.Owner, ct: _ct));
        var delete = await Assert.ThrowsAsync<OperationException>(() => editor.ApplyAsync(new DeleteFile(path), Principal.Owner, ct: _ct));

        Assert.Equal((OpError.InvalidPath, OpError.InvalidPath), (put.Error, delete.Error));
        Assert.False(File.Exists(Path.Combine(root, "..", "escape.txt")));
    }

    [Fact]
    public async Task Settings_go_to_pack_toml_for_packwiz_and_to_local_settings_for_folders()
    {
        var packwizRoot = TestPacks.Packwiz();
        await Open(packwizRoot).ApplyAsync(new SetSettings("1.21.4", Loader.Fabric, "0.16.9"), Principal.Owner, ct: _ct);
        Assert.Equal(new PackInfo("Test Pack", PackFormat.Packwiz, "1.21.4", Loader.Fabric, "0.16.9"), PackScanner.Scan(packwizRoot).Info);
        Assert.False(((TomlTable)Toml(Path.Combine(packwizRoot, "pack.toml"))["versions"]).ContainsKey("neoforge"));

        var folderRoot = TestPacks.Folder(); // has mmc-pack.json with 1.21.1 / NeoForge
        var editor = Open(folderRoot);
        await editor.ApplyAsync(new SetSettings("1.20.1", Loader.Forge, "47.3.0"), Principal.Owner, ct: _ct);
        var (info, _) = editor.Store.Scan();
        Assert.Equal(("1.20.1", Loader.Forge, "47.3.0"), (info.McVersion, info.Loader, info.LoaderVersion));
        Assert.Equal(["mmc-pack.json"], Directory.EnumerateFiles(folderRoot).Select(Path.GetFileName)); // nothing written into the pack
    }

    [Fact]
    public async Task Search_filters_by_pack_version_and_loader()
    {
        var http = new FakeHttp((_, _) => FakeHttp.Json("""{ "hits": [ { "project_id": "P1", "slug": "s", "title": "T", "downloads": 5 } ] }"""));

        var hits = await new ModrinthClient(new HttpClient(http)).SearchModsAsync("map", "1.21.1", Loader.Quilt, _ct);

        Assert.Equal("P1", Assert.Single(hits).ProjectId);
        var query = Uri.UnescapeDataString(Assert.Single(http.Requests).Request.RequestUri!.Query);
        Assert.Contains("""[["project_type:mod"],["versions:1.21.1"],["categories:quilt","categories:fabric"]]""", query);
    }
}
