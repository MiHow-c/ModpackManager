using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core;
using ModpackManager.Core.Stats;

namespace ModpackManager.App.ViewModels;

/// <summary>Statistics page: categories and sides from Modrinth, content counted from the mod jars.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<BarRow> CategoryBars { get; } = [];

    public ObservableCollection<BarRow> SideBars { get; } = [];

    public ObservableCollection<ModStatsRow> ModStatsRows { get; } = [];

    /// <summary>Null until computed; reset whenever the pack is reloaded.</summary>
    [ObservableProperty] public partial PackStatistics? Stats { get; set; }

    [ObservableProperty] public partial string StatsNote { get; set; } = "";

    /// <summary>Called when the page is shown: computes once per pack state instead of on every reload.</summary>
    public void EnsureStats()
    {
        if (Stats is null && HasPack && !IsBusy) ComputeStatsCommand.Execute(null);
    }

    [RelayCommand]
    Task ComputeStats() => RunAsync(async () =>
    {
        Status = Strings.StatsComputing;
        // disabled jars do not run in the game, so they do not count
        var mods = _all.Where(e => e.Kind == EntryKind.Mod && e.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
        var stats = await PackStatistics.BuildAsync(_editor!.Root, mods, modrinth);

        Fill(CategoryBars, stats.Categories.Select(kv => (CategoryLabel(kv.Key), kv.Value)));
        Fill(SideBars, stats.Sides.Select(kv => (Strings.ResourceManager.GetString($"Side{kv.Key}", Strings.Culture) ?? kv.Key.ToString(), kv.Value)));
        ModStatsRows.Clear();
        foreach (var m in stats.Mods.OrderByDescending(m => m.Content?.Total ?? -1).ThenBy(m => m.Name))
            ModStatsRows.Add(new ModStatsRow(m, string.Join(", ", m.Categories.Select(CategoryLabel))));

        StatsNote = string.Join(" ", new[]
        {
            stats.WithoutModrinthData > 0 ? string.Format(Strings.StatsWithoutData, stats.WithoutModrinthData) : null,
            stats.JarsCounted > 0 ? string.Format(Strings.StatsJarsCounted, stats.JarsCounted, stats.Mods.Count) : Strings.StatsNoJars,
        }.OfType<string>());
        Stats = stats;
        Status = Strings.Done;
    });

    static void Fill(ObservableCollection<BarRow> target, IEnumerable<(string Label, int Count)> values)
    {
        var rows = values.OrderByDescending(v => v.Count).ToList();
        var max = rows.Count == 0 ? 0 : rows[0].Count;
        target.Clear();
        foreach (var (label, count) in rows) target.Add(new BarRow(label, count, max));
    }

    // Modrinth slugs such as "game-mechanics" map to Category_game_mechanics; unknown ones are shown as they come
    static string CategoryLabel(string slug) => Strings.ResourceManager.GetString($"Category_{slug.Replace('-', '_')}", Strings.Culture) ?? slug;
}

public sealed record BarRow(string Label, int Count, int Max);

public sealed class ModStatsRow(ModStats stats, string categories)
{
    public string Name => stats.Name;
    public string Categories => categories;
    public string Side => stats.Side is { } side ? Strings.ResourceManager.GetString($"Side{side}", Strings.Culture) ?? side.ToString() : Strings.NotSet;
    public int? Blocks => stats.Content?.Blocks;
    public int? Items => stats.Content?.Items;
    public int? Entities => stats.Content?.Entities;
    public int? Structures => stats.Content?.Structures;
    public int? Biomes => stats.Content?.Biomes;
    public int? Recipes => stats.Content?.Recipes;
    public int? Total => stats.Content?.Total;
}
