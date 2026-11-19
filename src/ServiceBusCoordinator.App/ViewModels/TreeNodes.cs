using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Explorer;
using ServiceBusCoordinator.Core.Models;
using ServiceBusCoordinator.Core.Settings;

namespace ServiceBusCoordinator.App.ViewModels;

public abstract partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>Neutral badge, e.g. the active message count.</summary>
    [ObservableProperty]
    public partial string? Badge { get; set; }

    /// <summary>Alert badge, e.g. the dead-letter count when it is above zero.</summary>
    [ObservableProperty]
    public partial string? AlertBadge { get; set; }

    /// <summary>Dead-letter count for entities that hold messages; shown even when zero.</summary>
    [ObservableProperty]
    public partial string? DeadLetterBadge { get; set; }

    [ObservableProperty]
    public partial bool HasDeadLetters { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged(value);

    protected virtual void OnExpandedChanged(bool expanded)
    {
    }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public abstract NamespaceNodeViewModel Namespace { get; }

    public abstract Geometry Icon { get; }

    public virtual bool IsQueue => false;

    public virtual bool IsTopic => false;

    public virtual bool IsSubscription => false;

    public virtual bool IsFolder => false;
}

/// <summary>
/// Stand-in child of a connection that has not loaded yet. It gives the tree an expand arrow, and says
/// "Connecting…" or why connecting failed.
/// </summary>
public sealed partial class PlaceholderNodeViewModel(NamespaceNodeViewModel ns, string text) : TreeNodeViewModel
{
    public override NamespaceNodeViewModel Namespace => ns;

    public override Geometry Icon => Icons.Namespace;

    public string Text => text;
}

public sealed partial class FolderNodeViewModel(NamespaceNodeViewModel ns, string title) : TreeNodeViewModel
{
    public override NamespaceNodeViewModel Namespace => ns;

    public override Geometry Icon => Icons.Folder;

    public override bool IsFolder => true;

    public long TotalActive { get; private set; }

    public long TotalDeadLetters { get; private set; }

    public void Init()
    {
        Title = title;
        Recount();
    }

    /// <summary>
    /// Sums the active and dead-letter messages of every queue or subscription under this folder, so the badges show
    /// how much is waiting rather than how many entities there are.
    /// </summary>
    public void Recount()
    {
        long active = 0, deadLetters = 0;
        foreach (var node in Entities(Children))
        {
            if (node.Entity is { HasMessages: true, Counts: { } c })
            {
                active += c.Active;
                deadLetters += c.DeadLetter;
            }
        }

        TotalActive = active;
        TotalDeadLetters = deadLetters;
        Badge = active.ToString("N0");
        DeadLetterBadge = $"DLQ {deadLetters:N0}";
        HasDeadLetters = deadLetters > 0;
    }

    private static IEnumerable<EntityNodeViewModel> Entities(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is EntityNodeViewModel entity)
            {
                yield return entity;
            }

            foreach (var child in Entities(node.Children))
            {
                yield return child;
            }
        }
    }
}

public sealed partial class EntityNodeViewModel : TreeNodeViewModel
{
    private readonly NamespaceNodeViewModel _ns;

    public EntityNodeViewModel(NamespaceNodeViewModel ns, EntityInfo entity)
    {
        _ns = ns;
        Entity = entity;
    }

    [ObservableProperty]
    public partial EntityInfo Entity { get; set; }

    public override NamespaceNodeViewModel Namespace => _ns;

    /// <summary>The Queues or Topics folder above this entity, whose totals include it.</summary>
    public FolderNodeViewModel? Folder { get; init; }

    public override Geometry Icon => Entity.Kind switch
    {
        EntityKind.Queue => Icons.Queue,
        EntityKind.Topic => Icons.Topic,
        _ => Icons.Subscription,
    };

    public override bool IsQueue => Entity.Kind == EntityKind.Queue;

    public override bool IsTopic => Entity.Kind == EntityKind.Topic;

    public override bool IsSubscription => Entity.Kind == EntityKind.Subscription;

    partial void OnEntityChanged(EntityInfo value)
    {
        Title = value.Name;
        if (value.Counts is { } c)
        {
            Badge = c.Active.ToString("N0");
            DeadLetterBadge = value.HasMessages ? $"DLQ {c.DeadLetter:N0}" : null;
            HasDeadLetters = c.DeadLetter > 0;
        }
        else
        {
            DeadLetterBadge = null;
            HasDeadLetters = false;
        }

        // After a refresh or purge, keep the folder and connection totals in step.
        if (Folder is not null)
        {
            Folder.Recount();
            _ns.UpdateTotals();
        }
    }
}

public sealed partial class NamespaceNodeViewModel : TreeNodeViewModel, IAsyncDisposable
{
    public NamespaceNodeViewModel(ConnectionProfile profile, AppOptions? options = null)
    {
        Profile = profile;
        Options = options ?? new AppOptions();
        Title = profile.Name;
        Children.Add(new PlaceholderNodeViewModel(this, "Connecting…"));
        if (ConnectionStringInfo.TryParse(profile.ConnectionString, out var info, out _))
        {
            Host = info!.FullyQualifiedNamespace;
            IsEmulator = info.IsEmulator;
        }
    }

    public ConnectionProfile Profile { get; }

    /// <summary>The user's options (shared with the main window), e.g. which folders start expanded.</summary>
    public AppOptions Options { get; }

    public string? Host { get; }

    public bool IsEmulator { get; }

    public NamespaceConnection? Connection { get; private set; }

    public NamespaceSnapshot? Snapshot { get; private set; }

    public override NamespaceNodeViewModel Namespace => this;

    public override Geometry Icon => Icons.Namespace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsLoading { get; set; }

    /// <summary>Whether entities have been loaded at least once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsConnected { get; set; }

    /// <summary>Not connected yet and not connecting: the overview offers a Connect button.</summary>
    public bool CanConnect => !IsConnected && !IsLoading;

    private Task? _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectPrompt), nameof(ConnectLabel))]
    public partial string? Error { get; set; }

    public string ConnectPrompt => Error is null
        ? "Not connected. Expand it in the tree, or connect now."
        : "Could not connect.";

    public string ConnectLabel => Error is null ? "Connect" : "Try again";

    [ObservableProperty]
    public partial string? LimitedAccessReason { get; set; }

    /// <summary>"3 queues · 2 topics · 5 subscriptions" once loaded.</summary>
    [ObservableProperty]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    public partial long TotalDeadLetters { get; set; }

    /// <summary>All queues and topics, for picking a resubmit or send target.</summary>
    public IReadOnlyList<EntityInfo> SendableEntities =>
        Snapshot is null ? [] : [.. Snapshot.Queues, .. Snapshot.Topics.Select(t => t.Topic)];

    /// <summary>Connections connect the first time they are expanded, not when the app starts.</summary>
    protected override void OnExpandedChanged(bool expanded)
    {
        // Also retries after a failure: collapse and expand again.
        if (expanded && !IsConnected)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>Expands the connection in the tree, which connects it.</summary>
    [RelayCommand]
    private void Connect()
    {
        if (IsExpanded && !IsConnected)
        {
            _ = LoadAsync();
        }

        IsExpanded = true;
    }

    /// <summary>Connects (if needed) and loads the entities. A load already in progress is shared, not repeated.</summary>
    public Task LoadAsync() => _loading is { IsCompleted: false } ? _loading : _loading = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        Error = null;
        if (!IsConnected)
        {
            Children.Clear();
            Children.Add(new PlaceholderNodeViewModel(this, "Connecting…"));
        }

        try
        {
            Connection ??= new NamespaceConnection(Profile);
            var snapshot = await Connection.LoadAsync();
            Snapshot = snapshot;
            LimitedAccessReason = snapshot.LimitedAccessReason;
            Rebuild(snapshot);
            IsConnected = true;
        }
        catch (Exception ex)
        {
            Error = SecretScrubber.Scrub(ex.Message);
            Summary = null;
            Snapshot = null;
            IsConnected = false;
            Children.Clear();
            Children.Add(new PlaceholderNodeViewModel(this, "Could not connect"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild(NamespaceSnapshot snapshot)
    {
        // The first load follows the options (by default Queues expanded, Topics and each topic collapsed).
        // After a refresh, keep whatever the user expanded or collapsed.
        var previous = Children.OfType<FolderNodeViewModel>().ToList();
        var firstLoad = previous.Count == 0;
        var queuesExpanded = previous.ElementAtOrDefault(0)?.IsExpanded ?? Options.ExpandQueues;
        var topicsExpanded = previous.ElementAtOrDefault(1)?.IsExpanded ?? Options.ExpandTopicsFolder;
        var openTopics = previous.ElementAtOrDefault(1)?.Children.Where(c => c.IsExpanded).Select(c => c.Title).ToHashSet() ?? [];
        Children.Clear();

        var queues = new FolderNodeViewModel(this, "Queues") { IsExpanded = queuesExpanded };
        foreach (var q in snapshot.Queues)
        {
            queues.Children.Add(new EntityNodeViewModel(this, q) { Folder = queues });
        }

        var topics = new FolderNodeViewModel(this, "Topics") { IsExpanded = topicsExpanded };
        foreach (var t in snapshot.Topics)
        {
            var topicNode = new EntityNodeViewModel(this, t.Topic) { Folder = topics, IsExpanded = firstLoad ? Options.ExpandTopics : openTopics.Contains(t.Topic.Name) };
            foreach (var s in t.Subscriptions)
            {
                topicNode.Children.Add(new EntityNodeViewModel(this, s) { Folder = topics });
            }

            topicNode.Badge = t.Subscriptions.Count == 1 ? "1 sub" : $"{t.Subscriptions.Count} subs";
            topics.Children.Add(topicNode);
        }

        queues.Init();
        topics.Init();
        Children.Add(queues);
        Children.Add(topics);

        var subscriptionCount = snapshot.Topics.Sum(t => t.Subscriptions.Count);
        Summary = $"{Plural(snapshot.Queues.Count, "queue")} · {Plural(snapshot.Topics.Count, "topic")} · {Plural(subscriptionCount, "subscription")}";
        UpdateTotals();
    }

    /// <summary>Recomputes the connection's dead-letter total from its folders.</summary>
    internal void UpdateTotals()
    {
        TotalDeadLetters = Children.OfType<FolderNodeViewModel>().Sum(f => f.TotalDeadLetters);
        AlertBadge = TotalDeadLetters > 0 ? $"DLQ {TotalDeadLetters:N0}" : null;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }
    }
}
