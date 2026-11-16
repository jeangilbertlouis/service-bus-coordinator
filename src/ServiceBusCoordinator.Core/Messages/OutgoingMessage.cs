using System.Globalization;
using System.Text;
using Azure.Messaging.ServiceBus;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Messages;

/// <summary>A message composed in the app (new, or edited from an existing one) ready to send.</summary>
public sealed record OutgoingMessage(
    string Body,
    string? ContentType = null,
    string? Subject = null,
    string? MessageId = null,
    string? CorrelationId = null,
    string? SessionId = null,
    string? To = null,
    string? ReplyTo = null,
    TimeSpan? TimeToLive = null,
    DateTimeOffset? ScheduledEnqueueTime = null,
    IReadOnlyDictionary<string, object>? ApplicationProperties = null)
{
    public ServiceBusMessage ToServiceBusMessage()
    {
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(Body))
        {
            // The SDK rejects a null/empty MessageId, so generate one when none is given.
            MessageId = Blank(MessageId) ?? Guid.NewGuid().ToString("N"),
        };

        if (Blank(ContentType) is { } contentType)
        {
            message.ContentType = contentType;
        }

        if (Blank(Subject) is { } subject)
        {
            message.Subject = subject;
        }

        if (Blank(CorrelationId) is { } correlationId)
        {
            message.CorrelationId = correlationId;
        }

        if (Blank(SessionId) is { } sessionId)
        {
            message.SessionId = sessionId;
        }

        if (Blank(To) is { } to)
        {
            message.To = to;
        }

        if (Blank(ReplyTo) is { } replyTo)
        {
            message.ReplyTo = replyTo;
        }

        if (TimeToLive is { } ttl)
        {
            message.TimeToLive = ttl;
        }

        foreach (var (key, value) in ApplicationProperties ?? new Dictionary<string, object>())
        {
            message.ApplicationProperties[key] = value;
        }

        return message;
    }

    /// <summary>Pre-fills a new message from an existing one (for "edit &amp; resend"). Body must be text.</summary>
    public static OutgoingMessage From(MessageView m) => new(
        Encoding.UTF8.GetString(m.Body),
        m.ContentType,
        m.Subject,
        MessageId: null, // new id by default so duplicate detection doesn't drop the resend
        m.CorrelationId,
        m.SessionId,
        m.To,
        m.ReplyTo,
        ApplicationProperties: m.ApplicationProperties
            .Where(p => p.Key is not (MessageCloner.DeadLetterReasonKey or MessageCloner.DeadLetterErrorDescriptionKey))
            .ToDictionary(p => p.Key, p => p.Value));

    /// <summary>
    /// Parses one <c>name=value</c> per line. Values are typed: <c>true/false</c> → bool, whole numbers → long,
    /// decimals → double, <c>"quoted"</c> → string as-is, anything else → string. Blank lines and <c>#</c> comments are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, object> ParseProperties(string? text)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                throw new FormatException($"Line {lineNumber}: expected name=value.");
            }

            result[line[..eq].Trim()] = ParseValue(line[(eq + 1)..].Trim());
        }

        return result;
    }

    /// <summary>Inverse of <see cref="ParseProperties"/> for pre-filling the editor.</summary>
    public static string FormatProperties(IReadOnlyDictionary<string, object>? properties) =>
        properties is null
            ? ""
            : string.Join("\n", properties.Select(p => $"{p.Key}={FormatValue(p.Value)}"));

    private static object ParseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        if (bool.TryParse(value, out var b))
        {
            return b;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (value.Contains('.') && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return value;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "\"\"",
        bool b => b ? "true" : "false",
        string s when s.Length == 0 || bool.TryParse(s, out _) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            => $"\"{s}\"",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
