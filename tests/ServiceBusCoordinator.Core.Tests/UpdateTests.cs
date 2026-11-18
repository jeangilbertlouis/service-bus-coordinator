using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceBusCoordinator.Core.Updates;

namespace ServiceBusCoordinator.Core.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("0.6.0", "0.7.0")]
    [InlineData("0.6.0", "0.6.1")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("0.6.0-ci.12", "0.6.0")]
    [InlineData("0.6.0-ci.2", "0.6.0-ci.10")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    public void Orders_by_semver_precedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
        Assert.True(SemanticVersion.Parse(higher) > SemanticVersion.Parse(lower));
    }

    [Fact]
    public void Accepts_tag_prefix_and_ignores_build_metadata()
    {
        Assert.Equal(0, SemanticVersion.Parse("v1.2.3+abc").CompareTo(SemanticVersion.Parse("1.2.3")));
        Assert.Equal("1.2.0", SemanticVersion.Parse("1.2").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3-")]
    public void Rejects_invalid(string text) => Assert.False(SemanticVersion.TryParse(text, out _));
}

public class UpdateCheckerTests
{
    private static JsonElement Release(string tag, bool prerelease = false, bool draft = false, params string[] assets)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            prerelease,
            draft,
            html_url = $"https://github.com/o/r/releases/tag/{tag}",
            body = "notes",
            assets = assets.Select(a => new { name = a, browser_download_url = $"https://github.com/o/r/releases/download/{tag}/{a}", size = 10 }),
        });
        return JsonDocument.Parse(json).RootElement;
    }

    [Theory]
    [InlineData("linux-x64", "ServiceBusCoordinator-0.7.0-linux-x64.tar.gz")]
    [InlineData("win-x64", "ServiceBusCoordinator-0.7.0-win-x64.zip")]
    [InlineData("osx-arm64", "ServiceBusCoordinator-0.7.0-osx-arm64.zip")]
    public void Picks_the_package_for_this_platform(string rid, string expected)
    {
        var update = UpdateChecker.ParseRelease(Release("v0.7.0", assets:
        [
            "ServiceBusCoordinator-0.7.0-linux-x64.tar.gz",
            "ServiceBusCoordinator-0.7.0-win-x64.zip",
            "ServiceBusCoordinator-0.7.0-osx-arm64.zip",
            "SHA256SUMS.txt",
        ]), rid)!;

        Assert.Equal(expected, update.Package!.Name);
        Assert.Equal("SHA256SUMS.txt", update.Checksums!.Name);
        Assert.True(update.CanInstallInPlace);
    }

    [Fact]
    public void Release_without_checksums_cannot_install_in_place()
    {
        var update = UpdateChecker.ParseRelease(Release("v0.7.0", assets: "ServiceBusCoordinator-0.7.0-win-x64.zip"), "win-x64")!;
        Assert.NotNull(update.Package);
        Assert.False(update.CanInstallInPlace);
    }

    [Fact]
    public void Ignores_prereleases_and_drafts()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v0.7.0", prerelease: true), "win-x64"));
        Assert.Null(UpdateChecker.ParseRelease(Release("v0.7.0", draft: true), "win-x64"));
        Assert.Null(UpdateChecker.ParseRelease(Release("v0.7.0-rc.1"), "win-x64"));
    }

    [Fact]
    public async Task Offers_only_newer_versions()
    {
        var http = new HttpClient(new FakeHandler(_ => Json(Release("v0.7.0").GetRawText())));
        var checker = new UpdateChecker(http, "o/r");
        Assert.NotNull(await checker.CheckAsync(SemanticVersion.Parse("0.6.0"), "win-x64"));
        Assert.NotNull(await checker.CheckAsync(SemanticVersion.Parse("0.7.0-ci.5"), "win-x64"));
        Assert.Null(await checker.CheckAsync(SemanticVersion.Parse("0.7.0"), "win-x64"));
        Assert.Null(await checker.CheckAsync(SemanticVersion.Parse("0.8.0"), "win-x64"));
    }

    [Fact]
    public async Task No_releases_means_no_update()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new UpdateChecker(http, "o/r").CheckAsync(SemanticVersion.Parse("0.6.0"), "win-x64"));
    }

    internal static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbc-update-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Detects_single_file_and_macos_bundle_installs()
    {
        Assert.Null(InstallLocation.Detect("/opt/sbc/ServiceBusCoordinator", isSingleFile: false));

        var file = InstallLocation.Detect("/opt/sbc/ServiceBusCoordinator", isSingleFile: true)!;
        Assert.False(file.IsBundle);
        Assert.Equal("/opt/sbc/ServiceBusCoordinator", file.TargetPath);

        // Built with Path.Combine so the expected path uses this OS's separator (Windows normalises '/' to '\').
        var app = Path.Combine(Path.GetTempPath(), "Applications", "Service Bus Coordinator.app");
        var bundle = InstallLocation.Detect(Path.Combine(app, "Contents", "MacOS", "ServiceBusCoordinator"), true)!;
        Assert.True(bundle.IsBundle);
        Assert.Equal(app, bundle.TargetPath);
    }

    [Fact]
    public void Parses_sha256sum_output()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = UpdateInstaller.ParseChecksums($"{a}  one.zip\n{b} *two.tar.gz\nnot a line\n");
        Assert.Equal(a, sums["one.zip"]);
        Assert.Equal(b.ToLowerInvariant(), sums["two.tar.gz"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public async Task Replaces_a_single_file_install_from_a_tarball()
    {
        var install = MakeFileInstall("old build");
        var (update, http) = Serve("linux-x64", MakeTarGz("ServiceBusCoordinator", "new build"));

        var prepared = await new UpdateInstaller(http).PrepareAsync(update, install);
        UpdateInstaller.Apply(prepared, install);

        Assert.Equal("new build", File.ReadAllText(install.TargetPath));
        Assert.Equal("old build", File.ReadAllText(install.BackupPath));
        Assert.False(Directory.Exists(prepared.StagingDirectory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(install.TargetPath).HasFlag(UnixFileMode.UserExecute));
        }

        UpdateInstaller.CleanUp(install);
        Assert.False(File.Exists(install.BackupPath));
        Assert.True(File.Exists(install.TargetPath));
    }

    [Fact]
    public async Task Replaces_a_macos_bundle_from_a_zip()
    {
        var bundle = Path.Combine(_root, "Service Bus Coordinator.app");
        var exe = Path.Combine(bundle, "Contents", "MacOS", "ServiceBusCoordinator");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "old build");
        File.WriteAllText(Path.Combine(bundle, "Contents", "stale.txt"), "only in old");
        var install = InstallLocation.Detect(exe, isSingleFile: true)!;

        var (update, http) = Serve("osx-arm64", MakeZip(
            ("Service Bus Coordinator.app/Contents/MacOS/ServiceBusCoordinator", "new build"),
            ("Service Bus Coordinator.app/Contents/Info.plist", "<plist/>")));

        UpdateInstaller.Apply(await new UpdateInstaller(http).PrepareAsync(update, install), install);

        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.False(File.Exists(Path.Combine(bundle, "Contents", "stale.txt")));
        Assert.True(Directory.Exists(install.BackupPath));
    }

    [Fact]
    public async Task Refuses_a_package_that_does_not_match_its_checksum()
    {
        var install = MakeFileInstall("old build");
        var (update, http) = Serve("linux-x64", MakeTarGz("ServiceBusCoordinator", "new build"), checksumOverride: new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInstaller(http).PrepareAsync(update, install));

        Assert.Equal("old build", File.ReadAllText(install.TargetPath));
        Assert.Single(Directory.GetFileSystemEntries(_root)); // No staging folder left behind.
    }

    [Fact]
    public async Task Refuses_a_package_without_the_executable()
    {
        var install = MakeFileInstall("old build");
        var (update, http) = Serve("linux-x64", MakeTarGz("SomethingElse", "x"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInstaller(http).PrepareAsync(update, install));
        Assert.Equal("old build", File.ReadAllText(install.TargetPath));
    }

    private InstallLocation MakeFileInstall(string content)
    {
        var exe = Path.Combine(_root, "ServiceBusCoordinator");
        File.WriteAllText(exe, content);
        return InstallLocation.Detect(exe, isSingleFile: true)!;
    }

    private static (AvailableUpdate Update, HttpClient Http) Serve(string rid, byte[] package, string? checksumOverride = null)
    {
        var version = SemanticVersion.Parse("0.7.0");
        var name = UpdateChecker.PackageFileName(version, rid);
        var hash = checksumOverride ?? Convert.ToHexStringLower(SHA256.HashData(package));
        var packageUrl = new Uri($"https://example.test/{name}");
        var sumsUrl = new Uri("https://example.test/SHA256SUMS.txt");

        var http = new HttpClient(new FakeHandler(req => req.RequestUri == packageUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {name}\n") }));

        var update = new AvailableUpdate(version, "v0.7.0", new Uri("https://example.test/release"), null,
            new ReleaseAsset(name, packageUrl, package.Length), new ReleaseAsset("SHA256SUMS.txt", sumsUrl, 100));
        return (update, http);
    }

    private static byte[] MakeTarGz(string fileName, string content)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "./" + fileName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            tar.WriteEntry(entry);
        }

        return ms.ToArray();
    }

    private static byte[] MakeZip(params (string Path, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        }

        return ms.ToArray();
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}
