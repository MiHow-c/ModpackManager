using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ModpackManager.App.ViewModels;
using ModpackManager.App.Views;
using ModpackManager.Core;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Sync;

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
            var modrinth = new ModrinthClient(http);
            var curseForge = CurseForgeClient.FromEnvironment(http);
            var vm = new MainViewModel(paths, modrinth, new SourceIdentifier(modrinth, curseForge, paths), new Puller(modrinth, curseForge, http));
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Shutdown();
            // a pack path on the command line opens it right away
            if (desktop.Args is [var packPath]) _ = vm.OpenAsync(packPath);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
