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

public enum DropPlacement
{
    Before,
    After,
    Into,
}

/// <summary>Where a dragged connection or folder lands: before or after a node, or into a folder.</summary>
public sealed record DropTarget(TreeNodeViewModel Node, DropPlacement Placement);

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

    /// <summary>Messages waiting in error queues below this node (e.g. the Queues folder); null when there are none to count.</summary>
    [ObservableProperty]
    public partial string? ErrorBadge { get; set; }

    [ObservableProperty]
    public partial bool IsErrorBadgeAlert { get; set; }

    [ObservableProperty]
    public partial string? ErrorBadgeTip { get; set; }

    /// <summary>Shows <see cref="Badge"/> in red, e.g. for messages waiting in an error queue.</summary>
    [ObservableProperty]
    public partial bool IsBadgeAlert { get; set; }

    [ObservableProperty]
    public partial string? BadgeTip { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged(value);

    protected virtual void OnExpandedChanged(bool expanded)
    {
    }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>The connection this node belongs to; null for a sidebar folder of connections.</summary>
    public abstract NamespaceNodeViewModel? Namespace { get; }

    public abstract Geometry Icon { get; }

    public virtual bool IsQueue => false;

    public virtual bool IsTopic => false;

    public virtual bool IsSubscription => false;

    public virtual bool IsFolder => false;
}

/// <summary>A sidebar folder grouping connections. Holds only <see cref="NamespaceNodeViewModel"/> children.</summary>
public sealed partial class ConnectionFolderNodeViewModel : TreeNodeViewModel
{
    public ConnectionFolderNodeViewModel(ConnectionFolder folder)
    {
        Folder = folder;
        Title = folder.Name;
        IsExpanded = folder.IsExpanded;
        Children.CollectionChanged += (_, _) => UpdateBadge();
        UpdateBadge();
    }

    public ConnectionFolder Folder { get; }

    public override NamespaceNodeViewModel? Namespace => null;

    public override Geometry Icon => Icons.Folder;

    public override bool IsFolder => true;

    public IEnumerable<NamespaceNodeViewModel> Connections => Children.OfType<NamespaceNodeViewModel>();

    /// <summary>Raised when the user expands or collapses the folder, so the choice can be saved.</summary>
    public event Action? ExpansionChanged;

    protected override void OnExpandedChanged(bool expanded)
    {
        Folder.IsExpanded = expanded;
        ExpansionChanged?.Invoke();
    }

    public void Rename(string name)
    {
        Folder.Name = name;
        Title = name;
    }

    private void UpdateBadge() => Badge = Children.Count.ToString("N0");
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

    /// <summary>Active messages in the error queues under this folder (queues matching the Options suffixes).</summary>
    public long TotalErrorMessages { get; private set; }

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
        long active = 0, deadLetters = 0, errors = 0;
        var errorQueues = 0;
        foreach (var node in Entities(Children))
        {
            if (node.Entity is { HasMessages: true, Counts: { } c })
            {
                active += c.Active;
                deadLetters += c.DeadLetter;
            }

            if (node.IsQueue && ns.Options.IsErrorQueue(node.Entity.Name))
            {
                errorQueues++;
                errors += node.Entity.Counts?.Active ?? 0;
            }
        }

        TotalErrorMessages = errors;
        ErrorBadge = errorQueues > 0 ? $"{ErrorLabel(ns.Options.ErrorQueueSuffixes)} {errors:N0}" : null;
        IsErrorBadgeAlert = errors > 0;
        ErrorBadgeTip = errorQueues == 1 ? "Messages in 1 error queue" : $"Messages across {errorQueues:N0} error queues";

        TotalActive = active;
        TotalDeadLetters = deadLetters;
        Badge = active.ToString("N0");
        DeadLetterBadge = $"DLQ {deadLetters:N0}";
        HasDeadLetters = deadLetters > 0;
    }

    /// <summary>"_error" for a single suffix, otherwise "errors".</summary>
    private static string ErrorLabel(string? suffixes)
    {
        var list = (suffixes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 1 ? list[0] : "errors";
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

    /// <summary>
    /// Red count for a queue whose name marks it as an error queue (e.g. <c>orders_error</c>) while it holds messages,
    /// as set in Options.
    /// </summary>
    public void UpdateErrorHighlight()
    {
        var isErrorQueue = Entity.Kind == EntityKind.Queue && _ns.Options.IsErrorQueue(Entity.Name);
        IsBadgeAlert = isErrorQueue && Entity.Counts is { Active: > 0 };
        BadgeTip = isErrorQueue ? "Messages in this error queue" : "Active messages";
    }

    /// <summary>The Queues or Topics folder above this entity, whose totals include it.</summary>
    public FolderNodeViewModel? Folder { get; init; }

    /// <summary>For a subscription, the topic row above it, whose totals include it.</summary>
    public EntityNodeViewModel? ParentTopic { get; init; }

    /// <summary>
    /// A topic holds no messages itself, so its row shows the active and dead-letter totals of its subscriptions,
    /// like a queue's row.
    /// </summary>
    public void UpdateTopicTotals()
    {
        if (!IsTopic)
        {
            return;
        }

        long active = 0, deadLetters = 0;
        var subscriptions = 0;
        foreach (var sub in Children.OfType<EntityNodeViewModel>())
        {
            subscriptions++;
            if (sub.Entity.Counts is { } c)
            {
                active += c.Active;
                deadLetters += c.DeadLetter;
            }
        }

        Badge = active.ToString("N0");
        BadgeTip = $"Active messages across {(subscriptions == 1 ? "1 subscription" : $"{subscriptions:N0} subscriptions")}";
        DeadLetterBadge = $"DLQ {deadLetters:N0}";
        HasDeadLetters = deadLetters > 0;
    }

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

        UpdateErrorHighlight();
        UpdateTopicTotals();
        ParentTopic?.UpdateTopicTotals();

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

    /// <summary>Active messages across all queues and subscriptions.</summary>
    [ObservableProperty]
    public partial long TotalActive { get; set; }

    /// <summary>Active messages across the error queues (names ending in <c>_error</c> by default, see Options).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessages))]
    public partial long TotalErrorMessages { get; set; }

    public bool HasErrorMessages => TotalErrorMessages > 0;

    /// <summary>"in 3 error queues", or why there are none.</summary>
    [ObservableProperty]
    public partial string? ErrorQueueSummary { get; set; }

    /// <summary>Whether the overview shows the error-queue total (error-queue highlighting is on in Options).</summary>
    [ObservableProperty]
    public partial bool ShowErrorQueues { get; set; }

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
                topicNode.Children.Add(new EntityNodeViewModel(this, s) { Folder = topics, ParentTopic = topicNode });
            }

            topicNode.UpdateTopicTotals();
            topics.Children.Add(topicNode);
        }

        queues.Init();
        topics.Init();
        Children.Add(queues);
        Children.Add(topics);

        UpdateSummary();
        UpdateTotals();
    }

    private void UpdateSummary()
    {
        var folders = Children.OfType<FolderNodeViewModel>().ToList();
        var queues = folders.ElementAtOrDefault(0)?.Children.Count ?? 0;
        var topics = folders.ElementAtOrDefault(1)?.Children ?? [];
        var subscriptionCount = topics.Sum(t => t.Children.Count);
        Summary = $"{Plural(queues, "queue")} · {Plural(topics.Count, "topic")} · {Plural(subscriptionCount, "subscription")}";
    }

    /// <summary>Takes a deleted queue or subscription out of the tree and recomputes the totals that included it.</summary>
    public void RemoveEntity(EntityNodeViewModel node)
    {
        if (node.ParentTopic is { } topic)
        {
            topic.Children.Remove(node);
            topic.UpdateTopicTotals();
        }
        else
        {
            node.Folder?.Children.Remove(node);
        }

        // A manually added entry for it would otherwise show it again; the caller saves the profile.
        Profile.ManualEntities.RemoveAll(p => EntityPathMatches(p, node.Entity));

        node.Folder?.Recount();
        UpdateSummary();
        UpdateTotals();
    }

    private static bool EntityPathMatches(string manualPath, EntityInfo entity)
    {
        try
        {
            return string.Equals(EntityInfo.ParseManual(manualPath).Path, entity.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Recomputes the connection's dead-letter total from its folders.</summary>
    internal void UpdateTotals()
    {
        var folders = Children.OfType<FolderNodeViewModel>().ToList();
        TotalDeadLetters = folders.Sum(f => f.TotalDeadLetters);
        TotalActive = folders.Sum(f => f.TotalActive);
        AlertBadge = TotalDeadLetters > 0 ? $"DLQ {TotalDeadLetters:N0}" : null;

        var errorQueues = folders.SelectMany(f => f.Children).OfType<EntityNodeViewModel>()
            .Where(n => n.IsQueue && Options.IsErrorQueue(n.Entity.Name))
            .ToList();
        TotalErrorMessages = errorQueues.Sum(n => n.Entity.Counts?.Active ?? 0);
        ShowErrorQueues = Options.HighlightErrorQueues;
        ErrorQueueSummary = errorQueues.Count switch
        {
            0 => $"No queues ending in {Options.ErrorQueueSuffixes}",
            1 => "in 1 error queue",
            var n => $"in {n:N0} error queues",
        };
    }

    /// <summary>Re-applies display options (such as error-queue highlighting) to the loaded entities.</summary>
    public void ApplyOptions()
    {
        foreach (var node in Children.SelectMany(c => c.Children.Concat(c.Children.SelectMany(t => t.Children))).OfType<EntityNodeViewModel>())
        {
            node.UpdateErrorHighlight();
        }

        foreach (var folder in Children.OfType<FolderNodeViewModel>())
        {
            folder.Recount();
        }

        UpdateTotals();
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
