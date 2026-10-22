using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core.Updates;

namespace ModpackManager.App.ViewModels;

/// <summary>App updates: checked at startup, installed only when the user clicks.</summary>
public sealed partial class MainViewModel
{
    AvailableUpdate? _update;

    /// <summary>Set by the app: closes the application so the staged update can replace its files.</summary>
    public Action? RequestShutdown { get; set; }

    static Version CurrentVersion => typeof(MainViewModel).Assembly.GetName().Version!;

    public string AppVersion => string.Format(Strings.AppVersion, CurrentVersion.ToString(3));

    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty] public partial string UpdateTitle { get; set; } = "";

    [ObservableProperty] public partial string UpdateNotes { get; set; } = "";

    public async Task CheckForUpdatesAsync()
    {
        if (updater is null) return;
        try
        {
            _update = await updater.CheckAsync(CurrentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or IOException or TaskCanceledException)
        {
            return; // offline or no release published yet: nothing worth interrupting the user for
        }
        if (_update is null) return;
        UpdateTitle = string.Format(Strings.UpdateAvailable, _update.Version.ToString(3));
        UpdateNotes = _update.Notes ?? "";
        IsUpdateAvailable = true;
    }

    [RelayCommand]
    Task InstallUpdate() => RunAsync(async () =>
    {
        // the app closes for the update, which would drop unsaved editor drafts
        if (UnsavedCount > 0)
        {
            Status = Strings.UpdateUnsaved;
            return;
        }
        Status = Strings.UpdateDownloading;
        try
        {
            var stagedExe = await updater!.StageAsync(_update!, Path.GetFileName(Environment.ProcessPath!));
            Process.Start(new ProcessStartInfo(stagedExe)
            {
                UseShellExecute = false,
                ArgumentList = { Updater.ApplyArgument, AppContext.BaseDirectory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) },
            });
        }
        catch (InvalidDataException e)
        {
            Status = string.Format(Strings.UpdateFailed, e.Message);
            return;
        }
        RequestShutdown?.Invoke();
    });

    [RelayCommand]
    void DismissUpdate() => IsUpdateAvailable = false;
}
