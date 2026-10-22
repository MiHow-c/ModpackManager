using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ModpackManager.App.Resources;
using ModpackManager.App.ViewModels;

namespace ModpackManager.App.Views.Pages;

public partial class ModsPage : UserControl
{
    public ModsPage() => InitializeComponent();
}

public partial class FilesPage : UserControl
{
    public FilesPage() => InitializeComponent();

    async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.PickFileTitle });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
            await ((MainViewModel)DataContext!).AddFileAsync(path);
    }
}

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();
}

public partial class CollabPage : UserControl
{
    public CollabPage() => InitializeComponent();
}
