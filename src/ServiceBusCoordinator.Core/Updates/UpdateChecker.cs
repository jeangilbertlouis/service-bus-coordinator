using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ServiceBusCoordinator.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A published release, and the files for this platform when the release has them.</summary>
public sealed record AvailableUpdate(
    SemanticVersion Version,
    string TagName,
    Uri ReleasePage,
    string? Notes,
    ReleaseAsset? Package,
    ReleaseAsset? Checksums)
{
    /// <summary>Whether the app can install this update itself: it has a package for this platform and a checksum file.</summary>
    public bool CanInstallInPlace => Package is not null && Checksums is not null;
}

/// <summary>Asks GitHub for the latest stable release and compares it with the running version.</summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "jeangilbertlouis/service-bus-coordinator";

    /// <summary>Name of the checksum file attached to each release (sha256sum format).</summary>
    public const string ChecksumFileName = "SHA256SUMS.txt";

    /// <summary>
    /// Returns the latest release when it is newer than <paramref name="currentVersion"/>, otherwise null.
    /// Pre-releases and drafts are never offered (GitHub's "latest" excludes them).
    /// </summary>
    public async Task<AvailableUpdate?> CheckAsync(SemanticVersion currentVersion, string runtimeId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ServiceBusCoordinator", currentVersion.ToString()));

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // No releases yet.
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var update = ParseRelease(doc.RootElement, runtimeId);
        return update is not null && update.Version > currentVersion ? update : null;
    }

    /// <summary>Reads a GitHub release object. Returns null for drafts, pre-releases or unparseable tags.</summary>
    public static AvailableUpdate? ParseRelease(JsonElement release, string runtimeId)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
        {
            return null;
        }

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!SemanticVersion.TryParse(tag, out var version) || version.IsPrerelease)
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list))
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    assets.Add(new ReleaseAsset(name, uri, a.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
                }
            }
        }

        var packageName = PackageFileName(version, runtimeId);
        var page = release.TryGetProperty("html_url", out var html) && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var htmlUri)
            ? htmlUri
            : new Uri($"https://github.com/{DefaultRepository}/releases/tag/{tag}");

        return new AvailableUpdate(
            version,
            tag,
            page,
            release.TryGetProperty("body", out var body) ? body.GetString() : null,
            assets.FirstOrDefault(a => a.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(a => a.Name.Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The archive name build/publish.* produces for a version and runtime.</summary>
    public static string PackageFileName(SemanticVersion version, string runtimeId) =>
        $"ServiceBusCoordinator-{version}-{runtimeId}" + (runtimeId.StartsWith("linux-", StringComparison.Ordinal) ? ".tar.gz" : ".zip");

    /// <summary>The runtime this app was published for, e.g. <c>win-x64</c> or <c>osx-arm64</c>.</summary>
    public static string CurrentRuntimeId()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }
}
