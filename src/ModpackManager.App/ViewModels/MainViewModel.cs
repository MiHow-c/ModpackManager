using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Sync;

namespace ModpackManager.App.ViewModels;

/// <summary>Shared state: the open pack, its entries and status. Tabs live in the other MainViewModel.*.cs parts.</summary>
public sealed partial class MainViewModel(
    AppPaths paths, ModrinthClient modrinth, SourceIdentifier identifier, Puller puller, Core.Updates.Updater? updater) : ObservableObject
{
    PackEditor? _editor;
    List<PackEntry> _all = [];
    PackInfo? _info;
    long _baseRevision; // revision of the pack the user's edits are based on (the host's when connected)

    public ObservableCollection<EntryRow> Rows { get; } = [];

    [ObservableProperty] public partial string PackName { get; set; } = "";

    [ObservableProperty] public partial string PackDetails { get; set; } = "";

    [ObservableProperty] public partial Loader? PackLoader { get; set; }

    [ObservableProperty] public partial string PackLoaderText { get; set; } = "";

    [ObservableProperty] public partial string Status { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool HasPack { get; set; }

    [ObservableProperty] public partial bool OnlyUnknown { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignLinkCommand), nameof(RemoveModCommand), nameof(LoadModVersionsCommand), nameof(UpdateModCommand))]
    public partial EntryRow? SelectedRow { get; set; }

    [ObservableProperty] public partial string ManualLink { get; set; } = "";

    partial void OnOnlyUnknownChanged(bool value) => RefreshRows();

    public async Task OpenAsync(string root)
    {
        await ShutdownAsync();
        Disconnect();
        _editor = PackEditor.Open(root, paths, modrinth, identifier);
        // a pack that fails to load (e.g. broken pack.toml) stays closed, so its error stays on screen
        HasPack = false;
        LoadCollabState();
        await RunAsync(ReloadAsync);
    }

    async Task ReloadAsync()
    {
        Status = Strings.Scanning;
        var (manifest, failed) = await _editor!.LoadAsync();
        var info = manifest.Info;
        _info = info;
        _all = manifest.Entries.ToList();
        Stats = null; // recomputed the next time the statistics page is shown
        if (!IsConnected) _baseRevision = manifest.Revision;
        PackName = info.Name;
        PackDetails = string.Format(Strings.PackDetails,
            info.Format == PackFormat.Packwiz ? Strings.FormatPackwiz : Strings.FormatFolder, info.McVersion ?? Strings.NotSet);
        PackLoader = info.Loader;
        PackLoaderText = info.Loader is { } loader ? $"{loader} {info.LoaderVersion}".TrimEnd() : Strings.NoLoader;
        LoadSettingsInputs(info);
        RefreshRows();
        RefreshFiles();
        HasPack = true;
        var mods = _all.Where(IsModRow).ToList();
        Status = string.Join(" ", failed.Select(s => string.Format(Strings.SourceFailed, s))
            .Prepend(string.Format(Strings.Summary, mods.Count, mods.Count(e => e.Source == SourceKind.Unknown))));
    }

    /// <summary>
    /// Sends the operation to whoever owns the pack (this process, or the host when connected), then refreshes:
    /// a connected client pulls the result so its local copy shows the change.
    /// </summary>
    Task ApplyAsync(Operation op) => RunAsync(async () =>
    {
        _baseRevision = await Session.ApplyAsync(op, _baseRevision);
        if (IsConnected) await PullAsync();
        else await ReloadAsync();
        Status = $"{Strings.Done} {Status}";
    });

    /// <summary>"Pull changes": the host's state when connected, otherwise a rescan of the local pack.</summary>
    [RelayCommand]
    Task Refresh() => RunAsync(() => IsConnected ? CompareAsync() : ReloadAsync());

    /// <summary>UI-side permission check to hide what the user cannot do; the host enforces it anyway.</summary>
    bool Allowed(Operation op) => _editor is not null && Session.Me.Has(Permissions.Required(op, targetExists: true));

    void NotifyPermissionCommands()
    {
        foreach (var command in new IRelayCommand[] { RemoveModCommand, AddModCommand, UpdateModCommand, SaveFileCommand, DeleteSelectedFileCommand, SaveSettingsCommand })
            command.NotifyCanExecuteChanged();
    }

    async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationException e)
        {
            Status = string.Format(Strings.ResourceManager.GetString($"Error{e.Error}", Strings.Culture) ?? "{0}", e.Detail);
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            Status = Strings.ErrorCertificate;
        }
        catch (HttpRequestException e)
        {
            Status = string.Format(Strings.ErrorNetwork, e.Message);
        }
        catch (Exception e)
        {
            // UI boundary: show the problem instead of crashing the app
            Status = string.Format(Strings.ErrorUnexpected, e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // only jars (also Prism's disabled *.jar.disabled) are mods; other files in mods/ belong in the file editor
    static bool IsModRow(PackEntry e) => e.Kind is EntryKind.ResourcePack or EntryKind.ShaderPack
        || (e.Kind == EntryKind.Mod && (e.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)));

    void RefreshRows()
    {
        Rows.Clear();
        foreach (var e in _all.Where(e => IsModRow(e) && (!OnlyUnknown || e.Source == SourceKind.Unknown)))
            Rows.Add(new EntryRow(e));
    }

    [RelayCommand(CanExecute = nameof(CanAssignLink))]
    void AssignLink()
    {
        if (!Uri.TryCreate(ManualLink.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Status = Strings.InvalidLink;
            return;
        }
        var entry = SelectedRow!.Entry;
        identifier.SetManualLink(_editor!.Root, entry.Sha1!, uri.ToString());
        _all[_all.IndexOf(entry)] = entry with { Source = SourceKind.Manual, Url = uri.ToString() };
        ManualLink = "";
        RefreshRows();
    }

    // Modrinth/CurseForge hits are authoritative; only weaker sources can be overridden by hand
    bool CanAssignLink() => SelectedRow?.Entry is { Sha1: not null, Source: SourceKind.Unknown or SourceKind.Url or SourceKind.Manual };

    [RelayCommand(CanExecute = nameof(CanRemoveMod))]
    Task RemoveMod() => ApplyAsync(new DeleteFile(SelectedRow!.Path));

    bool CanRemoveMod() => SelectedRow is { } row && Allowed(new DeleteFile(row.Path));
}

public sealed class EntryRow(PackEntry entry)
{
    public PackEntry Entry => entry;
    public string Name => entry.Name;
    public string Version => entry.Mod?.Version ?? System.IO.Path.GetFileName(entry.Path);
    public string Loaders => entry.Mod is { } mod ? string.Join(", ", mod.Loaders) : Strings.NotSet;
    public IReadOnlyList<Loader> LoaderList => entry.Mod?.Loaders ?? [];
    public SourceKind Source => entry.Source;
    public Uri? Link => Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ? uri : null;
    public string LinkText => Link?.Host ?? "";
    public string Path => entry.Path;

    public string Status => entry.Source switch
    {
        SourceKind.Modrinth => Strings.StatusModrinth,
        SourceKind.CurseForge => Strings.StatusCurseForge,
        SourceKind.Url => Strings.StatusUrl,
        SourceKind.Manual => Strings.StatusManual,
        _ => Strings.StatusUnknown,
    };
}
