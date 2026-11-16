using CommunityToolkit.Mvvm.ComponentModel;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.ViewModels;

public sealed record PropertyRow(string Name, string Value);

public sealed partial class MessageRowViewModel : ObservableObject
{
    private (BodyKind Kind, string Text)? _formatted;

    public MessageRowViewModel(MessageView view) => View = view;

    public MessageView View { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public long SequenceNumber => View.SequenceNumber;

    public string MessageId => View.MessageId ?? "";

    public string Subject => View.Subject ?? "";

    public string Enqueued => View.EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public int DeliveryCount => View.DeliveryCount;

    public string DeadLetterReason => View.DeadLetterReason ?? "";

    /// <summary>Blank for normal messages; "Scheduled" or "Deferred" otherwise.</summary>
    public string StateLabel => View.State == Azure.Messaging.ServiceBus.ServiceBusMessageState.Active ? "" : View.State.ToString();

    public string BodyPreview
    {
        get
        {
            var text = Formatted.Text.ReplaceLineEndings(" ");
            return text.Length > 120 ? text[..120] + "…" : text;
        }
    }

    public string BodyKindLabel => Formatted.Kind.ToString();

    public string BodyText => Formatted.Text;

    private (BodyKind Kind, string Text) Formatted => _formatted ??= BodyFormatter.Format(View.Body);

    public IReadOnlyList<PropertyRow> SystemProperties =>
    [
        new("SequenceNumber", View.SequenceNumber.ToString()),
        new("MessageId", View.MessageId ?? ""),
        new("Subject", View.Subject ?? ""),
        new("ContentType", View.ContentType ?? ""),
        new("CorrelationId", View.CorrelationId ?? ""),
        new("SessionId", View.SessionId ?? ""),
        new("PartitionKey", View.PartitionKey ?? ""),
        new("To", View.To ?? ""),
        new("ReplyTo", View.ReplyTo ?? ""),
        new("EnqueuedTime", View.EnqueuedTime.ToString("O")),
        new("ExpiresAt", View.ExpiresAt == DateTimeOffset.MaxValue ? "never" : View.ExpiresAt.ToString("O")),
        new("TimeToLive", View.TimeToLive == TimeSpan.MaxValue ? "∞" : View.TimeToLive.ToString()),
        new("DeliveryCount", View.DeliveryCount.ToString()),
        new("State", View.State.ToString()),
        new("ScheduledEnqueueTime", View.ScheduledEnqueueTime == default ? "" : View.ScheduledEnqueueTime.ToString("O")),
        new("DeadLetterReason", View.DeadLetterReason ?? ""),
        new("DeadLetterErrorDescription", View.DeadLetterErrorDescription ?? ""),
        new("DeadLetterSource", View.DeadLetterSource ?? ""),
        new("BodySize", $"{View.Body.Length:N0} bytes"),
    ];

    public IReadOnlyList<PropertyRow> ApplicationProperties =>
        View.ApplicationProperties
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PropertyRow(p.Key, Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
            .ToList();
}
