using ModpackManager.Core.Scanning;
using ModpackManager.Core.Sources;

namespace ModpackManager.Core.Packs;

/// <summary>Plain instance folder: mods are real jars downloaded from their source.</summary>
public sealed class FolderStore(string root, AppPaths paths, ModrinthClient modrinth) : IPackStore
{
    readonly Backups _backups = new(paths, root);

    public string Root => root;

    public (PackInfo Info, List<PackEntry> Entries) Scan()
    {
        var (info, entries) = PackScanner.Scan(root);
        var s = JsonFile.Read<PackSettings>(paths.PackSettings(root));
        return (info with
        {
            McVersion = s.McVersion ?? info.McVersion,
            Loader = s.Loader ?? info.Loader,
            LoaderVersion = s.Loader is null ? info.LoaderVersion : s.LoaderVersion,
        }, entries);
    }

    public PackEntry? Find(string path)
    {
        var full = Path.Combine(root, path);
        return File.Exists(full) ? PackScanner.FileEntry(root, full) : null;
    }

    // only the synced folders, so an operation can never touch saves, options.txt or launcher files
    public bool IsPackPath(string path) =>
        PackScanner.IsSafeRelativePath(path) && path.Contains('/') && PackScanner.TrackedRoots.Contains(path[..path.IndexOf('/')]);

    public async Task<string> WriteModAsync(ResolvedMod mod, string? replacing, CancellationToken ct)
    {
        var path = $"{mod.Folder}/{mod.File.Filename}";
        var bytes = await modrinth.DownloadAsync(mod.File, ct); // download first: a failure leaves the old version in place
        _backups.Write(path, bytes);
        if (replacing is not null && replacing != path) _backups.Move(replacing);
        return path;
    }

    public void PutFile(string path, byte[] content) => _backups.Write(path, content);

    public void Delete(string path) => _backups.Move(path);

    public void SetVersions(string mcVersion, Loader loader, string? loaderVersion)
    {
        var file = paths.PackSettings(root);
        var s = JsonFile.Read<PackSettings>(file);
        (s.McVersion, s.Loader, s.LoaderVersion) = (mcVersion, loader, loaderVersion);
        JsonFile.Write(file, s);
    }
}
