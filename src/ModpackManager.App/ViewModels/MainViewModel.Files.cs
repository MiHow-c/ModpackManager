using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Packs;

namespace ModpackManager.App.ViewModels;

/// <summary>Files tab: config editor plus adding and removing other pack files.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeleteSelectedFileCommand), nameof(SaveFileCommand))]
    public partial string? SelectedFile { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    public partial bool CanEditFile { get; set; }

    [ObservableProperty] public partial string EditorText { get; set; } = "";

    [ObservableProperty] public partial string NewFileFolder { get; set; } = "config";

    // shortcut: unsaved editor text is dropped when another file is selected; add a dirty check if it bites
    partial void OnSelectedFileChanged(string? value)
    {
        var full = value is null || _editor is null ? null : Path.Combine(_editor.Root, value);
        // packwiz files that are only described by metadata are not on disk, so they cannot be edited
        CanEditFile = full is not null && ConfigValidator.IsEditable(value!) && File.Exists(full);
        EditorText = CanEditFile ? File.ReadAllText(full!) : value is null ? "" : Strings.NotEditable;
    }

    void RefreshFiles()
    {
        var selected = SelectedFile;
        Files.Clear();
        foreach (var e in _all.Where(e => e.Kind != EntryKind.Mod)) Files.Add(e.Path);
        SelectedFile = Files.Contains(selected ?? "") ? selected : null;
    }

    [RelayCommand(CanExecute = nameof(CanSaveFile))]
    Task SaveFile()
    {
        // same check the editor runs, done here too for an instant message
        if (ConfigValidator.Validate(SelectedFile!, EditorText) is { } error)
        {
            Status = string.Format(Strings.ErrorSyntaxError, error);
            return Task.CompletedTask;
        }
        return ApplyAsync(new PutFile(SelectedFile!, Encoding.UTF8.GetBytes(EditorText)));
    }

    bool CanSaveFile() => CanEditFile && Allowed(new PutFile(SelectedFile!, []));

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedFile))]
    Task DeleteSelectedFile() => ApplyAsync(new DeleteFile(SelectedFile!));

    bool CanDeleteSelectedFile() => SelectedFile is { } file && Allowed(new DeleteFile(file));

    /// <summary>Copies a local file into the pack folder given in <see cref="NewFileFolder"/>.</summary>
    public Task AddFileAsync(string localPath) =>
        ApplyAsync(new PutFile($"{NewFileFolder.Trim().Trim('/')}/{Path.GetFileName(localPath)}", File.ReadAllBytes(localPath)));
}
