using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using ModpackManager.App.Resources;
using ModpackManager.App.ViewModels;
using TextMateSharp.Grammars;

namespace ModpackManager.App.Views.Pages;

/// <summary>
/// AvaloniaEdit has no bindable Text, so this view keeps the editor and <see cref="MainViewModel.EditorText"/> in sync
/// and owns editor-only concerns: highlighting, the error-line marker, search and caret position.
/// </summary>
public partial class FilesPage : UserControl
{
    readonly RegistryOptions _grammars = new(ThemeName.DarkPlus);
    readonly IssueLineRenderer _issueLine = new();
    TextMate.Installation? _textMate;
    MainViewModel? _vm;
    bool _syncing;

    public FilesPage()
    {
        InitializeComponent();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_issueLine);
        Editor.TextChanged += (_, _) =>
        {
            if (!_syncing && _vm is not null) _vm.EditorText = Editor.Text;
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretText.Text = string.Format(Strings.CaretPosition, Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_textMate is not null) return; // pages can be re-attached; install once
        _textMate = Editor.InstallTextMate(_grammars);
        // keep the card's own background instead of the TextMate theme's
        _textMate.AppliedTheme += (_, _) => Editor.Background = Brushes.Transparent;
        ApplyTheme();
        _textMate.SetGrammar(ScopeFor(_vm?.SelectedFile));
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    void ApplyTheme() =>
        _textMate?.SetTheme(_grammars.LoadTheme(ActualThemeVariant == ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus));

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.EditorText) when _vm!.EditorText != Editor.Text:
                _syncing = true;
                // a new document per file also gives each file its own undo history
                if (_vm.SelectedFile != _documentFile) Editor.Document = new TextDocument(_vm.EditorText);
                else Editor.Document.Text = _vm.EditorText;
                _documentFile = _vm.SelectedFile;
                _syncing = false;
                break;
            case nameof(MainViewModel.SelectedFileRow):
                _textMate?.SetGrammar(ScopeFor(_vm!.SelectedFile));
                break;
            case nameof(MainViewModel.EditorIssue):
                _issueLine.Issue = _vm!.EditorIssue;
                Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                break;
        }
    }

    string? _documentFile;

    // shortcut: no TOML grammar ships with TextMateSharp; INI covers TOML's sections, keys, strings and comments
    static string? ScopeFor(string? path) => path is null ? null : Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" or ".json5" or ".snbt" => "source.json.comments",
        _ => "source.ini",
    };

    void OnGoToIssueClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.EditorIssue is not { } issue) return;
        var line = Math.Min(issue.Line, Editor.Document.LineCount);
        Editor.TextArea.Caret.Location = new TextLocation(line, issue.Column);
        Editor.ScrollTo(line, issue.Column);
        Editor.TextArea.Focus();
    }

    void OnSearchClick(object? sender, RoutedEventArgs e) => Editor.SearchPanel.Open();

    async void OnAddFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.PickFileTitle });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
            await _vm!.AddFileAsync(path);
    }

    /// <summary>Tints the line of the current syntax problem: red for errors, amber for warnings.</summary>
    sealed class IssueLineRenderer : IBackgroundRenderer
    {
        static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromArgb(56, 229, 72, 77));
        static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromArgb(48, 245, 180, 40));

        public Core.Packs.ConfigIssue? Issue { get; set; }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Issue is not { } issue || textView.Document is null || issue.Line > textView.Document.LineCount) return;
            if (textView.GetVisualLine(issue.Line) is not { } line) return; // not on screen
            var top = line.VisualTop - textView.ScrollOffset.Y;
            drawingContext.FillRectangle(issue.IsError ? ErrorBrush : WarningBrush, new Rect(0, top, textView.Bounds.Width, line.Height));
        }
    }
}
