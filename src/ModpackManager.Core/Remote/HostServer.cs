using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModpackManager.Core.Ops;

namespace ModpackManager.Core.Remote;

/// <summary>
/// HTTPS JSON API of a hosted pack, embedded Kestrel on 127.0.0.1 only: the user's playit.gg agent on the same
/// machine forwards the public TCP tunnel to this port. Every request except join needs a personal token.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly X509Certificate2 _certificate;

    HostServer(WebApplication app, X509Certificate2 certificate, int port) =>
        (_app, _certificate, Port, Fingerprint) = (app, certificate, port, HostCertificate.Fingerprint(certificate));

    public int Port { get; }

    public string Fingerprint { get; }

    /// <summary>Starts serving; the server owns <paramref name="certificate"/> and disposes it when stopped.</summary>
    public static async Task<HostServer> StartAsync(PackEditor editor, X509Certificate2 certificate, int port, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // shortcut: files travel base64 in JSON; 100 MB covers configs and typical resource packs, raise if needed
            kestrel.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate));
        });
        var app = builder.Build();
        Map(app, editor);
        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            await app.DisposeAsync();
            certificate.Dispose();
            throw;
        }
        return new HostServer(app, certificate, new Uri(app.Urls.First()).Port);
    }

    static void Map(WebApplication app, PackEditor editor)
    {
        var access = editor.Access;

        // authenticate before any body is read, so anonymous clients cannot make the host parse large payloads
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/api/join")
                ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 16 * 1024;
            else if (Token(ctx) is not { } token || access.Authenticate(token) is not { } who)
            {
                await Error(OpError.Unauthorized, null).ExecuteAsync(ctx);
                return;
            }
            else ctx.Items[nameof(Principal)] = who;
            await next(ctx);
        });

        app.MapPost("/api/join", (JoinRequest request) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 40) return Error(OpError.InvalidValue, "name");
            return access.Join(request.InviteToken ?? "", name) is var (_, token) ? Results.Ok(new JoinResponse(token)) : Error(OpError.Unauthorized, null);
        });

        app.MapGet("/api/me", (HttpContext ctx) =>
        {
            var who = Who(ctx);
            return Results.Ok(new MeResponse(who.Name, who.Permissions.Order().ToList()));
        });

        app.MapGet("/api/manifest", async (CancellationToken ct) => Results.Ok((await editor.LoadAsync(ct)).Manifest));

        app.MapGet("/api/files/{**path}", (string path) =>
        {
            try { return Results.Bytes(editor.ReadFile(path)); }
            catch (OperationException e) { return Error(e.Error, e.Detail); }
        });

        app.MapPost("/api/ops", async (HttpContext ctx, OpRequest request, CancellationToken ct) =>
        {
            try { return Results.Ok(new OpResponse(await editor.ApplyAsync(request.Op, Who(ctx), request.BaseRevision, ct))); }
            catch (OperationException e) { return Error(e.Error, e.Detail); }
            catch (HttpRequestException e) { return Results.Json(new ErrorResponse(OpError.NoFile, e.Message), statusCode: StatusCodes.Status502BadGateway); }
        });
    }

    static Principal Who(HttpContext ctx) => (Principal)ctx.Items[nameof(Principal)]!;

    static string? Token(HttpContext ctx) =>
        ctx.Request.Headers.Authorization.ToString() is var h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h["Bearer ".Length..] : null;

    static IResult Error(OpError error, string? detail) => Results.Json(new ErrorResponse(error, detail), statusCode: error switch
    {
        OpError.Unauthorized => StatusCodes.Status401Unauthorized,
        OpError.Forbidden => StatusCodes.Status403Forbidden,
        OpError.NotFound => StatusCodes.Status404NotFound,
        OpError.StaleRevision => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose(); // on Windows this also deletes the temporary key from the user's key store
    }
}
