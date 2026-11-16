using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Explorer;

/// <summary>A live connection to one namespace: entity discovery plus message operations.</summary>
public sealed class NamespaceConnection : IAsyncDisposable
{
    private readonly ServiceBusAdministrationClient _admin;

    public NamespaceConnection(ConnectionProfile profile)
    {
        Profile = profile;
        if (!ConnectionStringInfo.TryParse(profile.ConnectionString, out var info, out var error))
        {
            throw new FormatException(error);
        }

        Info = info!;
        Client = new ServiceBusClient(profile.ConnectionString);
        _admin = new ServiceBusAdministrationClient(profile.ConnectionString);
        Messages = new MessageOperations(this);
    }

    public ConnectionProfile Profile { get; }

    public ConnectionStringInfo Info { get; }

    internal ServiceBusClient Client { get; }

    public MessageOperations Messages { get; }

    /// <summary>Lists queues, topics and subscriptions with counts. Falls back to manual entities without Manage rights.</summary>
    public async Task<NamespaceSnapshot> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var queueProps = new Dictionary<string, QueueProperties>(StringComparer.OrdinalIgnoreCase);
            await foreach (var q in _admin.GetQueuesAsync(ct))
            {
                queueProps[q.Name] = q;
            }

            var queues = new List<EntityInfo>();
            await foreach (var r in _admin.GetQueuesRuntimePropertiesAsync(ct))
            {
                var requiresSession = queueProps.TryGetValue(r.Name, out var p) && p.RequiresSession;
                queues.Add(new EntityInfo(EntityKind.Queue, r.Name, RequiresSession: requiresSession, Counts: ToCounts(r)));
            }

            var topics = new List<TopicNode>();
            await foreach (var t in _admin.GetTopicsAsync(ct))
            {
                topics.Add(new TopicNode(new EntityInfo(EntityKind.Topic, t.Name), await LoadSubscriptionsAsync(t.Name, ct)));
            }

            return new NamespaceSnapshot(
                queues.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                topics.OrderBy(t => t.Topic.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                LimitedAccess: false,
                LimitedAccessReason: null);
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return BuildManualSnapshot(
                "This connection cannot list entities (Manage rights are required). Add queues or subscriptions by name.");
        }
    }

    /// <summary>Re-reads the counts for one entity. Returns the entity unchanged if counts cannot be read.</summary>
    public async Task<EntityInfo> RefreshAsync(EntityInfo entity, CancellationToken ct = default)
    {
        try
        {
            switch (entity.Kind)
            {
                case EntityKind.Queue:
                    var q = (await _admin.GetQueueRuntimePropertiesAsync(entity.Name, ct)).Value;
                    return entity with { Counts = ToCounts(q) };
                case EntityKind.Subscription:
                    var s = (await _admin.GetSubscriptionRuntimePropertiesAsync(entity.TopicName, entity.Name, ct)).Value;
                    return entity with { Counts = ToCounts(s) };
                default:
                    return entity;
            }
        }
        catch (Exception ex) when (IsAuthorizationFailure(ex))
        {
            return entity;
        }
    }

    private async Task<IReadOnlyList<EntityInfo>> LoadSubscriptionsAsync(string topic, CancellationToken ct)
    {
        var subProps = new Dictionary<string, SubscriptionProperties>(StringComparer.OrdinalIgnoreCase);
        await foreach (var s in _admin.GetSubscriptionsAsync(topic, ct))
        {
            subProps[s.SubscriptionName] = s;
        }

        var subs = new List<EntityInfo>();
        await foreach (var r in _admin.GetSubscriptionsRuntimePropertiesAsync(topic, ct))
        {
            var requiresSession = subProps.TryGetValue(r.SubscriptionName, out var p) && p.RequiresSession;
            subs.Add(new EntityInfo(EntityKind.Subscription, r.SubscriptionName, topic, requiresSession, ToCounts(r)));
        }

        return subs.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private NamespaceSnapshot BuildManualSnapshot(string reason)
    {
        var paths = Profile.ManualEntities.ToList();
        if (Info.EntityPath is { } entityPath && !paths.Contains(entityPath, StringComparer.OrdinalIgnoreCase))
        {
            paths.Insert(0, entityPath);
        }

        var entities = new List<EntityInfo>();
        foreach (var path in paths)
        {
            try
            {
                entities.Add(EntityInfo.ParseManual(path));
            }
            catch (FormatException)
            {
                // Skip malformed saved paths.
            }
        }

        var queues = entities.Where(e => e.Kind == EntityKind.Queue).ToList();
        var topics = entities
            .Where(e => e.Kind == EntityKind.Subscription)
            .GroupBy(e => e.TopicName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TopicNode(new EntityInfo(EntityKind.Topic, g.Key, IsManual: true), g.ToList()))
            .ToList();

        return new NamespaceSnapshot(queues, topics, LimitedAccess: true, LimitedAccessReason: reason);
    }

    internal static bool IsAuthorizationFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        RequestFailedException { Status: 401 or 403 } => true,
        ServiceBusException { Reason: ServiceBusFailureReason.GeneralError } sbe
            when sbe.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    private static EntityCounts ToCounts(QueueRuntimeProperties r) =>
        new(r.ActiveMessageCount, r.DeadLetterMessageCount, r.ScheduledMessageCount, r.TransferDeadLetterMessageCount);

    private static EntityCounts ToCounts(SubscriptionRuntimeProperties r) =>
        new(r.ActiveMessageCount, r.DeadLetterMessageCount, 0, r.TransferDeadLetterMessageCount);

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}
