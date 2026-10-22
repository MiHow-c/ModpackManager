using System.Globalization;

namespace ModpackManager.Core.Packs;

/// <summary>Moves files that are about to be overwritten or deleted into the data dir, never into the pack.</summary>
public sealed class Backups(AppPaths paths, string packRoot)
{
    public string Dir => Path.Combine(paths.PackDir(packRoot), "backups");

    /// <summary>Moves the pack file to backups/&lt;timestamp&gt;/&lt;path&gt;; does nothing when the file does not exist.</summary>
    public void Move(string relPath)
    {
        var source = Path.Combine(packRoot, relPath);
        if (!File.Exists(source)) return;
        var target = Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture), relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
    }

    /// <summary>Backs up the current version first, so a failed write never loses the old file.</summary>
    public void Write(string relPath, byte[] content)
    {
        Move(relPath);
        var full = Path.Combine(packRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }
}
