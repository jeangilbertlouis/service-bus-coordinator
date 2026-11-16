using System.Globalization;
using Azure.Messaging.ServiceBus.Administration;

namespace ServiceBusCoordinator.Core.Explorer;

public sealed record DetailItem(string Name, string Value, string? Hint = null);

public sealed record DetailGroup(string Title, IReadOnlyList<DetailItem> Items);

/// <summary>A subscription rule: its filter (and optional action) in readable form.</summary>
public sealed record RuleDetails(string Name, string FilterKind, string Filter, string? Action);

/// <summary>Configuration and runtime details of a queue, topic or subscription, grouped for display.</summary>
public sealed record EntityDetails(IReadOnlyList<DetailGroup> Groups, IReadOnlyList<RuleDetails> Rules)
{
    public static EntityDetails FromQueue(QueueProperties p, QueueRuntimeProperties r) => new(
    [
        new("General",
        [
            new("Status", p.Status.ToString()),
            new("Created", Format.Date(r.CreatedAt)),
            new("Updated", Format.Date(r.UpdatedAt)),
            new("Last accessed", Format.Date(r.AccessedAt)),
            new("Size", Format.Bytes(r.SizeInBytes), $"{r.SizeInBytes:N0} bytes"),
            new("Max size", Format.Megabytes(p.MaxSizeInMegabytes)),
            new("Max message size", Format.Kilobytes(p.MaxMessageSizeInKilobytes)),
            new("Partitioning", Format.YesNo(p.EnablePartitioning)),
            new("Batched operations", Format.YesNo(p.EnableBatchedOperations)),
            new("User metadata", Format.Text(p.UserMetadata)),
        ]),
        new("Message counts",
        [
            new("Total", r.TotalMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Active", r.ActiveMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Dead-letter", r.DeadLetterMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Scheduled", r.ScheduledMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Transfer", r.TransferMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Transfer dead-letter", r.TransferDeadLetterMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
        ]),
        new("Message handling",
        [
            new("Lock duration", Format.Duration(p.LockDuration)),
            new("Max delivery count", p.MaxDeliveryCount.ToString(CultureInfo.CurrentCulture)),
            new("Default time to live", Format.Duration(p.DefaultMessageTimeToLive)),
            new("Requires session", Format.YesNo(p.RequiresSession)),
            new("Duplicate detection", Format.YesNo(p.RequiresDuplicateDetection)),
            new("Duplicate detection window", p.RequiresDuplicateDetection ? Format.Duration(p.DuplicateDetectionHistoryTimeWindow) : "—"),
            new("Auto-delete when idle", Format.Duration(p.AutoDeleteOnIdle)),
        ]),
        new("Dead-lettering & forwarding",
        [
            new("Dead-letter on expiration", Format.YesNo(p.DeadLetteringOnMessageExpiration)),
            new("Forward to", Format.Text(p.ForwardTo)),
            new("Forward dead-letters to", Format.Text(p.ForwardDeadLetteredMessagesTo)),
        ]),
        AuthorizationGroup(p.AuthorizationRules),
    ],
    []);

    public static EntityDetails FromTopic(TopicProperties p, TopicRuntimeProperties r) => new(
    [
        new("General",
        [
            new("Status", p.Status.ToString()),
            new("Created", Format.Date(r.CreatedAt)),
            new("Updated", Format.Date(r.UpdatedAt)),
            new("Last accessed", Format.Date(r.AccessedAt)),
            new("Size", Format.Bytes(r.SizeInBytes), $"{r.SizeInBytes:N0} bytes"),
            new("Max size", Format.Megabytes(p.MaxSizeInMegabytes)),
            new("Max message size", Format.Kilobytes(p.MaxMessageSizeInKilobytes)),
            new("Partitioning", Format.YesNo(p.EnablePartitioning)),
            new("Batched operations", Format.YesNo(p.EnableBatchedOperations)),
            new("Ordering support", Format.YesNo(p.SupportOrdering)),
            new("User metadata", Format.Text(p.UserMetadata)),
        ]),
        new("Counts",
        [
            new("Subscriptions", r.SubscriptionCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Scheduled messages", r.ScheduledMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
        ]),
        new("Message handling",
        [
            new("Default time to live", Format.Duration(p.DefaultMessageTimeToLive)),
            new("Duplicate detection", Format.YesNo(p.RequiresDuplicateDetection)),
            new("Duplicate detection window", p.RequiresDuplicateDetection ? Format.Duration(p.DuplicateDetectionHistoryTimeWindow) : "—"),
            new("Auto-delete when idle", Format.Duration(p.AutoDeleteOnIdle)),
        ]),
        AuthorizationGroup(p.AuthorizationRules),
    ],
    []);

    public static EntityDetails FromSubscription(SubscriptionProperties p, SubscriptionRuntimeProperties r, IEnumerable<RuleProperties> rules) => new(
    [
        new("General",
        [
            new("Status", p.Status.ToString()),
            new("Created", Format.Date(r.CreatedAt)),
            new("Updated", Format.Date(r.UpdatedAt)),
            new("Last accessed", Format.Date(r.AccessedAt)),
            new("User metadata", Format.Text(p.UserMetadata)),
        ]),
        new("Message counts",
        [
            new("Total", r.TotalMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Active", r.ActiveMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Dead-letter", r.DeadLetterMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Transfer", r.TransferMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
            new("Transfer dead-letter", r.TransferDeadLetterMessageCount.ToString("N0", CultureInfo.CurrentCulture)),
        ]),
        new("Message handling",
        [
            new("Lock duration", Format.Duration(p.LockDuration)),
            new("Max delivery count", p.MaxDeliveryCount.ToString(CultureInfo.CurrentCulture)),
            new("Default time to live", Format.Duration(p.DefaultMessageTimeToLive)),
            new("Requires session", Format.YesNo(p.RequiresSession)),
            new("Batched operations", Format.YesNo(p.EnableBatchedOperations)),
            new("Auto-delete when idle", Format.Duration(p.AutoDeleteOnIdle)),
        ]),
        new("Dead-lettering & forwarding",
        [
            new("Dead-letter on expiration", Format.YesNo(p.DeadLetteringOnMessageExpiration)),
            new("Dead-letter on filter errors", Format.YesNo(p.EnableDeadLetteringOnFilterEvaluationExceptions)),
            new("Forward to", Format.Text(p.ForwardTo)),
            new("Forward dead-letters to", Format.Text(p.ForwardDeadLetteredMessagesTo)),
        ]),
    ],
    rules.Select(DescribeRule).ToList());

    public static RuleDetails DescribeRule(RuleProperties rule)
    {
        var (kind, filter) = rule.Filter switch
        {
            TrueRuleFilter => ("All messages", "1=1"),
            FalseRuleFilter => ("No messages", "1=0"),
            SqlRuleFilter sql => ("SQL", sql.SqlExpression),
            CorrelationRuleFilter c => ("Correlation", DescribeCorrelation(c)),
            null => ("None", "—"),
            var other => (other.GetType().Name, other.ToString() ?? ""),
        };

        var action = rule.Action is SqlRuleAction sqlAction ? sqlAction.SqlExpression : null;
        return new RuleDetails(rule.Name, kind, filter, action);
    }

    private static string DescribeCorrelation(CorrelationRuleFilter c)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{name} = '{value}'");
            }
        }

        Add("CorrelationId", c.CorrelationId);
        Add("MessageId", c.MessageId);
        Add("Subject", c.Subject);
        Add("To", c.To);
        Add("ReplyTo", c.ReplyTo);
        Add("SessionId", c.SessionId);
        Add("ReplyToSessionId", c.ReplyToSessionId);
        Add("ContentType", c.ContentType);
        foreach (var (key, value) in c.ApplicationProperties)
        {
            Add(key, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "(matches everything)" : string.Join(" AND ", parts);
    }

    private static DetailGroup AuthorizationGroup(AuthorizationRules rules) => new(
        "Shared access policies",
        rules.Count == 0
            ? [new DetailItem("Policies", "None on this entity (namespace policies still apply)")]
            : rules.Select(rule => new DetailItem(rule.KeyName, string.Join(", ", rule.Rights))).ToList());
}

/// <summary>Human-friendly formatting for entity properties.</summary>
public static class Format
{
    /// <summary>Service Bus uses TimeSpan.MaxValue (or ~10,675,199 days) for "never".</summary>
    public static string Duration(TimeSpan value)
    {
        if (value >= TimeSpan.FromDays(10_000))
        {
            return "Never";
        }

        if (value == TimeSpan.Zero)
        {
            return "0 s";
        }

        var parts = new List<string>();
        if (value.Days > 0)
        {
            parts.Add($"{value.Days} d");
        }

        if (value.Hours > 0)
        {
            parts.Add($"{value.Hours} h");
        }

        if (value.Minutes > 0)
        {
            parts.Add($"{value.Minutes} min");
        }

        if (value.Seconds > 0 || value.Milliseconds > 0)
        {
            parts.Add(value.Milliseconds > 0 ? $"{value.TotalSeconds % 60:0.###} s" : $"{value.Seconds} s");
        }

        return string.Join(" ", parts);
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
    };

    public static string Megabytes(long megabytes) =>
        megabytes >= 1024 && megabytes % 1024 == 0 ? $"{megabytes / 1024:N0} GB" : $"{megabytes:N0} MB";

    public static string Kilobytes(long? kilobytes) => kilobytes switch
    {
        null => "Default",
        >= 1024 when kilobytes % 1024 == 0 => $"{kilobytes / 1024:N0} MB",
        _ => $"{kilobytes:N0} KB",
    };

    public static string YesNo(bool value) => value ? "Yes" : "No";

    public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    public static string Date(DateTimeOffset value) =>
        value == default || value.Year <= 1 ? "—" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
