using System.Security.Authentication;
using ModpackManager.Core;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Remote;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;
using ModpackManager.Core.Sync;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

/// <summary>Real Kestrel with TLS on 127.0.0.1 and a random port, talked to through RemotePackSession.</summary>
public sealed class HostTests : IAsyncLifetime
{
    readonly AppPaths _paths = new(TestPacks.NewDir("data"));
    readonly FakeModrinth _modrinth = new();
    readonly string _host = TestPacks.Folder();
    readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    PackEditor _editor = null!;
    HostServer _server = null!;

    string Address => $"127.0.0.1:{_server.Port}";

    public async ValueTask InitializeAsync()
    {
        _editor = PackEditor.Open(_host, _paths, _modrinth.Client, new SourceIdentifier(_modrinth.Client, null, _paths));
        var certificate = HostCertificate.LoadOrCreate(Path.Combine(_paths.Root, "host-cert.pfx"));
        _server = await HostServer.StartAsync(_editor, certificate, 0, _ct);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    InviteCode Invite() => new(Address, _server.Fingerprint, _editor.Access.CreateInvite());

    /// <summary>Joins through an invite code (as text, like a user would paste it) and grants the given permissions.</summary>
    async Task<RemotePackSession> JoinAs(string name, params Permission[] permissions)
    {
        var session = await RemotePackSession.JoinAsync(InviteCode.Parse(Invite().ToString())!, name, _ct);
        _editor.Access.SetPermissions(_editor.Access.Snapshot().Users.Single(u => u.Name == name).Id, permissions);
        return session;
    }

    [Fact]
    public async Task Moderator_without_mods_remove_is_refused_and_the_Creator_removes_the_mod()
    {
        using var moderator = await JoinAs("Moderator", Permission.ModsAdd, Permission.ModsUpdate, Permission.ConfigsEdit, Permission.FilesManage);
        var revision = (await moderator.GetManifestAsync(_ct)).Revision;

        var refused = await Assert.ThrowsAsync<OperationException>(() => moderator.ApplyAsync(new DeleteFile("mods/neo.jar"), revision, _ct));

        Assert.Equal((OpError.Forbidden, "mods.remove"), (refused.Error, refused.Detail));
        Assert.True(File.Exists(Path.Combine(_host, "mods", "neo.jar")));

        await new LocalPackSession(_editor).ApplyAsync(new DeleteFile("mods/neo.jar"), revision, _ct);

        var after = await moderator.GetManifestAsync(_ct);
        Assert.Equal(revision + 1, after.Revision);
        Assert.DoesNotContain(after.Entries, e => e.Path == "mods/neo.jar");
        Assert.False(File.Exists(Path.Combine(_host, "mods", "neo.jar")));
    }

    [Fact]
    public async Task New_users_can_only_read_until_the_Creator_grants_permissions()
    {
        using var viewer = await RemotePackSession.JoinAsync(Invite(), "Viewer", _ct);
        var manifest = await viewer.GetManifestAsync(_ct);

        Assert.Empty(viewer.Me.Permissions);
        Assert.Equal("x = 1\n", System.Text.Encoding.UTF8.GetString(await viewer.GetFileAsync("config/a.toml", _ct)));
        var refused = await Assert.ThrowsAsync<OperationException>(() =>
            viewer.ApplyAsync(new PutFile("config/a.toml", "x = 9"u8.ToArray()), manifest.Revision, _ct));
        Assert.Equal(OpError.Forbidden, refused.Error);
    }

    [Fact]
    public async Task Wrong_token_wrong_fingerprint_reused_invite_and_revoked_user_are_rejected()
    {
        var wrongToken = await Assert.ThrowsAsync<OperationException>(() =>
            RemotePackSession.ConnectAsync(new Connection(Address, _server.Fingerprint, "not-a-token"), _ct));
        Assert.Equal(OpError.Unauthorized, wrongToken.Error);

        var invite = Invite();
        using var user = await RemotePackSession.JoinAsync(invite, "User", _ct);
        var reused = await Assert.ThrowsAsync<OperationException>(() => RemotePackSession.JoinAsync(invite, "Again", _ct));
        Assert.Equal(OpError.Unauthorized, reused.Error);

        // a certificate other than the pinned one: the client refuses the TLS handshake before sending the token
        string otherFingerprint;
        using (var other = HostCertificate.LoadOrCreate(Path.Combine(TestPacks.NewDir("other"), "c.pfx")))
            otherFingerprint = HostCertificate.Fingerprint(other);
        var mitm = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RemotePackSession.ConnectAsync(user.Connection with { Fingerprint = otherFingerprint }, _ct));
        Assert.IsType<AuthenticationException>(mitm.InnerException);

        _editor.Access.RemoveUser(_editor.Access.Snapshot().Users.Single(u => u.Name == "User").Id);
        var revoked = await Assert.ThrowsAsync<OperationException>(() => user.GetManifestAsync(_ct));
        Assert.Equal(OpError.Unauthorized, revoked.Error);
        Assert.DoesNotContain(user.Connection.Token, File.ReadAllText(Path.Combine(_paths.PackDir(_host), "host.json")));
    }

    [Fact]
    public async Task Operation_based_on_a_stale_revision_is_rejected_until_changes_are_pulled()
    {
        using var moderator = await JoinAs("Moderator", Permission.ConfigsEdit);
        var revision = (await moderator.GetManifestAsync(_ct)).Revision;
        await moderator.ApplyAsync(new PutFile("config/a.toml", "x = 5\n"u8.ToArray()), revision, _ct);

        var stale = await Assert.ThrowsAsync<OperationException>(() =>
            moderator.ApplyAsync(new PutFile("config/a.toml", "x = 6\n"u8.ToArray()), revision, _ct));

        Assert.Equal((OpError.StaleRevision, $"{revision + 1}"), (stale.Error, stale.Detail));
        Assert.Equal("x = 5\n", File.ReadAllText(Path.Combine(_host, "config", "a.toml")));
        var fresh = (await moderator.GetManifestAsync(_ct)).Revision;
        await moderator.ApplyAsync(new PutFile("config/a.toml", "x = 6\n"u8.ToArray()), fresh, _ct);
        Assert.Equal("x = 6\n", File.ReadAllText(Path.Combine(_host, "config", "a.toml")));
    }

    [Theory]
    [InlineData("saves/world/level.dat")]
    [InlineData("mmc-pack.json")]
    [InlineData("../outside.txt")]
    [InlineData("config/missing.toml")]
    public async Task Files_endpoint_serves_only_files_of_the_pack(string path)
    {
        using var viewer = await RemotePackSession.JoinAsync(Invite(), "Viewer", _ct);

        await Assert.ThrowsAsync<OperationException>(() => viewer.GetFileAsync(path, _ct));
    }

    [Fact]
    public async Task Client_pulls_the_whole_pack_through_the_encrypted_connection()
    {
        using var viewer = await RemotePackSession.JoinAsync(Invite(), "Viewer", _ct);
        var local = TestPacks.NewDir("client");
        var identifier = new SourceIdentifier(_modrinth.Client, null, _paths);
        var hostManifest = await viewer.GetManifestAsync(_ct);
        var diff = ManifestDiff.Compare(hostManifest.Entries, []);

        var result = await new Puller(_modrinth.Client, null, new HttpClient(_modrinth.Http))
            .PullAsync(viewer, diff, new Core.Packs.FolderStore(local, _paths, _modrinth.Client), _ct);

        Assert.Empty(result.Skipped);
        var localEntries = (await identifier.IdentifyAsync(local, PackScanner.Scan(local).Entries, _ct)).Entries;
        Assert.Empty(ManifestDiff.Compare(hostManifest.Entries, localEntries));
    }
}
