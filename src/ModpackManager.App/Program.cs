using Avalonia;
using ModpackManager.Core.Updates;

namespace ModpackManager.App;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // the staged new version is started with this to replace the installed files, before any UI exists
        if (args is [Updater.ApplyArgument, var installDir, var oldPid] && int.TryParse(oldPid, out var pid))
            return Updater.ApplyStaged(installDir, pid);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // also used by the IDE previewer
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
