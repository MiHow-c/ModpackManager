using ModpackManager.Core;
using ModpackManager.Core.Scanning;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class ScanTests
{
    [Fact]
    public void Folder_scan_reads_mod_id_version_and_loader_for_every_loader()
    {
        var (info, entries) = PackScanner.Scan(TestPacks.Folder());

        var mods = entries.Where(e => e.Mod is not null).ToDictionary(e => e.Mod!.ModId, e => e.Mod!);
        Assert.Equal(["fabricmod", "forgemod", "neomod", "oldneomod", "quiltmod"], mods.Keys.Order());
        AssertMod(mods["neomod"], "1.0.0", Loader.NeoForge, "[1.21.1,1.22)");
        AssertMod(mods["forgemod"], "2.0.0", Loader.Forge, "[1.20.1,1.21)");
        AssertMod(mods["oldneomod"], "3.0.0", Loader.NeoForge, "[1.20.4,1.20.5)"); // mods.toml + neoforge dep, version from MANIFEST.MF
        AssertMod(mods["fabricmod"], "4.0.0", Loader.Fabric, ">=1.21");
        AssertMod(mods["quiltmod"], "5.0.0", Loader.Quilt, ">=1.21");

        Assert.Equal(new PackInfo(info.Name, PackFormat.Folder, "1.21.1", Loader.NeoForge, "21.1.77"), info);
        var broken = Assert.Single(entries, e => e.Path == "mods/broken.jar");
        Assert.Null(broken.Mod);
        Assert.Equal(EntryKind.Config, Assert.Single(entries, e => e.Path == "config/a.toml").Kind);
        Assert.DoesNotContain(entries, e => e.Path.StartsWith("saves/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Path.Contains("/.index/", StringComparison.Ordinal));
        Assert.All(entries, e => Assert.Equal(["sha1", "sha256", "sha512"], e.Hashes.Keys.Order()));
    }

    static void AssertMod(ModInfo mod, string version, Loader loader, string mc)
    {
        Assert.Equal(version, mod.Version);
        Assert.Equal([loader], mod.Loaders);
        Assert.Equal(mc, mod.McVersion);
        Assert.Equal($"{mod.ModId} name", mod.Name);
    }

    [Fact]
    public void Packwiz_scan_reads_sources_from_update_sections_without_network()
    {
        var (info, entries) = PackScanner.Scan(TestPacks.Packwiz());

        Assert.Equal(new PackInfo("Test Pack", PackFormat.Packwiz, "1.21.1", Loader.NeoForge, "21.1.77"), info);
        Assert.Equal(["config/a.toml", "mods/jei-1.0.jar", "mods/sodium-1.0.jar"], entries.Select(e => e.Path));

        var sodium = entries[2];
        Assert.Equal((SourceKind.Modrinth, "AANobbMI", "abc123", "Sodium"), (sodium.Source, sodium.ProjectId, sodium.VersionId, sodium.Name));
        Assert.Equal("https://modrinth.com/project/AANobbMI", sodium.Url);
        Assert.Equal("aa11", sodium.Hashes["sha512"]);
        Assert.Null(sodium.Size);

        var jei = entries[1];
        Assert.Equal((SourceKind.CurseForge, "238222", "5101366"), (jei.Source, jei.ProjectId, jei.VersionId));
        Assert.Equal("bb22", jei.Hashes["sha1"]);

        Assert.Equal(EntryKind.Config, entries[0].Kind);
    }

    [Theory]
    [InlineData("mods/a.jar", true)]
    [InlineData("config/sub/b.toml", true)]
    [InlineData("../a.jar", false)]
    [InlineData("mods/../../a.jar", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/x.jar", false)]
    [InlineData("mods\\..\\a.jar", false)]
    [InlineData("mods//a.jar", false)]
    [InlineData("", false)]
    public void Relative_paths_cannot_escape_the_pack(string path, bool safe) =>
        Assert.Equal(safe, PackScanner.IsSafeRelativePath(path));
}
