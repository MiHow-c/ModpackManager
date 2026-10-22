using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.Core;
using ModpackManager.Core.Ops;

namespace ModpackManager.App.ViewModels;

/// <summary>Pack settings tab: Minecraft version and loader.</summary>
public sealed partial class MainViewModel
{
    public Loader[] LoaderChoices { get; } = Enum.GetValues<Loader>();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial string SettingsMcVersion { get; set; } = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial Loader? SettingsLoader { get; set; }

    [ObservableProperty] public partial string SettingsLoaderVersion { get; set; } = "";

    void LoadSettingsInputs(PackInfo info)
    {
        SettingsMcVersion = info.McVersion ?? "";
        SettingsLoader = info.Loader;
        SettingsLoaderVersion = info.LoaderVersion ?? "";
    }

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    Task SaveSettings() => ApplyAsync(new SetSettings(SettingsMcVersion, SettingsLoader!.Value, SettingsLoaderVersion));

    bool CanSaveSettings() => !string.IsNullOrWhiteSpace(SettingsMcVersion) && SettingsLoader is not null
                              && Allowed(new SetSettings("", Loader.NeoForge, null));
}
