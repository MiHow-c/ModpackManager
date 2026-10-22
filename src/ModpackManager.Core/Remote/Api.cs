using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModpackManager.Core.Ops;

namespace ModpackManager.Core.Remote;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record JoinRequest(string InviteToken, string Name);

public sealed record JoinResponse(string Token);

public sealed record MeResponse(string Name, IReadOnlyList<Permission> Permissions);

public sealed record OpRequest(long BaseRevision, Operation Op);

public sealed record OpResponse(long Revision);

public sealed record ErrorResponse(OpError Error, string? Detail);

/// <summary>Where a client connects and what it trusts: tunnel address, pinned certificate fingerprint and personal token.</summary>
public sealed record Connection(string Address, string Fingerprint, string Token);

/// <summary>mpm1.&lt;address&gt;.&lt;fingerprint&gt;.&lt;one-time token&gt;, address and fingerprint base64url-encoded.</summary>
public sealed record InviteCode(string Address, string Fingerprint, string Token)
{
    const string Prefix = "mpm1";

    public override string ToString() =>
        $"{Prefix}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Address))}.{Base64Url.EncodeToString(Convert.FromHexString(Fingerprint))}.{Token}";

    /// <summary>Null for anything that is not a well-formed code (wrong prefix, bad encoding, address or fingerprint).</summary>
    public static InviteCode? Parse(string text)
    {
        if (text.Trim().Split('.') is not [Prefix, var address, var fingerprint, var token] || token.Length == 0) return null;
        try
        {
            var addr = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(address));
            var fp = Base64Url.DecodeFromChars(fingerprint);
            return fp.Length == 32 && IsHostPort(addr) ? new InviteCode(addr, Convert.ToHexStringLower(fp), token) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool IsHostPort(string address) =>
        Uri.TryCreate($"https://{address}/", UriKind.Absolute, out var uri) && uri.Port is > 0 and < 65536
        && address.EndsWith($":{uri.Port}", StringComparison.Ordinal) && uri.AbsolutePath == "/";
}
