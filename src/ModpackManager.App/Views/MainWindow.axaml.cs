using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ModpackManager.App.Resources;
using ModpackManager.App.ViewModels;

namespace ModpackManager.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    MainViewModel ViewModel => (MainViewModel)DataContext!;

    async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.PickFolderTitle });
        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
            await ViewModel.OpenAsync(path);
    }

    async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.PickFileTitle });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
            await ViewModel.AddFileAsync(path);
    }
}
