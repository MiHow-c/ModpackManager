using System.IO.Compression;
using System.Text.Json;
using ModpackManager.Core.Sources;

namespace ModpackManager.Core.Stats;

public enum ModSide { Client, Server, Both }

/// <summary>What a mod adds to the game, counted from the definition files in its jar.</summary>
public sealed record ContentCounts(int Blocks, int Items, int Entities, int Structures, int Biomes, int Recipes)
{
    public static readonly ContentCounts Zero = new(0, 0, 0, 0, 0, 0);

    public int Total => Blocks + Items + Entities + Structures + Biomes + Recipes;

    public static ContentCounts operator +(ContentCounts a, ContentCounts b) => new(
        a.Blocks + b.Blocks, a.Items + b.Items, a.Entities + b.Entities, a.Structures + b.Structures, a.Biomes + b.Biomes, a.Recipes + b.Recipes);
}

/// <summary>Content is null when there is no jar on disk to read (packwiz) or it is not a valid zip.</summary>
public sealed record ModStats(string Name, string Path, IReadOnlyList<string> Categories, ModSide? Side, ContentCounts? Content);

/// <summary>
/// Pack statistics: Modrinth categories and sides (one bulk request) plus the content each mod jar adds.
/// Nothing here rates "power": these are counts anyone can verify, not a made-up score.
/// Content is null when no jar could be read (packwiz): unknown, not zero.
/// </summary>
public sealed record PackStatistics(
    IReadOnlyList<ModStats> Mods,
    IReadOnlyDictionary<string, int> Categories,
    IReadOnlyDictionary<ModSide, int> Sides,
    int WithoutModrinthData,
    int JarsCounted,
    ContentCounts? Content)
{
    public static async Task<PackStatistics> BuildAsync(string root, IReadOnlyList<PackEntry> mods, ModrinthClient modrinth, CancellationToken ct = default)
    {
        var ids = mods.Where(e => e is { Source: SourceKind.Modrinth, ProjectId: not null }).Select(e => e.ProjectId!).Distinct().ToList();
        Dictionary<string, ModrinthProject> projects = [];
        try
        {
            if (ids.Count > 0) projects = (await modrinth.GetProjectsAsync(ids, ct)).ToDictionary(p => p.Id);
        }
        catch (HttpRequestException)
        {
            // offline: categories stay empty, content counts still work
        }

        var stats = await Task.Run(() => mods.Select(e =>
        {
            var project = e is { Source: SourceKind.Modrinth, ProjectId: { } id } ? projects.GetValueOrDefault(id) : null;
            var jar = Path.Combine(root, e.Path);
            var content = File.Exists(jar) ? ContentCounter.Count(jar) : null; // packwiz keeps only metadata, no jar
            return new ModStats(e.Name, e.Path, project?.Categories ?? [], project is null ? null : SideOf(project), content);
        }).ToList(), ct);

        return new PackStatistics(
            stats,
            stats.SelectMany(m => m.Categories).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count()),
            stats.Where(m => m.Side is not null).GroupBy(m => m.Side!.Value).ToDictionary(g => g.Key, g => g.Count()),
            stats.Count(m => m.Side is null),
            stats.Count(m => m.Content is not null),
            stats.Any(m => m.Content is not null) ? stats.Aggregate(ContentCounts.Zero, (sum, m) => sum + (m.Content ?? ContentCounts.Zero)) : null);
    }

    static ModSide SideOf(ModrinthProject p) =>
        p.ServerSide == "unsupported" ? ModSide.Client
        : p.ClientSide == "unsupported" ? ModSide.Server
        : ModSide.Both;
}

public static class ContentCounter
{
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Blocks = blockstate files, items = item models, entities = "entity.ns.name" translations, structures and biomes =
    /// worldgen definitions, recipes = recipe files. The minecraft namespace is skipped for assets: those are retextures.
    /// </summary>
    // shortcut: counts definition files, not game registrations; content registered purely in code is missed — read registries if exact numbers matter
    public static ContentCounts? Count(string jarPath)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(jarPath); }
        catch (InvalidDataException) { return null; }

        using (zip)
        {
            int blocks = 0, items = 0, entities = 0, structures = 0, biomes = 0, recipes = 0;
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.Ordinal)) continue;
                switch (entry.FullName.Split('/'))
                {
                    case ["assets", var ns, "blockstates", ..] when ns != "minecraft": blocks++; break;
                    case ["assets", var ns, "models", "item", ..] when ns != "minecraft": items++; break;
                    case ["assets", _, "lang", "en_us.json"]: entities += CountEntities(entry); break;
                    case ["data", _, "worldgen", "structure", ..]: structures++; break;
                    case ["data", _, "worldgen", "biome", ..]: biomes++; break;
                    case ["data", _, "recipe" or "recipes", ..]: recipes++; break; // "recipes" before 1.21
                }
            }
            return new ContentCounts(blocks, items, entities, structures, biomes, recipes);
        }
    }

    static int CountEntities(ZipArchiveEntry lang)
    {
        try
        {
            using var stream = lang.Open();
            using var doc = JsonDocument.Parse(stream, Lenient);
            // "entity.ns.name" names an entity; longer keys are descriptions, minecraft.* are vanilla renames
            return doc.RootElement.EnumerateObject().Count(p => p.Name.Split('.') is ["entity", var ns, _] && ns != "minecraft");
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
