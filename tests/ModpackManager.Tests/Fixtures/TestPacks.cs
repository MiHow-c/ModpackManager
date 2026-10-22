using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ModpackManager.Tests.Fixtures;

/// <summary>
/// Generated test packs: tiny jars holding only loader metadata and a mini packwiz pack.
/// Everything is written under the test output directory (inside tests/), never to real game folders.
/// </summary>
static class TestPacks
{
    public static string NewDir(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Jar(string path, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JarBytes(files));
        return path;
    }

    public static byte[] JarBytes(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }

    public static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static string Sha512(byte[] bytes) => Convert.ToHexStringLower(SHA512.HashData(bytes));

    public static string ModsToml(string id, string version, string loaderDep, string mcRange) => $$"""
        modLoader="javafml"
        loaderVersion="[1,)"
        license="MIT"

        [[mods]]
        modId="{{id}}"
        version="{{version}}"
        displayName="{{id}} name"

        [[dependencies.{{id}}]]
        modId="{{loaderDep}}"
        type="required"
        versionRange="[1,)"

        [[dependencies.{{id}}]]
        modId="minecraft"
        type="required"
        versionRange="{{mcRange}}"
        """;

    public static string FabricJson(string id, string version, string? homepage = null) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "version": "{{version}}",
          "name": "{{id}} name",
          "contact": { {{(homepage is null ? "" : $"\"homepage\": \"{homepage}\"")}} },
          "depends": { "fabricloader": ">=0.15", "minecraft": ">=1.21" }
        }
        """;

    public static string QuiltJson(string id, string version) => $$"""
        {
          "schema_version": 1,
          "quilt_loader": {
            "group": "com.example",
            "id": "{{id}}",
            "version": "{{version}}",
            "metadata": { "name": "{{id}} name" },
            "depends": [ "quilt_loader", { "id": "minecraft", "versions": ">=1.21" } ]
          }
        }
        """;

    /// <summary>Instance folder with one jar per loader, an old-style NeoForge jar, a config and an ignored save.</summary>
    public static string Folder()
    {
        var root = NewDir("folder");
        var mods = Path.Combine(root, "mods");
        Jar(Path.Combine(mods, "neo.jar"), ("META-INF/neoforge.mods.toml", ModsToml("neomod", "1.0.0", "neoforge", "[1.21.1,1.22)")));
        Jar(Path.Combine(mods, "forge.jar"), ("META-INF/mods.toml", ModsToml("forgemod", "2.0.0", "forge", "[1.20.1,1.21)")));
        Jar(Path.Combine(mods, "oldneo.jar"),
            ("META-INF/mods.toml", ModsToml("oldneomod", "${file.jarVersion}", "neoforge", "[1.20.4,1.20.5)")),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 3.0.0\r\n"));
        Jar(Path.Combine(mods, "fabric.jar"), ("fabric.mod.json", FabricJson("fabricmod", "4.0.0")));
        Jar(Path.Combine(mods, "quilt.jar"), ("quilt.mod.json", QuiltJson("quiltmod", "5.0.0")));
        Write(Path.Combine(mods, "broken.jar"), "not a zip");
        Write(Path.Combine(root, "config", "a.toml"), "x = 1\n");
        Write(Path.Combine(root, "saves", "world", "level.dat"), "ignored");
        Write(Path.Combine(root, "mmc-pack.json"), """
            {
              "formatVersion": 1,
              "components": [
                { "uid": "net.minecraft", "version": "1.21.1" },
                { "uid": "net.neoforged", "version": "21.1.77" }
              ]
            }
            """);
        return root;
    }

    /// <summary>Mini packwiz pack: one Modrinth mod, one CurseForge mod, one config and an index entry pointing outside the pack.</summary>
    public static string Packwiz()
    {
        var dir = NewDir("packwiz");
        var root = Path.Combine(dir, "pack");
        Write(Path.Combine(dir, "outside.txt"), "must never be indexed through ../");
        Write(Path.Combine(root, "mods", "sodium.pw.toml"), """
            name = "Sodium"
            filename = "sodium-1.0.jar"
            side = "client"

            [download]
            url = "https://cdn.modrinth.com/data/AANobbMI/versions/abc123/sodium-1.0.jar"
            hash-format = "sha512"
            hash = "AA11"

            [update]
            [update.modrinth]
            mod-id = "AANobbMI"
            version = "abc123"
            """);
        Write(Path.Combine(root, "mods", "jei.pw.toml"), """
            name = "Just Enough Items"
            filename = "jei-1.0.jar"
            side = "both"

            [download]
            hash-format = "sha1"
            hash = "bb22"
            mode = "metadata:curseforge"

            [update]
            [update.curseforge]
            file-id = 5101366
            project-id = 238222
            """);
        Write(Path.Combine(root, "config", "a.toml"), "x = 1\n");

        var index = new StringBuilder("hash-format = \"sha256\"\n");
        foreach (var (file, meta) in new[] { ("mods/sodium.pw.toml", true), ("mods/jei.pw.toml", true), ("config/a.toml", false) })
            index.Append($"\n[[files]]\nfile = \"{file}\"\nhash = \"{Sha256(Path.Combine(root, file))}\"\n{(meta ? "metafile = true\n" : "")}");
        index.Append("\n[[files]]\nfile = \"../outside.txt\"\nhash = \"00\"\n");
        var indexPath = Write(Path.Combine(root, "index.toml"), index.ToString());

        Write(Path.Combine(root, "pack.toml"), $"""
            name = "Test Pack"
            pack-format = "packwiz:1.1.0"

            [index]
            file = "index.toml"
            hash-format = "sha256"
            hash = "{Sha256(indexPath)}"

            [versions]
            minecraft = "1.21.1"
            neoforge = "21.1.77"
            """);
        return root;
    }
}
