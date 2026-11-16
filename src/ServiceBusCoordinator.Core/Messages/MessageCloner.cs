using System.Globalization;
using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Messages;

/// <summary>Builds the outgoing copy of a dead-lettered message for resubmission.</summary>
public static class MessageCloner
{
    public const string DeadLetterReasonKey = "DeadLetterReason";
    public const string DeadLetterErrorDescriptionKey = "DeadLetterErrorDescription";
    public const string ResubmittedAtKey = "x-resubmitted-at";
    public const string ResubmittedFromKey = "x-resubmitted-from";
    public const string ResubmitCountKey = "x-resubmit-count";
    public const string OriginalSequenceNumberKey = "x-original-sequence-number";

    public static ServiceBusMessage Clone(
        ServiceBusReceivedMessage source,
        bool stripDeadLetterProperties,
        bool stampResubmitHeaders,
        string sourcePath,
        DateTimeOffset now)
    {
        var message = new ServiceBusMessage(source)
        {
            // Never carry a schedule over; the message should be available immediately.
            ScheduledEnqueueTime = default,
        };

        var props = message.ApplicationProperties;
        if (stripDeadLetterProperties)
        {
            props.Remove(DeadLetterReasonKey);
            props.Remove(DeadLetterErrorDescriptionKey);
        }
        else
        {
            if (source.DeadLetterReason is { } reason)
            {
                props[DeadLetterReasonKey] = reason;
            }

            if (source.DeadLetterErrorDescription is { } description)
            {
                props[DeadLetterErrorDescriptionKey] = description;
            }
        }

        if (stampResubmitHeaders)
        {
            var previous = props.TryGetValue(ResubmitCountKey, out var raw) && raw is not null
                && int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var n) ? n : 0;

            props[ResubmittedAtKey] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            props[ResubmittedFromKey] = sourcePath + "/$deadletterqueue";
            props[ResubmitCountKey] = previous + 1;
            props[OriginalSequenceNumberKey] = source.SequenceNumber;
        }

        return message;
    }
}
