using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModpackManager.App.Resources;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Remote;
using ModpackManager.Core.Sync;

namespace ModpackManager.App.ViewModels;

/// <summary>Collaboration tab: hosting the pack (Creator) or joining a host and syncing with it.</summary>
public sealed partial class MainViewModel
{
    HostServer? _server;
    RemotePackSession? _remote;
    List<DiffItem> _diff = [];
    PackInfo? _hostInfo;

    public ObservableCollection<UserRow> Users { get; } = [];

    public ObservableCollection<DiffRow> DiffRows { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartHostCommand), nameof(StopHostCommand), nameof(CreateInviteCommand), nameof(JoinCommand), nameof(ReconnectCommand))]
    public partial bool IsHosting { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartHostCommand), nameof(JoinCommand), nameof(ReconnectCommand), nameof(DisconnectCommand), nameof(CompareCommand), nameof(PullCommand))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty] public partial string HostPort { get; set; } = "47500";

    [ObservableProperty] public partial string PublicAddress { get; set; } = "";

    [ObservableProperty] public partial string HostInfo { get; set; } = Strings.HostStopped;

    [ObservableProperty] public partial string InviteText { get; set; } = "";

    [ObservableProperty] public partial string JoinCode { get; set; } = "";

    [ObservableProperty] public partial string UserName { get; set; } = Environment.UserName;

    [ObservableProperty] public partial string ConnectionInfo { get; set; } = Strings.NotConnected;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    public partial bool HasSavedConnection { get; set; }

    /// <summary>Who edits: the Creator in-process, or this user on the connected host.</summary>
    IPackSession Session => (IPackSession?)_remote ?? new LocalPackSession(_editor!);

    void LoadCollabState()
    {
        var host = _editor!.Access.Snapshot();
        (HostPort, PublicAddress) = (host.Port.ToString(CultureInfo.InvariantCulture), host.PublicAddress);
        HasSavedConnection = JsonFile.Read<PackSettings>(paths.PackSettings(_editor.Root)).Connection is not null;
        RefreshUsers();
    }

    // ---- host (Creator) ----

    [RelayCommand(CanExecute = nameof(CanStartHost))]
    Task StartHost() => RunAsync(async () =>
    {
        if (!int.TryParse(HostPort, out var port) || port is < 1024 or > 65535) throw new OperationException(OpError.InvalidValue, HostPort);
        _editor!.Access.SetEndpoint(port, PublicAddress);
        var certificate = HostCertificate.LoadOrCreate(Path.Combine(paths.Root, "host-cert.pfx"));
        _server = await HostServer.StartAsync(_editor, certificate, port);
        IsHosting = true;
        HostInfo = string.Format(Strings.HostRunning, _server.Port, _server.Fingerprint);
    });

    bool CanStartHost() => !IsHosting && !IsConnected;

    [RelayCommand(CanExecute = nameof(IsHosting))]
    Task StopHost() => RunAsync(StopServerAsync);

    async Task StopServerAsync()
    {
        if (_server is null) return;
        await _server.DisposeAsync();
        _server = null;
        IsHosting = false;
        HostInfo = Strings.HostStopped;
    }

    [RelayCommand(CanExecute = nameof(IsHosting))]
    void CreateInvite()
    {
        if (!InviteCode.IsHostPort(PublicAddress.Trim()))
        {
            Status = Strings.InviteNeedsAddress;
            return;
        }
        _editor!.Access.SetEndpoint(_server!.Port, PublicAddress);
        InviteText = new InviteCode(PublicAddress.Trim(), _server.Fingerprint, _editor.Access.CreateInvite()).ToString();
        Status = Strings.InviteHint;
    }

    [RelayCommand]
    void RefreshUsers()
    {
        Users.Clear();
        foreach (var user in _editor?.Access.Snapshot().Users ?? [])
            Users.Add(new UserRow(user, _editor!.Access, RefreshUsers));
    }

    // ---- client ----

    [RelayCommand(CanExecute = nameof(CanConnect))]
    Task Join() => RunAsync(async () =>
    {
        var invite = InviteCode.Parse(JoinCode) ?? throw new OperationException(OpError.InvalidValue, Strings.InvalidInvite);
        UseRemote(await RemotePackSession.JoinAsync(invite, UserName.Trim()));
        JoinCode = "";
        await CompareAsync();
    });

    bool CanConnect() => !IsConnected && !IsHosting;

    [RelayCommand(CanExecute = nameof(CanReconnect))]
    Task Reconnect() => RunAsync(async () =>
    {
        var saved = JsonFile.Read<PackSettings>(paths.PackSettings(_editor!.Root)).Connection!;
        UseRemote(await RemotePackSession.ConnectAsync(saved));
        await CompareAsync();
    });

    bool CanReconnect() => CanConnect() && HasSavedConnection;

    void UseRemote(RemotePackSession remote)
    {
        _remote = remote;
        var file = paths.PackSettings(_editor!.Root);
        var settings = JsonFile.Read<PackSettings>(file);
        settings.Connection = remote.Connection;
        JsonFile.Write(file, settings);
        HasSavedConnection = true;
        IsConnected = true;
        var permissions = remote.Me.Permissions.Count == 0 ? Strings.NoPermissions : string.Join(", ", remote.Me.Permissions.Order().Select(PermissionLabel));
        ConnectionInfo = string.Format(Strings.Connected, remote.Connection.Address, remote.Me.Name, permissions);
        NotifyPermissionCommands();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    void Disconnect()
    {
        _remote?.Dispose();
        _remote = null;
        IsConnected = false;
        ConnectionInfo = Strings.NotConnected;
        DiffRows.Clear();
        NotifyPermissionCommands();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    Task Compare() => RunAsync(CompareAsync);

    async Task CompareAsync()
    {
        var host = await _remote!.GetManifestAsync();
        _baseRevision = host.Revision;
        _hostInfo = host.Info;
        var local = (await _editor!.LoadAsync()).Manifest;
        _diff = ManifestDiff.Compare(host.Entries, local.Entries);
        DiffRows.Clear();
        foreach (var d in _diff) DiffRows.Add(new DiffRow(d));
        Status = _diff.Count == 0 ? Strings.DiffEmpty : string.Format(Strings.DiffSummary, _diff.Count, host.Revision);
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    Task Pull() => RunAsync(PullAsync);

    async Task PullAsync()
    {
        await CompareAsync();
        var result = await puller.PullAsync(_remote!, _diff, _editor!.Store);
        await ReloadAsync();
        await CompareAsync();
        Status = string.Join(" ", new[] { string.Format(Strings.Pulled, result.Applied) }
            .Concat(result.Skipped.Count > 0 ? [string.Format(Strings.PullSkipped, string.Join(", ", result.Skipped))] : []));
    }

    public async Task ShutdownAsync()
    {
        _remote?.Dispose();
        await StopServerAsync(); // also removes the certificate key from the Windows key store
    }

    /// <summary>
    /// On app exit (UI thread, which must block): stop the host on the thread pool, since awaiting on the UI
    /// context here would deadlock and leave the certificate key in the Windows key store.
    /// </summary>
    public void Shutdown()
    {
        _remote?.Dispose();
        if (_server is { } server) Task.Run(() => server.DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }

    static string PermissionLabel(Permission p) => Strings.ResourceManager.GetString($"Perm{p}", Strings.Culture) ?? p.ToString();
}

/// <summary>A user of the hosted pack; toggling a permission saves it to host.json immediately.</summary>
public sealed partial class UserRow(HostUser user, HostAccess access, Action changed) : ObservableObject
{
    public string Name => user.Name;

    public bool ModsAdd { get => Has(Permission.ModsAdd); set => Set(Permission.ModsAdd, value); }
    public bool ModsRemove { get => Has(Permission.ModsRemove); set => Set(Permission.ModsRemove, value); }
    public bool ModsUpdate { get => Has(Permission.ModsUpdate); set => Set(Permission.ModsUpdate, value); }
    public bool ConfigsEdit { get => Has(Permission.ConfigsEdit); set => Set(Permission.ConfigsEdit, value); }
    public bool FilesManage { get => Has(Permission.FilesManage); set => Set(Permission.FilesManage, value); }
    public bool PackSettings { get => Has(Permission.PackSettings); set => Set(Permission.PackSettings, value); }

    bool Has(Permission p) => user.Permissions.Contains(p);

    void Set(Permission p, bool on)
    {
        if (on == Has(p)) return;
        if (on) user.Permissions.Add(p); else user.Permissions.Remove(p);
        access.SetPermissions(user.Id, user.Permissions);
    }

    [RelayCommand]
    void Revoke()
    {
        access.RemoveUser(user.Id);
        changed();
    }
}

public sealed class DiffRow(DiffItem item)
{
    public string Kind => Strings.ResourceManager.GetString($"Diff{item.Kind}", Strings.Culture) ?? item.Kind.ToString();
    public DiffKind KindValue => item.Kind;
    public string Path => item.Path;
    public string HostVersion => Describe(item.Host);
    public string LocalVersion => Describe(item.Local);

    static string Describe(PackEntry? e) => e is null ? Strings.NotSet : e.Mod?.Version ?? e.VersionId ?? System.IO.Path.GetFileName(e.Path);
}
