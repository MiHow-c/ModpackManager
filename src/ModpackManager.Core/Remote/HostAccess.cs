using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ModpackManager.Core.Ops;

namespace ModpackManager.Core.Remote;

public sealed class HostUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public List<Permission> Permissions { get; set; } = [];
}

public sealed record PendingInvite(string TokenHash, DateTimeOffset ExpiresAt);

/// <summary>Contents of host.json in the pack's data directory.</summary>
public sealed class HostStateFile
{
    public long Revision { get; set; }
    public int Port { get; set; } = 47500;
    public string PublicAddress { get; set; } = "";
    public List<HostUser> Users { get; set; } = [];
    public List<PendingInvite> Invites { get; set; } = [];
}

/// <summary>
/// Users, invites and the revision of a hosted pack. Tokens are 256-bit random values and only their SHA-256 is
/// stored (with that much entropy a slow KDF adds nothing). Reads host.json on every call so revocation is immediate.
/// </summary>
public sealed class HostAccess(string file)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);

    readonly Lock _lock = new();

    public HostStateFile Snapshot() => Read(s => s);

    public long Revision => Read(s => s.Revision);

    public long BumpRevision() => Update(s => ++s.Revision);

    public void SetEndpoint(int port, string publicAddress) => Update(s => (s.Port, s.PublicAddress) = (port, publicAddress.Trim()));

    /// <summary>Creates a one-time invite token valid for <see cref="InviteLifetime"/>.</summary>
    public string CreateInvite()
    {
        var token = NewToken();
        Update(s =>
        {
            s.Invites.RemoveAll(i => i.ExpiresAt <= DateTimeOffset.UtcNow);
            s.Invites.Add(new PendingInvite(Hash(token), DateTimeOffset.UtcNow + InviteLifetime));
            return 0;
        });
        return token;
    }

    /// <summary>Consumes an invite and creates a user without permissions; null when the invite is unknown, used or expired.</summary>
    public (string UserId, string Token)? Join(string inviteToken, string name)
    {
        var token = NewToken();
        return Update(s =>
        {
            var invite = s.Invites.FirstOrDefault(i => Matches(i.TokenHash, inviteToken) && i.ExpiresAt > DateTimeOffset.UtcNow);
            if (invite is null) return ((string, string)?)null;
            s.Invites.Remove(invite);
            var user = new HostUser { Id = Guid.NewGuid().ToString("N"), Name = name, TokenHash = Hash(token) };
            s.Users.Add(user);
            return (user.Id, token);
        });
    }

    public Principal? Authenticate(string token) => Read(s =>
        s.Users.FirstOrDefault(u => Matches(u.TokenHash, token)) is { } u ? new Principal(u.Id, u.Name, false, u.Permissions.ToHashSet()) : null);

    public void SetPermissions(string userId, IEnumerable<Permission> permissions) =>
        Update(s => s.Users.Single(u => u.Id == userId).Permissions = permissions.Distinct().ToList());

    /// <summary>Revokes access: the user's token stops working on the next request.</summary>
    public void RemoveUser(string userId) => Update(s => s.Users.RemoveAll(u => u.Id == userId));

    T Read<T>(Func<HostStateFile, T> read)
    {
        lock (_lock) return read(JsonFile.Read<HostStateFile>(file));
    }

    T Update<T>(Func<HostStateFile, T> change)
    {
        lock (_lock)
        {
            var state = JsonFile.Read<HostStateFile>(file);
            var result = change(state);
            JsonFile.Write(file, state);
            return result;
        }
    }

    static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static bool Matches(string storedHash, string token) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(token)));
}
