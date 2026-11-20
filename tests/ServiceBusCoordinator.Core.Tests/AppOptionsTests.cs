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

    [Theory]
    [InlineData("orders_error", true)]
    [InlineData("Orders_ERROR", true)]
    [InlineData("orders", false)]
    [InlineData("orders_error_archive", false)]
    public void Error_queues_are_matched_by_suffix_by_default(string name, bool expected) =>
        Assert.Equal(expected, new AppOptions().IsErrorQueue(name));

    [Fact]
    public void Error_queue_suffixes_are_configurable_and_can_be_turned_off()
    {
        var options = new AppOptions { ErrorQueueSuffixes = " _error, -dlq ,," };
        Assert.True(options.IsErrorQueue("payments-dlq"));
        Assert.True(options.IsErrorQueue("payments_error"));

        options.HighlightErrorQueues = false;
        Assert.False(options.IsErrorQueue("payments_error"));

        Assert.False(new AppOptions { ErrorQueueSuffixes = "" }.IsErrorQueue("payments_error"));
    }

    [Fact]
    public void Error_queue_settings_round_trip()
    {
        var store = new AppOptionsStore(Path.Combine(_dir, "options.json"));
        store.Save(new AppOptions { HighlightErrorQueues = false, ErrorQueueSuffixes = "_error,_failed" });

        var loaded = store.Load();

        Assert.False(loaded.HighlightErrorQueues);
        Assert.Equal("_error,_failed", loaded.ErrorQueueSuffixes);
    }

    [Theory]
    [InlineData(1.0, true, 1.1)]
    [InlineData(1.0, false, 0.9)]
    [InlineData(2.0, true, 2.0)]
    [InlineData(0.5, false, 0.5)]
    [InlineData(1.05, true, 1.1)]
    [InlineData(1.05, false, 1.0)]
    [InlineData(0.67, false, 0.5)]
    public void Zoom_steps_like_a_browser_and_stops_at_the_ends(double from, bool zoomIn, double expected)
    {
        Assert.Equal(expected, AppOptions.StepZoom(from, zoomIn), 3);
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(-2, 1.0)]
    [InlineData(9, 2.0)]
    [InlineData(0.1, 0.5)]
    [InlineData(1.25, 1.25)]
    public void Zoom_from_the_file_is_kept_in_range(double stored, double expected)
    {
        Assert.Equal(expected, AppOptions.ClampZoom(stored), 3);
    }

    [Fact]
    public void Zoom_round_trips()
    {
        var store = new AppOptionsStore(Path.Combine(_dir, "options.json"));
        store.Save(new AppOptions { Zoom = 1.25 });

        Assert.Equal(1.25, store.Load().Zoom, 3);
    }
}
