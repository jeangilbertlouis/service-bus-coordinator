using System.Diagnostics;
using System.Reflection;
using ServiceBusCoordinator.Core.Updates;

namespace ServiceBusCoordinator.App.Services;

/// <summary>Checks GitHub for a newer release and installs it over the running copy.</summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Set to 1 to stop the app checking for updates on startup.</summary>
    public const string DisableCheckVariable = "SBC_DISABLE_UPDATE_CHECK";

    public static string CurrentVersionText { get; } = typeof(UpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    public static SemanticVersion? CurrentVersion { get; } =
        SemanticVersion.TryParse(CurrentVersionText, out var v) ? v : null;

    /// <summary>The install to replace, or null when running from source (which cannot update itself).</summary>
    public static InstallLocation? Install { get; } = DetectInstall();

    public static bool StartupCheckEnabled => Environment.GetEnvironmentVariable(DisableCheckVariable) != "1";

    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        if (CurrentVersion is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await new UpdateChecker(Http).CheckAsync(CurrentVersion, UpdateChecker.CurrentRuntimeId(), timeout.Token);
    }

    /// <summary>Downloads, verifies and swaps in the update. The caller then restarts the app.</summary>
    public static async Task InstallAsync(AvailableUpdate update, IProgress<double> progress, CancellationToken ct)
    {
        var install = Install ?? throw new InvalidOperationException("This copy is running from source and cannot update itself.");
        var prepared = await new UpdateInstaller(Http).PrepareAsync(update, install, progress, ct);

        // A single-file app loads its assemblies from its own executable on first use. On Windows that happens by
        // path, so once the executable is swapped, anything not yet loaded (for example System.IO.Pipes, needed to
        // start the new version) can no longer be found. Load everything the app can reach while it still is.
        PreloadReferencedAssemblies();
        UpdateInstaller.Apply(prepared, install);
    }

    /// <summary>Loads every assembly reachable from the app's references, so none has to be read after the swap.</summary>
    private static void PreloadReferencedAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<Assembly>(AppDomain.CurrentDomain.GetAssemblies());
        foreach (var loaded in pending)
        {
            seen.Add(loaded.GetName().Name ?? "");
        }

        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                {
                    continue;
                }

                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch (Exception)
                {
                    // Some references only exist on other platforms; the app never needs those here.
                }
            }
        }
    }

    /// <summary>Starts the newly installed version.</summary>
    public static void Relaunch()
    {
        if (Install is { } install)
        {
            Process.Start(UpdateInstaller.RelaunchInfo(install))?.Dispose();
        }
    }

    /// <summary>Deletes what a previous update left behind. Call once on startup.</summary>
    public static void CleanUpPreviousUpdate()
    {
        if (Install is { } install)
        {
            UpdateInstaller.CleanUp(install);
        }
    }

    private static InstallLocation? DetectInstall()
    {
        // Single-file apps report an empty assembly location; that is exactly the published build we can replace.
#pragma warning disable IL3000
        var isSingleFile = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);
#pragma warning restore IL3000
        return InstallLocation.Detect(Environment.ProcessPath, isSingleFile);
    }
}
