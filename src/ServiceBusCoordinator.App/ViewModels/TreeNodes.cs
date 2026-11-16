using System.Collections.ObjectModel;
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

    [ObservableProperty]
    public partial string? Badge { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    public abstract NamespaceNodeViewModel Namespace { get; }

    public abstract string Glyph { get; }
}

public sealed partial class FolderNodeViewModel(NamespaceNodeViewModel ns, string title) : TreeNodeViewModel
{
    public override NamespaceNodeViewModel Namespace => ns;

    public override string Glyph => "▸";

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

    public override string Glyph => Entity.Kind switch
    {
        EntityKind.Queue => "Q",
        EntityKind.Topic => "T",
        _ => "S",
    };

    partial void OnEntityChanged(EntityInfo value)
    {
        Title = value.Name + (value.RequiresSession ? " (sessions)" : "");
        Badge = value.Counts is { } c
            ? c.DeadLetter > 0 ? $"{c.Active:N0} · DLQ {c.DeadLetter:N0}" : $"{c.Active:N0}"
            : null;
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
            Badge = info!.FullyQualifiedNamespace;
        }
    }

    public ConnectionProfile Profile { get; }

    public NamespaceConnection? Connection { get; private set; }

    public NamespaceSnapshot? Snapshot { get; private set; }

    public override NamespaceNodeViewModel Namespace => this;

    public override string Glyph => "◆";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? LimitedAccessReason { get; set; }

    /// <summary>All queues and topics, for picking a resubmit target.</summary>
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

            topicNode.Badge = $"{t.Subscriptions.Count} subs";
            topics.Children.Add(topicNode);
        }

        queues.Init();
        topics.Init();
        Children.Add(queues);
        Children.Add(topics);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
            Connection = null;
        }
    }
}
