using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ModpackManager.App.Resources;
using ModpackManager.App.ViewModels;

namespace ModpackManager.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.PickFolderTitle });
        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
            await ((MainViewModel)DataContext!).OpenAsync(path);
    }
}
