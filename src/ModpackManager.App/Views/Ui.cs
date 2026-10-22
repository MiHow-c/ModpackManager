using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FluentIcons.Common;
using ModpackManager.Core;
using ModpackManager.Core.Sync;

namespace ModpackManager.App.Views;

/// <summary>Converters for loader icons and status badges (icon + text + tone, never color alone).</summary>
public static class Ui
{
    public static readonly FuncValueConverter<object?, IImage?> LoaderIcon = new(l => l is Loader loader ? Find($"LoaderIcon.{loader}") as IImage : null);

    public static readonly FuncValueConverter<object?, IBrush?> BadgeBackground = new(k => Find($"Badge.{Tone(k)}.Background") as IBrush);

    public static readonly FuncValueConverter<object?, IBrush?> BadgeForeground = new(k => Find($"Badge.{Tone(k)}.Foreground") as IBrush);

    public static readonly FuncValueConverter<object?, Symbol> BadgeSymbol = new(k => k switch
    {
        SourceKind.Modrinth or SourceKind.CurseForge => Symbol.CheckmarkCircle,
        SourceKind.Url => Symbol.Link,
        SourceKind.Manual => Symbol.Person,
        SourceKind.Unknown => Symbol.QuestionCircle,
        DiffKind.Missing => Symbol.ArrowDownload,
        DiffKind.Extra => Symbol.Delete,
        DiffKind.DifferentVersion => Symbol.ArrowSwap,
        _ => Symbol.Edit,
    });

    static string Tone(object? kind) => kind switch
    {
        SourceKind.Modrinth => "Success",
        SourceKind.CurseForge or DiffKind.DifferentVersion => "Orange",
        SourceKind.Url or DiffKind.Missing => "Info",
        SourceKind.Unknown or DiffKind.Extra => "Warning",
        _ => "Neutral",
    };

    // shortcut: resolved once per binding, so badges pick up an OS light/dark switch only when their list refreshes
    static object? Find(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
}
