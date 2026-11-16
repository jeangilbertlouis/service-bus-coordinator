using System.Globalization;
using System.Text;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Messages;

/// <summary>
/// Case-insensitive text search over loaded messages: IDs, subject, dead-letter info, property names and values, and
/// the body. A query of the form <c>name=value</c> matches only the application property <c>name</c> containing
/// <c>value</c>. A number matches a sequence number exactly as well as any text.
/// </summary>
public static class MessageFilter
{
    public static bool Matches(MessageView message, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        query = query.Trim();

        var eq = query.IndexOf('=');
        if (eq > 0)
        {
            var key = query[..eq].Trim();
            var value = query[(eq + 1)..].Trim();
            var hit = message.ApplicationProperties.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is not null)
            {
                return Contains(Convert.ToString(hit.Value, CultureInfo.InvariantCulture), value);
            }
        }

        if (long.TryParse(query, out var seq) && message.SequenceNumber == seq)
        {
            return true;
        }

        return Contains(message.MessageId, query)
            || Contains(message.Subject, query)
            || Contains(message.CorrelationId, query)
            || Contains(message.SessionId, query)
            || Contains(message.ContentType, query)
            || Contains(message.DeadLetterReason, query)
            || Contains(message.DeadLetterErrorDescription, query)
            || message.ApplicationProperties.Any(p =>
                Contains(p.Key, query) || Contains(Convert.ToString(p.Value, CultureInfo.InvariantCulture), query))
            || BodyContains(message.Body, query);
    }

    private static bool Contains(string? text, string query) =>
        text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool BodyContains(byte[] body, string query)
    {
        if (body.Length == 0)
        {
            return false;
        }

        // Lenient decode: binary bodies simply won't match text queries.
        return Encoding.UTF8.GetString(body).Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
