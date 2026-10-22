using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ModpackManager.Core.Ops;

namespace ModpackManager.Core.Remote;

/// <summary>
/// Access to a pack, wherever it lives: in this process (the Creator on the host) or behind a network address.
/// Pack logic only talks to this interface, so the way of connecting (playit tunnel, LAN, VPN) can change freely.
/// </summary>
public interface IPackSession
{
    Principal Me { get; }

    Task<PackManifest> GetManifestAsync(CancellationToken ct = default);

    Task<byte[]> GetFileAsync(string path, CancellationToken ct = default);

    /// <summary>Applies an operation based on <paramref name="baseRevision"/>; returns the new revision.</summary>
    Task<long> ApplyAsync(Operation op, long baseRevision, CancellationToken ct = default);
}

public sealed class LocalPackSession(PackEditor editor) : IPackSession
{
    public Principal Me => Principal.Owner;

    public async Task<PackManifest> GetManifestAsync(CancellationToken ct = default) => (await editor.LoadAsync(ct)).Manifest;

    public Task<byte[]> GetFileAsync(string path, CancellationToken ct = default) => Task.FromResult(editor.ReadFile(path));

    public Task<long> ApplyAsync(Operation op, long baseRevision, CancellationToken ct = default) =>
        editor.ApplyAsync(op, Principal.Owner, baseRevision, ct);
}

/// <summary>HTTPS client of <see cref="HostServer"/>. Trusts exactly the certificate whose fingerprint came in the invite.</summary>
public sealed class RemotePackSession : IPackSession, IDisposable
{
    readonly HttpClient _http;

    RemotePackSession(Connection connection)
    {
        Connection = connection;
        var pin = Convert.FromHexString(connection.Fingerprint);
        var handler = new SocketsHttpHandler
        {
            // pinning replaces CA and host name checks: the tunnel address never matches a self-signed certificate
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin) },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{connection.Address}/"), Timeout = TimeSpan.FromMinutes(5) };
        if (connection.Token.Length > 0) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    }

    public Connection Connection { get; }

    public Principal Me { get; private set; } = new("", "", false, new HashSet<Permission>());

    /// <summary>Exchanges a one-time invite for a personal token and connects with it.</summary>
    public static async Task<RemotePackSession> JoinAsync(InviteCode invite, string name, CancellationToken ct = default)
    {
        string token;
        using (var anonymous = new RemotePackSession(new Connection(invite.Address, invite.Fingerprint, "")))
            token = (await anonymous.SendAsync<JoinResponse>(new HttpRequestMessage(HttpMethod.Post, "api/join")
            {
                Content = JsonContent.Create(new JoinRequest(invite.Token, name), options: ApiJson.Options),
            }, ct)).Token;
        return await ConnectAsync(new Connection(invite.Address, invite.Fingerprint, token), ct);
    }

    public static async Task<RemotePackSession> ConnectAsync(Connection connection, CancellationToken ct = default)
    {
        var session = new RemotePackSession(connection);
        try
        {
            var me = await session.SendAsync<MeResponse>(new HttpRequestMessage(HttpMethod.Get, "api/me"), ct);
            session.Me = new Principal("", me.Name, false, me.Permissions.ToHashSet());
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<PackManifest> GetManifestAsync(CancellationToken ct = default) =>
        SendAsync<PackManifest>(new HttpRequestMessage(HttpMethod.Get, "api/manifest"), ct);

    public async Task<byte[]> GetFileAsync(string path, CancellationToken ct = default)
    {
        var url = "api/files/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var response = await SendRawAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<long> ApplyAsync(Operation op, long baseRevision, CancellationToken ct = default) =>
        (await SendAsync<OpResponse>(new HttpRequestMessage(HttpMethod.Post, "api/ops")
        {
            Content = JsonContent.Create(new OpRequest(baseRevision, op), options: ApiJson.Options),
        }, ct)).Revision;

    async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendRawAsync(request, ct);
        return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct) ?? throw new JsonException("Empty response");
    }

    /// <summary>Host errors come back as <see cref="OperationException"/> with the host's error code.</summary>
    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                ErrorResponse? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ApiJson.Options, ct); }
                catch (JsonException) { }
                throw new OperationException(error?.Error ?? response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => OpError.Unauthorized,
                    HttpStatusCode.NotFound => OpError.NotFound,
                    _ => OpError.InvalidValue,
                }, error?.Detail ?? $"HTTP {(int)response.StatusCode}");
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
