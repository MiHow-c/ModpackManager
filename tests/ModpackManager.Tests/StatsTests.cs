using ModpackManager.Core;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Stats;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class StatsTests
{
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void Jar_content_is_counted_from_its_definition_files()
    {
        var jar = TestPacks.Jar(Path.Combine(TestPacks.NewDir("content"), "mod.jar"),
            ("assets/coolmod/blockstates/ore.json", "{}"),
            ("assets/coolmod/blockstates/lamp.json", "{}"),
            ("assets/minecraft/blockstates/stone.json", "{}"), // vanilla retexture, not a new block
            ("assets/coolmod/models/item/ore.json", "{}"),
            ("assets/coolmod/models/item/tools/pick.json", "{}"),
            ("assets/coolmod/lang/en_us.json", """{ "entity.coolmod.golem": "Golem", "entity.coolmod.golem.desc": "x", "entity.minecraft.pig": "Pig", "item.coolmod.ore": "Ore" }"""),
            ("data/coolmod/worldgen/structure/tower.json", "{}"),
            ("data/coolmod/worldgen/biome/glade.json", "{}"),
            ("data/coolmod/recipe/ore_smelting.json", "{}"),
            ("data/coolmod/recipes/legacy/pick.json", "{}"),
            ("data/minecraft/recipe/extra_bread.json", "{}"),
            ("assets/coolmod/textures/block/ore.png", "png"));

        Assert.Equal(new ContentCounts(Blocks: 2, Items: 2, Entities: 1, Structures: 1, Biomes: 1, Recipes: 3), ContentCounter.Count(jar));
        Assert.Null(ContentCounter.Count(TestPacks.Write(Path.Combine(TestPacks.NewDir("bad"), "broken.jar"), "not a zip")));
    }

    [Fact]
    public async Task Pack_statistics_combine_modrinth_categories_and_sides_with_jar_content()
    {
        var paths = new AppPaths(TestPacks.NewDir("data"));
        var modrinth = new FakeModrinth();
        var root = TestPacks.NewDir("stats");
        var tech = modrinth.Add("P1", "V1", "tech-1.0.jar", "tech", categories: ["technology", "storage"]);
        var map = modrinth.Add("P2", "W1", "minimap-1.0.jar", "minimap", categories: ["utility"], serverSide: "unsupported");
        foreach (var v in new[] { tech, map })
            File.WriteAllBytes(TestPacks.Write(Path.Combine(root, "mods", v.Filename), ""), v.Bytes);
        TestPacks.Jar(Path.Combine(root, "mods", "unknown.jar"), ("assets/x/blockstates/a.json", "{}"));
        var mods = (await new SourceIdentifier(modrinth.Client, null, paths).IdentifyAsync(root, PackScanner.Scan(root).Entries, _ct)).Entries;

        var stats = await PackStatistics.BuildAsync(root, mods, modrinth.Client, _ct);

        Assert.Equal(new Dictionary<string, int> { ["technology"] = 1, ["storage"] = 1, ["utility"] = 1 }, stats.Categories);
        Assert.Equal(new Dictionary<ModSide, int> { [ModSide.Both] = 1, [ModSide.Client] = 1 }, stats.Sides);
        Assert.Equal((1, 3, 1), (stats.WithoutModrinthData, stats.JarsCounted, stats.Content!.Blocks));
        Assert.Single(modrinth.Http.Requests, r => r.Request.RequestUri!.AbsolutePath == "/v2/projects"); // one bulk request
    }
}
