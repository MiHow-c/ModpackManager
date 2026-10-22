using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ModpackManager.App.ViewModels;
using ModpackManager.App.Views;
using ModpackManager.Core;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Sync;
using ModpackManager.Core.Updates;

namespace ModpackManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var http = new HttpClient();
            var paths = AppPaths.Default();
            var updateDir = Path.Combine(paths.Root, "update");
            Updater.Cleanup(updateDir); // leftovers of an update that has finished
            var modrinth = new ModrinthClient(http);
            var curseForge = CurseForgeClient.FromEnvironment(http);
            var vm = new MainViewModel(paths, modrinth, new SourceIdentifier(modrinth, curseForge, paths), new Puller(modrinth, curseForge, http),
                CreateUpdater(http, updateDir)) { RequestShutdown = () => desktop.Shutdown() };
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Shutdown();
            // a pack path on the command line opens it right away
            if (desktop.Args is [var packPath]) _ = vm.OpenAsync(packPath);
            _ = vm.CheckForUpdatesAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Release builds read update.json from the latest GitHub release of UpdateRepository (Directory.Build.props).
    /// MPM_UPDATE_URL overrides it (mirrors, testing); debug builds only check when that variable is set.
    /// </summary>
    static Updater? CreateUpdater(HttpClient http, string updateDir)
    {
        var url = Environment.GetEnvironmentVariable("MPM_UPDATE_URL");
#if !DEBUG
        var repository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(repository))
            url = $"https://github.com/{repository}/releases/latest/download/update.json";
#endif
        return Uri.TryCreate(url, UriKind.Absolute, out var manifest) ? new Updater(http, manifest, updateDir) : null;
    }
}
