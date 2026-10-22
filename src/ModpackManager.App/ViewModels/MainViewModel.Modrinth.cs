using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Sources;

namespace ModpackManager.App.ViewModels;

/// <summary>Mods tab: Modrinth search, adding a mod and switching the selected mod's version.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ModrinthSearchHit> SearchHits { get; } = [];

    public ObservableCollection<VersionItem> HitVersions { get; } = [];

    public ObservableCollection<VersionItem> ModVersions { get; } = [];

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";

    [ObservableProperty] public partial ModrinthSearchHit? SelectedHit { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddModCommand))]
    public partial VersionItem? SelectedHitVersion { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UpdateModCommand))]
    public partial VersionItem? SelectedModVersion { get; set; }

    /// <summary>Search filters follow the pack being edited: the host's when connected.</summary>
    PackInfo? TargetInfo => IsConnected ? _hostInfo : _info;

    [RelayCommand]
    Task Search() => RunAsync(async () =>
    {
        var hits = await modrinth.SearchModsAsync(SearchQuery.Trim(), TargetInfo?.McVersion, TargetInfo?.Loader);
        SearchHits.Clear();
        foreach (var hit in hits) SearchHits.Add(hit);
    });

    // versions are filtered by the pack's MC version and loader, so only compatible files are offered
    partial void OnSelectedHitChanged(ModrinthSearchHit? value)
    {
        HitVersions.Clear();
        if (value is not null)
            _ = RunAsync(async () =>
            {
                await LoadVersionsAsync(value.ProjectId, HitVersions);
                SelectedHitVersion = HitVersions.FirstOrDefault(); // newest compatible version
            });
    }

    [RelayCommand(CanExecute = nameof(CanAddMod))]
    Task AddMod() => ApplyAsync(new AddMod(SelectedHit!.ProjectId, SelectedHitVersion!.Version.Id));

    bool CanAddMod() => SelectedHitVersion is not null && Allowed(new AddMod("", ""));

    [RelayCommand(CanExecute = nameof(IsModrinthModSelected))]
    Task LoadModVersions() => RunAsync(() => LoadVersionsAsync(SelectedRow!.Entry.ProjectId!, ModVersions));

    bool IsModrinthModSelected() => SelectedRow?.Entry is { Kind: EntryKind.Mod, Source: SourceKind.Modrinth, ProjectId: not null };

    [RelayCommand(CanExecute = nameof(CanUpdateMod))]
    Task UpdateMod() => ApplyAsync(new UpdateMod(SelectedRow!.Path, SelectedModVersion!.Version.Id));

    bool CanUpdateMod() => SelectedModVersion is not null && SelectedRow is not null && Allowed(new UpdateMod("", ""));

    partial void OnSelectedRowChanged(EntryRow? value) => ModVersions.Clear();

    async Task LoadVersionsAsync(string projectId, ObservableCollection<VersionItem> target)
    {
        var versions = await modrinth.GetProjectVersionsAsync(projectId, TargetInfo?.McVersion, TargetInfo?.Loader);
        target.Clear();
        foreach (var v in versions.OrderByDescending(v => v.DatePublished)) target.Add(new VersionItem(v));
    }
}

public sealed record VersionItem(ModrinthVersion Version)
{
    public override string ToString() => $"{Version.VersionNumber}  ({Version.DatePublished:yyyy-MM-dd})";
}
