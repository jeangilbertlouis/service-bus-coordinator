using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Models;

/// <summary>Which sub-queue of an entity to work with.</summary>
public enum MessageSource
{
    Active,
    DeadLetter,
    TransferDeadLetter,
}

public static class MessageSourceExtensions
{
    public static SubQueue ToSubQueue(this MessageSource source) => source switch
    {
        MessageSource.DeadLetter => SubQueue.DeadLetter,
        MessageSource.TransferDeadLetter => SubQueue.TransferDeadLetter,
        _ => SubQueue.None,
    };

    /// <summary>Path suffix as Service Bus names it, e.g. <c>/$deadletterqueue</c>.</summary>
    public static string PathSuffix(this MessageSource source) => source switch
    {
        MessageSource.DeadLetter => "/$deadletterqueue",
        MessageSource.TransferDeadLetter => "/$transfer/$deadletterqueue",
        _ => "",
    };

    public static string DisplayName(this MessageSource source) => source switch
    {
        MessageSource.DeadLetter => "dead-letter queue",
        MessageSource.TransferDeadLetter => "transfer dead-letter queue",
        _ => "active messages",
    };

    public static bool IsDeadLetter(this MessageSource source) => source != MessageSource.Active;
}

/// <summary>A message session found on a session-enabled entity.</summary>
public sealed record SessionSummary(string SessionId, int StateBytes);
