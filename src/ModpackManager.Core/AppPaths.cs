using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModpackManager.Core;

/// <summary>Per-user data directory: %APPDATA%/ModpackManager or ~/.config/ModpackManager, overridable by MPM_DATA_DIR.</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default() => new(
        Environment.GetEnvironmentVariable("MPM_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModpackManager"));

    public string Root => root;

    public string LookupCache => Path.Combine(root, "lookup-cache.json");

    /// <summary>Data kept for one pack (settings, backups, host state), keyed by the pack's full path.</summary>
    public string PackDir(string packRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packRoot));
        return Path.Combine(root, "packs", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16]);
    }

    public string PackSettings(string packRoot) => Path.Combine(PackDir(packRoot), "settings.json");
}

public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static T Read<T>(string path) where T : new() =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // write-then-move so a crash never leaves a half-written file
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        // these files hold tokens and token hashes: owner-only on Unix (Windows profile dirs already are)
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Host-local settings of one pack, stored in the data directory (never inside the pack).</summary>
public sealed class PackSettings
{
    /// <summary>Manually assigned source links, keyed by file SHA-1.</summary>
    public Dictionary<string, string> ManualLinks { get; set; } = [];

    /// <summary>Manual MC version/loader for folder packs; overrides mmc-pack.json when set.</summary>
    public string? McVersion { get; set; }

    public Loader? Loader { get; set; }

    public string? LoaderVersion { get; set; }

    /// <summary>Host this local copy syncs with (address, pinned fingerprint, personal token).</summary>
    public Remote.Connection? Connection { get; set; }
}
