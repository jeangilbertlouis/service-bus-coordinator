namespace ServiceBusCoordinator.Core.Models;

public enum EntityKind
{
    Queue,
    Topic,
    Subscription,
}

public sealed record EntityCounts(long Active, long DeadLetter, long Scheduled, long TransferDeadLetter);

/// <summary>A queue, topic or subscription in a namespace.</summary>
public sealed record EntityInfo(
    EntityKind Kind,
    string Name,
    string? TopicName = null,
    bool RequiresSession = false,
    EntityCounts? Counts = null,
    bool IsManual = false)
{
    /// <summary>Entity path as used by the SDK and management APIs.</summary>
    public string Path => Kind == EntityKind.Subscription ? $"{TopicName}/subscriptions/{Name}" : Name;

    /// <summary>Whether this entity holds messages (topics do not; their subscriptions do).</summary>
    public bool HasMessages => Kind != EntityKind.Topic;

    /// <summary>Where dead-lettered messages go back to by default: the queue itself, or the subscription's topic.</summary>
    public string? DefaultResubmitTarget => Kind switch
    {
        EntityKind.Queue => Name,
        EntityKind.Subscription => TopicName,
        _ => null,
    };

    /// <summary>
    /// Parses a manually entered path: <c>queue</c>, <c>topic/subscriptions/sub</c> or the shorthand <c>topic/sub</c>.
    /// </summary>
    public static EntityInfo ParseManual(string path)
    {
        var parts = (path ?? "").Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts switch
        {
            [var queue] => new EntityInfo(EntityKind.Queue, queue, IsManual: true),
            [var topic, var sub] => new EntityInfo(EntityKind.Subscription, sub, topic, IsManual: true),
            [var topic, var middle, var sub] when middle.Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
                => new EntityInfo(EntityKind.Subscription, sub, topic, IsManual: true),
            _ => throw new FormatException("Use 'queue', 'topic/subscriptions/subscription' or 'topic/subscription'."),
        };
    }
}

public sealed record TopicNode(EntityInfo Topic, IReadOnlyList<EntityInfo> Subscriptions);

/// <summary>Everything discovered in a namespace.</summary>
public sealed record NamespaceSnapshot(
    IReadOnlyList<EntityInfo> Queues,
    IReadOnlyList<TopicNode> Topics,
    bool LimitedAccess,
    string? LimitedAccessReason);
