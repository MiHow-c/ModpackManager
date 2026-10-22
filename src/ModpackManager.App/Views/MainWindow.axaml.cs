using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ModpackManager.App.Resources;
using ModpackManager.App.ViewModels;

namespace ModpackManager.App.Views;

public partial class MainWindow : Window
{
    bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // no dialog needed: the first close with unsaved edits only warns, a second close discards them
        Closing += (_, e) =>
        {
            if (_closeConfirmed || DataContext is not MainViewModel { UnsavedCount: > 0 } vm) return;
            e.Cancel = _closeConfirmed = true;
            vm.Status = string.Format(Strings.UnsavedOnClose, vm.UnsavedCount);
        };
    }

    async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.PickFolderTitle });
        if (folders is [var folder] && folder.TryGetLocalPath() is { } path)
            await ((MainViewModel)DataContext!).OpenAsync(path);
    }
}
