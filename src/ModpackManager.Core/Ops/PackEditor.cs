using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ModpackManager.Core.Packs;
using ModpackManager.Core.Remote;
using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;

namespace ModpackManager.Core.Ops;

/// <summary>
/// The single place that changes a pack. Checks path, permission and revision on every operation and runs
/// operations one at a time. The Creator's GUI and the network host both go through here.
/// </summary>
public sealed class PackEditor(IPackStore store, ModrinthClient modrinth, SourceIdentifier identifier, HostAccess access)
{
    readonly SemaphoreSlim _lock = new(1, 1);

    public IPackStore Store => store;

    public HostAccess Access => access;

    public string Root => store.Root;

    public static PackEditor Open(string root, AppPaths paths, ModrinthClient modrinth, SourceIdentifier identifier) =>
        new(File.Exists(Path.Combine(root, "pack.toml")) ? new PackwizStore(root, paths) : new FolderStore(root, paths, modrinth),
            modrinth, identifier, new HostAccess(Path.Combine(paths.PackDir(root), "host.json")));

    /// <summary>Scans and identifies the pack; runs under the operation lock so entries always match the revision.</summary>
    public async Task<(PackManifest Manifest, IReadOnlyList<SourceKind> FailedSources)> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadUnlockedAsync(ct); }
        finally { _lock.Release(); }
    }

    async Task<(PackManifest, IReadOnlyList<SourceKind>)> LoadUnlockedAsync(CancellationToken ct)
    {
        var (info, entries) = await Task.Run(store.Scan, ct);
        var identified = await identifier.IdentifyAsync(store.Root, entries, ct);
        return (new PackManifest(access.Revision, info, identified.Entries), identified.FailedSources);
    }

    /// <summary>A pack file's content; only real files inside the pack can be read.</summary>
    public byte[] ReadFile(string path)
    {
        var full = Path.Combine(store.Root, path);
        if (!store.IsPackPath(path) || !File.Exists(full)) throw new OperationException(OpError.NotFound, path);
        return File.ReadAllBytes(full);
    }

    /// <summary>Applies an operation for <paramref name="who"/>; returns the new pack revision.</summary>
    /// <param name="baseRevision">Revision the caller's view is based on; a stale one is rejected.</param>
    public async Task<long> ApplyAsync(Operation op, Principal who, long? baseRevision = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var path = op switch { UpdateMod u => u.Path, PutFile p => p.Path, DeleteFile d => d.Path, _ => null };
            if (path is not null && !store.IsPackPath(path)) throw new OperationException(OpError.InvalidPath, path);

            // permissions are enforced here, on every operation; the UI only hides what the user cannot do
            var required = Permissions.Required(op, path is not null && path.StartsWith("mods/", StringComparison.Ordinal) && store.Find(path) is not null);
            if (!who.Has(required)) throw new OperationException(OpError.Forbidden, JsonSerializer.Serialize(required).Trim('"'));

            var current = access.Revision;
            if (baseRevision is { } b && b != current)
                throw new OperationException(OpError.StaleRevision, current.ToString(CultureInfo.InvariantCulture));

            await ExecuteAsync(op, ct);
            return access.BumpRevision();
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task ExecuteAsync(Operation op, CancellationToken ct)
    {
        switch (op)
        {
            case AddMod(var projectId, var versionId):
                var mod = await ResolveAsync(projectId, versionId, "mods", ct);
                var (current, _) = await LoadUnlockedAsync(ct);
                if (current.Entries.Any(e => e.Source == SourceKind.Modrinth && e.ProjectId == projectId))
                    throw new OperationException(OpError.AlreadyInstalled, mod.Project.Title);
                await store.WriteModAsync(mod, null, ct);
                break;

            case UpdateMod(var path, var versionId):
                var installed = (await LoadUnlockedAsync(ct)).Item1.Entries.FirstOrDefault(e => e.Path == path)
                                ?? throw new OperationException(OpError.NotFound, path);
                // only a Modrinth-identified mod can move to another version, and only of the same project
                if (installed is not { Kind: EntryKind.Mod, Source: SourceKind.Modrinth, ProjectId: { } installedProject })
                    throw new OperationException(OpError.WrongProject, path);
                await store.WriteModAsync(await ResolveAsync(installedProject, versionId, "mods", ct), path, ct);
                break;

            case PutFile(var path, var content):
                if (ConfigValidator.Validate(path, Encoding.UTF8.GetString(content)) is { } error)
                    throw new OperationException(OpError.SyntaxError, error);
                store.PutFile(path, content);
                break;

            case DeleteFile(var path):
                if (store.Find(path) is null) throw new OperationException(OpError.NotFound, path);
                store.Delete(path);
                break;

            case SetSettings(var mc, var loader, var loaderVersion):
                if (string.IsNullOrWhiteSpace(mc)) throw new OperationException(OpError.InvalidValue, nameof(mc));
                store.SetVersions(mc.Trim(), loader, loaderVersion?.Trim());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }

    /// <summary>Resolves ids to a downloadable file, checking that the version belongs to the project and is a mod.</summary>
    async Task<ResolvedMod> ResolveAsync(string projectId, string versionId, string folder, CancellationToken ct)
    {
        try
        {
            var version = await modrinth.GetVersionAsync(versionId, ct);
            var project = await modrinth.GetProjectAsync(projectId, ct);
            if (version.ProjectId != project.Id) throw new OperationException(OpError.WrongProject, versionId);
            if (project.ProjectType != "mod") throw new OperationException(OpError.NotAMod, project.Title);
            return CheckedMod(project, version, version.PrimaryFile ?? throw new OperationException(OpError.NoFile, versionId), folder);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OperationException(OpError.NotFound, $"{projectId}/{versionId}");
        }
    }

    /// <summary>The file name comes from the network and becomes a path in the pack, so it must stay inside it.</summary>
    public static ResolvedMod CheckedMod(ModrinthProject project, ModrinthVersion version, ModrinthFile file, string folder) =>
        file.Filename.Contains('/') || !PackScanner.IsSafeRelativePath($"{folder}/{file.Filename}")
            ? throw new OperationException(OpError.InvalidPath, file.Filename)
            : new ResolvedMod(project, version, file, folder);
}
