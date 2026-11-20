using ServiceBusCoordinator.Core.Settings;

namespace ServiceBusCoordinator.Core.Tests;

public sealed class AppOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sbc-options-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var options = new AppOptionsStore(Path.Combine(_dir, "options.json")).Load();

        Assert.False(options.ConnectOnStartup);
        Assert.True(options.ExpandQueues);
        Assert.False(options.ExpandTopicsFolder);
        Assert.False(options.ExpandTopics);
        Assert.Equal(AppOptions.DefaultPurgeConfirmThreshold, options.PurgeConfirmThreshold);
        Assert.True(options.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void Saved_options_round_trip()
    {
        var store = new AppOptionsStore(Path.Combine(_dir, "nested", "options.json"));
        store.Save(new AppOptions
        {
            ConnectOnStartup = true, ExpandQueues = false, ExpandTopicsFolder = true, ExpandTopics = true,
            PurgeConfirmThreshold = 5000, CheckForUpdatesOnStartup = false,
        });

        var loaded = store.Load();

        Assert.True(loaded.ConnectOnStartup);
        Assert.False(loaded.ExpandQueues);
        Assert.True(loaded.ExpandTopicsFolder);
        Assert.True(loaded.ExpandTopics);
        Assert.Equal(5000, loaded.PurgeConfirmThreshold);
        Assert.False(loaded.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "options.json");
        File.WriteAllText(path, "{ not json");

        Assert.True(new AppOptionsStore(path).Load().ExpandQueues);
    }

    [Fact]
    public void Unknown_and_missing_properties_keep_defaults_and_negative_threshold_is_clamped()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "options.json");
        File.WriteAllText(path, """{ "ExpandTopics": true, "PurgeConfirmThreshold": -3, "SomethingNew": 1 }""");

        var loaded = new AppOptionsStore(path).Load();

        Assert.True(loaded.ExpandTopics);
        Assert.True(loaded.ExpandQueues);
        Assert.Equal(0, loaded.PurgeConfirmThreshold);
    }
}
