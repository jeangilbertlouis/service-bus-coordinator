using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Explorer;
using ServiceBusCoordinator.Core.Models;

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

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public abstract NamespaceNodeViewModel Namespace { get; }

    public abstract Geometry Icon { get; }

    public virtual bool IsQueue => false;

    public virtual bool IsTopic => false;

    public virtual bool IsSubscription => false;

    public virtual bool IsFolder => false;
}

public sealed partial class FolderNodeViewModel(NamespaceNodeViewModel ns, string title) : TreeNodeViewModel
{
    public override NamespaceNodeViewModel Namespace => ns;

    public override Geometry Icon => Icons.Folder;

    public override bool IsFolder => true;

    public void Init()
    {
        Title = title;
        Badge = Children.Count.ToString();
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
            AlertBadge = c.DeadLetter > 0 ? $"DLQ {c.DeadLetter:N0}" : null;
        }
    }
}

public sealed partial class NamespaceNodeViewModel : TreeNodeViewModel, IAsyncDisposable
{
    public NamespaceNodeViewModel(ConnectionProfile profile)
    {
        Profile = profile;
        Title = profile.Name;
        IsExpanded = true;
        if (ConnectionStringInfo.TryParse(profile.ConnectionString, out var info, out _))
        {
            Host = info!.FullyQualifiedNamespace;
            IsEmulator = info.IsEmulator;
        }
    }

    public ConnectionProfile Profile { get; }

    public string? Host { get; }

    public bool IsEmulator { get; }

    public NamespaceConnection? Connection { get; private set; }

    public NamespaceSnapshot? Snapshot { get; private set; }

    public override NamespaceNodeViewModel Namespace => this;

    public override Geometry Icon => Icons.Namespace;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

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

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            Connection ??= new NamespaceConnection(Profile);
            var snapshot = await Connection.LoadAsync();
            Snapshot = snapshot;
            LimitedAccessReason = snapshot.LimitedAccessReason;
            Rebuild(snapshot);
        }
        catch (Exception ex)
        {
            Error = SecretScrubber.Scrub(ex.Message);
            Summary = null;
            Children.Clear();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild(NamespaceSnapshot snapshot)
    {
        Children.Clear();

        var queues = new FolderNodeViewModel(this, "Queues") { IsExpanded = true };
        foreach (var q in snapshot.Queues)
        {
            queues.Children.Add(new EntityNodeViewModel(this, q));
        }

        var topics = new FolderNodeViewModel(this, "Topics") { IsExpanded = true };
        foreach (var t in snapshot.Topics)
        {
            var topicNode = new EntityNodeViewModel(this, t.Topic) { IsExpanded = true };
            foreach (var s in t.Subscriptions)
            {
                topicNode.Children.Add(new EntityNodeViewModel(this, s));
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
        TotalDeadLetters = snapshot.Queues.Sum(q => q.Counts?.DeadLetter ?? 0)
            + snapshot.Topics.SelectMany(t => t.Subscriptions).Sum(s => s.Counts?.DeadLetter ?? 0);
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
