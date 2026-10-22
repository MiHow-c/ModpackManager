using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Packs;

namespace ModpackManager.App.ViewModels;

/// <summary>
/// Files tab: config editor with live syntax checks, plus adding and removing other pack files.
/// Unsaved edits are kept as drafts per file, so switching files never loses work.
/// </summary>
public sealed partial class MainViewModel
{
    readonly Dictionary<string, string> _drafts = []; // full path -> unsaved text
    string _savedText = "";
    CancellationTokenSource? _checkDelay;

    public ObservableCollection<FileRow> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedFileCommand), nameof(SaveFileCommand), nameof(FormatJsonCommand))]
    public partial FileRow? SelectedFileRow { get; set; }

    public string? SelectedFile => SelectedFileRow?.Path;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveFileCommand), nameof(FormatJsonCommand))]
    public partial bool CanEditFile { get; set; }

    [ObservableProperty] public partial string EditorText { get; set; } = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RevertFileCommand))]
    public partial bool IsEditorDirty { get; set; }

    [ObservableProperty] public partial string FileFilter { get; set; } = "";

    [ObservableProperty] public partial string NewFileFolder { get; set; } = "config";

    /// <summary>Current syntax problem of the edited file (null when fine or not checked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IssueText), nameof(HasIssueError), nameof(HasIssueWarning), nameof(IsSyntaxOk))]
    public partial ConfigIssue? EditorIssue { get; set; }

    public bool HasIssueError => CanEditFile && EditorIssue is { IsError: true };

    public bool HasIssueWarning => CanEditFile && EditorIssue is { IsError: false };

    public bool IsSyntaxOk => CanEditFile && EditorIssue is null;

    public string IssueText => !CanEditFile ? ""
        : EditorIssue is { } issue ? string.Format(Strings.ResourceManager.GetString($"Issue{issue.Kind}", Strings.Culture) ?? "{2}", issue.Line, issue.Column, issue.Detail)
        : SelectedFile is { } path && ConfigValidator.IsChecked(path) ? Strings.SyntaxOk : Strings.SyntaxNotChecked;

    /// <summary>Number of files with unsaved edits, for the close-window guard.</summary>
    public int UnsavedCount => _drafts.Count;

    string? FullPath(string? relative) => relative is null || _editor is null ? null : Path.Combine(_editor.Root, relative);

    partial void OnSelectedFileRowChanged(FileRow? value)
    {
        var full = FullPath(value?.Path);
        // packwiz files that are only described by metadata are not on disk, so they cannot be edited
        CanEditFile = full is not null && ConfigValidator.IsEditable(full) && File.Exists(full);
        _savedText = CanEditFile ? File.ReadAllText(full!) : "";
        EditorText = !CanEditFile ? (value is null ? "" : Strings.NotEditable) : _drafts.GetValueOrDefault(full!, _savedText);
        CheckNow();
    }

    partial void OnEditorTextChanged(string value)
    {
        if (!CanEditFile || FullPath(SelectedFile) is not { } full) return;
        if (value == _savedText) _drafts.Remove(full);
        else _drafts[full] = value;
        IsEditorDirty = SelectedFileRow!.IsDirty = _drafts.ContainsKey(full);
        _ = CheckSoonAsync();
    }

    // checking after a short pause keeps typing smooth in large configs
    async Task CheckSoonAsync()
    {
        _checkDelay?.Cancel();
        var delay = _checkDelay = new CancellationTokenSource();
        try { await Task.Delay(300, delay.Token); }
        catch (TaskCanceledException) { return; }
        CheckNow();
    }

    void CheckNow()
    {
        EditorIssue = CanEditFile && SelectedFile is { } path ? ConfigValidator.Check(path, EditorText) : null;
        OnPropertyChanged(nameof(IssueText)); // also changes with CanEditFile when the issue itself stays null
        OnPropertyChanged(nameof(IsSyntaxOk));
    }

    partial void OnFileFilterChanged(string value) => RefreshFiles();

    void RefreshFiles()
    {
        var selected = SelectedFile;
        Files.Clear();
        foreach (var e in _all.Where(e => !IsModRow(e) || e.Kind != Core.EntryKind.Mod))
            if (e.Path.Contains(FileFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                Files.Add(new FileRow(e.Path) { IsDirty = _drafts.ContainsKey(FullPath(e.Path)!) });
        SelectedFileRow = Files.FirstOrDefault(f => f.Path == selected);
    }

    [RelayCommand(CanExecute = nameof(CanSaveFile))]
    Task SaveFile()
    {
        // the editor makes the same check; doing it here gives an instant message
        if (ConfigValidator.Validate(SelectedFile!, EditorText) is { } error)
        {
            Status = string.Format(Strings.ErrorSyntaxError, error);
            return Task.CompletedTask;
        }
        // on success the reload re-reads the file, finds the draft equal to it and drops it
        return ApplyAsync(new PutFile(SelectedFile!, Encoding.UTF8.GetBytes(EditorText)));
    }

    bool CanSaveFile() => CanEditFile && Allowed(new PutFile(SelectedFile!, []));

    [RelayCommand(CanExecute = nameof(IsEditorDirty))]
    void RevertFile() => EditorText = _savedText;

    [RelayCommand(CanExecute = nameof(CanFormatJson))]
    void FormatJson()
    {
        try
        {
            if (ConfigValidator.FormatJson(EditorText) is { } formatted) EditorText = formatted;
            else Status = Strings.FormatHasComments;
        }
        catch (JsonException e)
        {
            Status = string.Format(Strings.ErrorSyntaxError, e.Message);
        }
    }

    bool CanFormatJson() => CanEditFile && SelectedFile!.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedFile))]
    Task DeleteSelectedFile() => ApplyAsync(new DeleteFile(SelectedFile!));

    bool CanDeleteSelectedFile() => SelectedFile is { } file && Allowed(new DeleteFile(file));

    /// <summary>Copies a local file into the pack folder given in <see cref="NewFileFolder"/>.</summary>
    public Task AddFileAsync(string localPath) =>
        ApplyAsync(new PutFile($"{NewFileFolder.Trim().Trim('/')}/{Path.GetFileName(localPath)}", File.ReadAllBytes(localPath)));
}

public sealed partial class FileRow(string path) : ObservableObject
{
    public string Path => path;

    [ObservableProperty] public partial bool IsDirty { get; set; }
}
