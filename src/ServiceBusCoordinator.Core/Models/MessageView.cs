using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Models;

/// <summary>Immutable snapshot of a peeked message for display.</summary>
public sealed record MessageView(
    long SequenceNumber,
    string? MessageId,
    string? Subject,
    string? ContentType,
    string? CorrelationId,
    string? SessionId,
    string? PartitionKey,
    string? ReplyTo,
    string? To,
    DateTimeOffset EnqueuedTime,
    DateTimeOffset ExpiresAt,
    TimeSpan TimeToLive,
    DateTimeOffset ScheduledEnqueueTime,
    int DeliveryCount,
    ServiceBusMessageState State,
    string? DeadLetterReason,
    string? DeadLetterErrorDescription,
    string? DeadLetterSource,
    IReadOnlyDictionary<string, object> ApplicationProperties,
    byte[] Body)
{
    public static MessageView From(ServiceBusReceivedMessage m) => new(
        m.SequenceNumber,
        m.MessageId,
        m.Subject,
        m.ContentType,
        m.CorrelationId,
        m.SessionId,
        m.PartitionKey,
        m.ReplyTo,
        m.To,
        m.EnqueuedTime,
        m.ExpiresAt,
        m.TimeToLive,
        m.ScheduledEnqueueTime,
        m.DeliveryCount,
        m.State,
        m.DeadLetterReason,
        m.DeadLetterErrorDescription,
        m.DeadLetterSource,
        new Dictionary<string, object>(m.ApplicationProperties),
        m.Body.ToArray());
}
