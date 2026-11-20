using System.Text.Json;

namespace ServiceBusCoordinator.Core.Settings;

/// <summary>User preferences from the Options dialog. Nothing here is secret, so it is kept in a plain JSON file.</summary>
public sealed class AppOptions
{
    public const int DefaultPurgeConfirmThreshold = 100;

    public const string DefaultErrorQueueSuffixes = "_error";

    /// <summary>Expand and connect every saved connection when the app starts. Off by default, so many connections cost nothing.</summary>
    public bool ConnectOnStartup { get; set; }

    /// <summary>Whether a connection's Queues folder starts expanded.</summary>
    public bool ExpandQueues { get; set; } = true;

    /// <summary>Whether a connection's Topics folder starts expanded.</summary>
    public bool ExpandTopicsFolder { get; set; }

    /// <summary>Whether each topic starts expanded to show its subscriptions.</summary>
    public bool ExpandTopics { get; set; }

    /// <summary>Purges of more messages than this (or of an unknown number) ask for the entity name to be entered.</summary>
    public int PurgeConfirmThreshold { get; set; } = DefaultPurgeConfirmThreshold;

    /// <summary>Show the message count of error queues (see <see cref="ErrorQueueSuffixes"/>) in red when they hold messages.</summary>
    public bool HighlightErrorQueues { get; set; } = true;

    /// <summary>Comma-separated name endings that mark a queue as an error queue, matched case-insensitively.</summary>
    public string ErrorQueueSuffixes { get; set; } = DefaultErrorQueueSuffixes;

    /// <summary>Whether a queue with this name is an error queue whose messages should stand out.</summary>
    public bool IsErrorQueue(string queueName) =>
        HighlightErrorQueues
        && (ErrorQueueSuffixes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(suffix => queueName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Look for a newer release when the app starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public AppOptions Clone() => (AppOptions)MemberwiseClone();

    public void CopyFrom(AppOptions other)
    {
        ConnectOnStartup = other.ConnectOnStartup;
        ExpandQueues = other.ExpandQueues;
        ExpandTopicsFolder = other.ExpandTopicsFolder;
        ExpandTopics = other.ExpandTopics;
        PurgeConfirmThreshold = Math.Max(0, other.PurgeConfirmThreshold);
        CheckForUpdatesOnStartup = other.CheckForUpdatesOnStartup;
        HighlightErrorQueues = other.HighlightErrorQueues;
        ErrorQueueSuffixes = other.ErrorQueueSuffixes?.Trim() ?? "";
    }
}

/// <summary>Reads and writes <see cref="AppOptions"/> as JSON. A missing or unreadable file gives the defaults.</summary>
public sealed class AppOptionsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>The per-user options file, e.g. <c>~/.config/ServiceBusCoordinator/options.json</c> or <c>%APPDATA%\ServiceBusCoordinator\options.json</c>.</summary>
    public static AppOptionsStore CreateDefault() => new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "ServiceBusCoordinator", "options.json"));

    public AppOptions Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new AppOptions();
            }

            var loaded = JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(Path), JsonOptions) ?? new AppOptions();
            var options = new AppOptions();
            options.CopyFrom(loaded); // Clamps out-of-range values.
            return options;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppOptions();
        }
    }

    public void Save(AppOptions options)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(options, JsonOptions));
        File.Move(temp, Path, overwrite: true);
    }
}
