using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ServiceBusCoordinator.Core.Updates;

/// <summary>
/// Where the running app is installed: the single-file executable, or on macOS the <c>.app</c> bundle around it.
/// This is what an update replaces.
/// </summary>
public sealed record InstallLocation(string TargetPath, bool IsBundle, string ExecutablePath)
{
    /// <summary>
    /// Works out the install from the running process. Returns null when the app is not a published single-file build
    /// (for example <c>dotnet run</c>), which cannot update itself.
    /// </summary>
    public static InstallLocation? Detect(string? processPath, bool isSingleFile)
    {
        if (!isSingleFile || string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var macOsDir = Path.GetDirectoryName(processPath);
        var contentsDir = macOsDir is null ? null : Path.GetDirectoryName(macOsDir);
        var bundleDir = contentsDir is null ? null : Path.GetDirectoryName(contentsDir);
        if (bundleDir is not null
            && Path.GetFileName(macOsDir) == "MacOS"
            && Path.GetFileName(contentsDir) == "Contents"
            && bundleDir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallLocation(bundleDir, IsBundle: true, processPath);
        }

        return new InstallLocation(processPath, IsBundle: false, processPath);
    }

    public string ParentDirectory => Path.GetDirectoryName(TargetPath)!;

    /// <summary>Where the previous version is moved during an update, until the next start deletes it.</summary>
    public string BackupPath => TargetPath + ".old";
}

/// <summary>A downloaded, verified and unpacked update, ready to swap in.</summary>
public sealed record PreparedUpdate(SemanticVersion Version, string StagingDirectory, string PayloadPath);

/// <summary>
/// Downloads a release package, checks its SHA-256 against the release's checksum file, unpacks it next to the install
/// and swaps it in place. The running files are renamed rather than overwritten, which every supported OS allows while
/// the app is running, so no helper process is needed. The old copy is removed on the next start.
/// </summary>
public sealed class UpdateInstaller(HttpClient http)
{
    private const string StagingPrefix = ".sbc-update-";
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    public async Task<PreparedUpdate> PrepareAsync(
        AvailableUpdate update, InstallLocation install, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.Package is not { } package || update.Checksums is not { } checksums)
        {
            throw new InvalidOperationException("This release has no package or checksum file for your system. Download it from the release page instead.");
        }

        // Stage next to the install so the final swap is a rename on the same volume.
        var staging = Path.Combine(install.ParentDirectory, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var sums = await http.GetStringAsync(checksums.DownloadUrl, ct);
            var expected = ParseChecksums(sums).GetValueOrDefault(package.Name)
                ?? throw new InvalidOperationException($"{UpdateChecker.ChecksumFileName} has no entry for {package.Name}.");

            var archive = Path.Combine(staging, package.Name);
            var actual = await DownloadAsync(package, archive, progress, ct);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The downloaded {package.Name} does not match its published checksum, so it was not installed.");
            }

            var extractDir = Path.Combine(staging, "extract");
            Directory.CreateDirectory(extractDir);
            await ExtractAsync(archive, extractDir, ct);
            File.Delete(archive);

            var payload = FindPayload(extractDir, install);
            MakeExecutable(install.IsBundle
                ? Path.Combine(payload, "Contents", "MacOS", Path.GetFileName(install.ExecutablePath))
                : payload);

            return new PreparedUpdate(update.Version, staging, payload);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves the running copy aside and the new one into its place. Rolls back if the second move fails.</summary>
    public static void Apply(PreparedUpdate prepared, InstallLocation install)
    {
        var backup = install.BackupPath;
        if (Exists(backup))
        {
            TryDelete(backup);
            if (Exists(backup))
            {
                // A previous backup is still locked (Windows); park this one under a unique name instead.
                backup = $"{install.BackupPath}-{Guid.NewGuid().ToString("N")[..8]}";
            }
        }

        Move(install.TargetPath, backup, install.IsBundle);
        try
        {
            Move(prepared.PayloadPath, install.TargetPath, install.IsBundle);
        }
        catch
        {
            Move(backup, install.TargetPath, install.IsBundle);
            throw;
        }

        TryDelete(prepared.StagingDirectory);
    }

    /// <summary>Starts the (now updated) app as a new process.</summary>
    public static ProcessStartInfo RelaunchInfo(InstallLocation install)
    {
        if (install.IsBundle)
        {
            var open = new ProcessStartInfo("open") { UseShellExecute = false };
            open.ArgumentList.Add("-n");
            open.ArgumentList.Add(install.TargetPath);
            return open;
        }

        return new ProcessStartInfo(install.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = install.ParentDirectory,
        };
    }

    /// <summary>Removes the previous version and any half-finished staging folders. Best-effort; call on startup.</summary>
    public static void CleanUp(InstallLocation install)
    {
        try
        {
            var parent = new DirectoryInfo(install.ParentDirectory);
            var backupPrefix = Path.GetFileName(install.BackupPath);
            foreach (var entry in parent.EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith(backupPrefix, StringComparison.Ordinal)
                    || entry.Name.StartsWith(StagingPrefix, StringComparison.Ordinal) && entry is DirectoryInfo)
                {
                    TryDelete(entry.FullName);
                }
            }
        }
        catch (Exception)
        {
            // Leftovers are harmless; try again next start.
        }
    }

    /// <summary>Parses <c>sha256sum</c> output: <c>&lt;hex&gt;  &lt;name&gt;</c> or <c>&lt;hex&gt; *&lt;name&gt;</c> per line.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64)
            {
                continue;
            }

            var hash = line[..64];
            var name = line[space..].TrimStart().TrimStart('*');
            if (name.Length > 0 && hash.All(Uri.IsHexDigit))
            {
                result[name] = hash.ToLowerInvariant();
            }
        }

        return result;
    }

    private async Task<string> DownloadAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxPackageBytes)
        {
            throw new InvalidDataException($"{asset.Name} is unexpectedly large ({total:N0} bytes).");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            read += n;
            if (read > MaxPackageBytes)
            {
                throw new InvalidDataException($"{asset.Name} is unexpectedly large.");
            }

            sha.AppendData(buffer, 0, n);
            await target.WriteAsync(buffer.AsMemory(0, n), ct);
            if (total > 0)
            {
                progress?.Report((double)read / total);
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        // Both extractors refuse entries that would land outside the destination.
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, ct);
        }
        else
        {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: false, ct);
        }
    }

    private static string FindPayload(string extractDir, InstallLocation install)
    {
        if (install.IsBundle)
        {
            var bundles = Directory.GetDirectories(extractDir, "*.app");
            return bundles.Length == 1
                ? bundles[0]
                : throw new InvalidDataException("The package does not contain exactly one .app bundle.");
        }

        var exe = Path.Combine(extractDir, Path.GetFileName(install.TargetPath));
        return File.Exists(exe)
            ? exe
            : throw new InvalidDataException($"The package does not contain {Path.GetFileName(install.TargetPath)}.");
    }

    private static void MakeExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The package does not contain {Path.GetFileName(path)}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort (a file may still be locked by the process that is exiting).
        }
    }
}
