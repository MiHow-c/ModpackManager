using Avalonia.Controls;

namespace ModpackManager.App.Views.Pages;

public partial class ModsPage : UserControl
{
    public ModsPage() => InitializeComponent();
}

public partial class StatsPage : UserControl
{
    public StatsPage() => InitializeComponent();

    // the carousel attaches a page when it is shown: compute statistics on first view, not on every pack reload
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ViewModels.MainViewModel)?.EnsureStats();
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
