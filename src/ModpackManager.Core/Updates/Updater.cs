using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModpackManager.Core.Updates;

public sealed record UpdateAsset(string Url, string Sha256);

/// <summary>update.json published with every release.</summary>
public sealed record UpdateManifest(string Version, string? Notes, Dictionary<string, UpdateAsset> Assets);

public sealed record AvailableUpdate(Version Version, string? Notes, UpdateAsset Asset);

/// <summary>
/// Self-update from a release manifest: download the package for this platform, verify its SHA-256, extract it to a
/// staging folder, then the staged new version copies itself over the install folder once this process has exited.
/// </summary>
public sealed class Updater(HttpClient http, Uri manifestUrl, string updateDir)
{
    public const string ApplyArgument = "--apply-update";

    /// <summary>win-x64 / linux-x64: the platform the running build was published for.</summary>
    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    string StagedDir => Path.Combine(updateDir, "staged");

    /// <summary>The newer release for this platform, or null when up to date.</summary>
    public async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct = default)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await ReadAsync(manifestUrl, ct), JsonSerializerOptions.Web)
                       ?? throw new InvalidDataException("Empty update manifest");
        if (!Version.TryParse(manifest.Version, out var version) || Normalize(version) <= Normalize(current)) return null;
        if (!manifest.Assets.TryGetValue(Rid, out var asset)) return null;
        // the package becomes our own executable: it must come over HTTPS (or a local file) with a full SHA-256
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || !(url.Scheme == Uri.UriSchemeHttps || url.IsFile) || asset.Sha256.Length != 64)
            throw new InvalidDataException($"Invalid update asset for {Rid}");
        return new AvailableUpdate(version, manifest.Notes, asset);
    }

    /// <summary>Downloads, verifies and extracts the package; returns the staged executable to start.</summary>
    public async Task<string> StageAsync(AvailableUpdate update, string exeName, CancellationToken ct = default)
    {
        var package = await ReadAsync(new Uri(update.Asset.Url), ct);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(package)), update.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package hash mismatch");

        if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, recursive: true);
        using (var zip = new ZipArchive(new MemoryStream(package)))
            zip.ExtractToDirectory(StagedDir); // throws on entries that would land outside the folder
        var exe = Path.Combine(StagedDir, exeName);
        if (!File.Exists(exe)) throw new InvalidDataException($"Update package has no {exeName}");
        MakeExecutable(exe);
        return exe;
    }

    /// <summary>
    /// Runs in the staged copy: waits for the old process to exit, copies itself over the install folder (restoring the
    /// old files if anything fails) and starts the installed app. Returns the process exit code.
    /// </summary>
    public static int ApplyStaged(string installDir, int oldProcessId)
    {
        try
        {
            using var old = Process.GetProcessById(oldProcessId);
            // still running means its files are locked: give up rather than leave a half-updated install
            if (!old.WaitForExit(TimeSpan.FromSeconds(30))) return 1;
        }
        catch (ArgumentException)
        {
            // already gone
        }

        var stagedDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        try
        {
            ReplaceFiles(stagedDir, installDir, Path.Combine(Path.GetDirectoryName(stagedDir)!, "rollback"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the old version was restored; start it again below
        }
        var installedExe = Path.Combine(installDir, Path.GetFileName(Environment.ProcessPath!));
        MakeExecutable(installedExe);
        Process.Start(new ProcessStartInfo(installedExe) { WorkingDirectory = installDir, UseShellExecute = false });
        return 0;
    }

    /// <summary>Copies every file of <paramref name="source"/> into <paramref name="target"/>; on failure puts the replaced files back.</summary>
    public static void ReplaceFiles(string source, string target, string rollback)
    {
        if (Directory.Exists(rollback)) Directory.Delete(rollback, recursive: true);
        var written = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                if (File.Exists(destination)) CopyWithRetry(destination, Path.Combine(rollback, relative));
                CopyWithRetry(file, destination);
                written.Add(relative);
            }
        }
        catch
        {
            foreach (var relative in written)
            {
                var kept = Path.Combine(rollback, relative);
                if (File.Exists(kept)) CopyWithRetry(kept, Path.Combine(target, relative));
                else File.Delete(Path.Combine(target, relative));
            }
            throw;
        }
    }

    /// <summary>Removes what a finished update left behind; silently skipped while files are still in use.</summary>
    public static void Cleanup(string updateDir)
    {
        try
        {
            if (Directory.Exists(updateDir) && !AppContext.BaseDirectory.StartsWith(Path.GetFullPath(updateDir), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(updateDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // a file just released by the old process can stay locked for a moment (e.g. antivirus scan)
    static void CopyWithRetry(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
        }
    }

    // zips made on Windows carry no Unix permissions, so the Linux executable bit is set explicitly
    static void MakeExecutable(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    async Task<byte[]> ReadAsync(Uri uri, CancellationToken ct) =>
        uri.IsFile ? await File.ReadAllBytesAsync(uri.LocalPath, ct)
        : uri.Scheme == Uri.UriSchemeHttps ? await http.GetByteArrayAsync(uri, ct)
        : throw new InvalidDataException($"Unsupported update URL: {uri}");

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
