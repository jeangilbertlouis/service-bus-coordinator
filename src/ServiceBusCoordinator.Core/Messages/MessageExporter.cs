using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Messages;

/// <summary>Writes messages to a JSON file: one object per message, body as UTF-8 text or base64.</summary>
public static class MessageExporter
{
    public const int FormatVersion = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task ExportAsync(Stream destination, string sourcePath, IEnumerable<MessageView> messages, DateTimeOffset exportedAt, CancellationToken ct = default)
    {
        var document = new ExportDocument(
            FormatVersion,
            sourcePath,
            exportedAt,
            messages.Select(ToExported).ToList());
        await JsonSerializer.SerializeAsync(destination, document, Options, ct);
    }

    public static string ToJson(string sourcePath, IEnumerable<MessageView> messages, DateTimeOffset exportedAt)
    {
        using var stream = new MemoryStream();
        ExportAsync(stream, sourcePath, messages, exportedAt).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ExportedMessage ToExported(MessageView m)
    {
        string body;
        string encoding;
        try
        {
            body = StrictUtf8.GetString(m.Body);
            encoding = "utf-8";
        }
        catch (DecoderFallbackException)
        {
            body = Convert.ToBase64String(m.Body);
            encoding = "base64";
        }

        return new ExportedMessage(
            m.SequenceNumber,
            m.MessageId,
            m.Subject,
            m.ContentType,
            m.CorrelationId,
            m.SessionId,
            m.PartitionKey,
            m.To,
            m.ReplyTo,
            m.EnqueuedTime,
            m.ScheduledEnqueueTime == default ? null : m.ScheduledEnqueueTime,
            m.TimeToLive == TimeSpan.MaxValue ? null : m.TimeToLive.ToString("c", CultureInfo.InvariantCulture),
            m.DeliveryCount,
            m.State.ToString(),
            m.DeadLetterReason,
            m.DeadLetterErrorDescription,
            m.DeadLetterSource,
            m.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value is DateTimeOffset or DateTime or Guid or Uri or TimeSpan
                ? Convert.ToString(p.Value, CultureInfo.InvariantCulture)!
                : p.Value),
            encoding,
            body);
    }

    private sealed record ExportDocument(int FormatVersion, string Source, DateTimeOffset ExportedAt, IReadOnlyList<ExportedMessage> Messages);

    private sealed record ExportedMessage(
        long SequenceNumber,
        string? MessageId,
        string? Subject,
        string? ContentType,
        string? CorrelationId,
        string? SessionId,
        string? PartitionKey,
        string? To,
        string? ReplyTo,
        DateTimeOffset EnqueuedTime,
        DateTimeOffset? ScheduledEnqueueTime,
        string? TimeToLive,
        int DeliveryCount,
        string State,
        string? DeadLetterReason,
        string? DeadLetterErrorDescription,
        string? DeadLetterSource,
        IReadOnlyDictionary<string, object> ApplicationProperties,
        string BodyEncoding,
        string Body);
}
