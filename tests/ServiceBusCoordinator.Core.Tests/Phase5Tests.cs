using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Tests;

internal static class Messages
{
    public static MessageView Make(
        long seq = 1,
        string body = "{\"orderId\":42}",
        string? messageId = "m-1",
        string? subject = "order.created",
        string? reason = null,
        Dictionary<string, object>? props = null,
        byte[]? rawBody = null) => new(
            seq, messageId, subject, "application/json", "corr-1", null, null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, TimeSpan.MaxValue, default, 1,
            ServiceBusMessageState.Active, reason, null, null,
            props ?? new Dictionary<string, object> { ["tenant"] = "acme", ["priority"] = 5L },
            rawBody ?? Encoding.UTF8.GetBytes(body));
}

public class MessageFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_query_matches_everything(string? q) => Assert.True(MessageFilter.Matches(Messages.Make(), q));

    [Theory]
    [InlineData("ORDER.CREATED")] // subject, case-insensitive
    [InlineData("m-1")] // message id
    [InlineData("corr")] // correlation id
    [InlineData("orderId")] // body
    [InlineData("acme")] // property value
    [InlineData("tenant")] // property name
    public void Matches_text_anywhere(string q) => Assert.True(MessageFilter.Matches(Messages.Make(), q));

    [Fact]
    public void Matches_dead_letter_reason() =>
        Assert.True(MessageFilter.Matches(Messages.Make(reason: "MaxDeliveryCountExceeded"), "maxdelivery"));

    [Fact]
    public void No_match() => Assert.False(MessageFilter.Matches(Messages.Make(), "invoice"));

    [Fact]
    public void Property_query_targets_one_property()
    {
        Assert.True(MessageFilter.Matches(Messages.Make(), "tenant=acm"));
        Assert.False(MessageFilter.Matches(Messages.Make(), "tenant=contoso"));
        Assert.True(MessageFilter.Matches(Messages.Make(), "priority=5"));
    }

    [Fact]
    public void Sequence_number_matches() => Assert.True(MessageFilter.Matches(Messages.Make(seq: 987654), "987654"));
}

public class MessageExporterTests
{
    [Fact]
    public void Exports_text_and_binary_bodies()
    {
        var json = MessageExporter.ToJson(
            "orders/$deadletterqueue",
            [Messages.Make(seq: 1), Messages.Make(seq: 2, rawBody: [0xFF, 0x00, 0x01])],
            DateTimeOffset.UnixEpoch);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("orders/$deadletterqueue", root.GetProperty("source").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("utf-8", messages[0].GetProperty("bodyEncoding").GetString());
        Assert.Equal("{\"orderId\":42}", messages[0].GetProperty("body").GetString());
        Assert.Equal("acme", messages[0].GetProperty("applicationProperties").GetProperty("tenant").GetString());
        Assert.Equal("base64", messages[1].GetProperty("bodyEncoding").GetString());
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x01 }, Convert.FromBase64String(messages[1].GetProperty("body").GetString()!));
    }

    [Fact]
    public void Omits_unset_fields()
    {
        var json = MessageExporter.ToJson("q", [Messages.Make()], DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("timeToLive", json);
        Assert.DoesNotContain("scheduledEnqueueTime", json);
        Assert.DoesNotContain("sessionId", json);
    }
}

public class OutgoingMessageTests
{
    [Fact]
    public void Parses_typed_properties()
    {
        var props = OutgoingMessage.ParseProperties("""
            # comment
            tenant = acme
            count=3
            ratio=0.5
            enabled=true
            code="42"

            """);

        Assert.Equal("acme", props["tenant"]);
        Assert.Equal(3L, props["count"]);
        Assert.Equal(0.5, props["ratio"]);
        Assert.Equal(true, props["enabled"]);
        Assert.Equal("42", props["code"]);
    }

    [Fact]
    public void Rejects_lines_without_equals() =>
        Assert.Throws<FormatException>(() => OutgoingMessage.ParseProperties("oops"));

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var original = new Dictionary<string, object> { ["a"] = "text", ["b"] = 7L, ["c"] = true, ["d"] = "123", ["e"] = 1.25 };
        var parsed = OutgoingMessage.ParseProperties(OutgoingMessage.FormatProperties(original));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Builds_service_bus_message()
    {
        var m = new OutgoingMessage(
            "hello", ContentType: "text/plain", Subject: " s ", MessageId: "", SessionId: "S1",
            TimeToLive: TimeSpan.FromMinutes(5),
            ApplicationProperties: new Dictionary<string, object> { ["k"] = 1L }).ToServiceBusMessage();

        Assert.Equal("hello", m.Body.ToString());
        Assert.Equal("text/plain", m.ContentType);
        Assert.Equal("s", m.Subject);
        Assert.Equal("S1", m.SessionId);
        Assert.Equal(TimeSpan.FromMinutes(5), m.TimeToLive);
        Assert.Equal(1L, m.ApplicationProperties["k"]);
        Assert.Matches("^[0-9a-f]{32}$", m.MessageId); // generated when blank
    }

    [Fact]
    public void Resend_prefill_drops_message_id_and_dead_letter_properties()
    {
        var view = Messages.Make(props: new Dictionary<string, object> { ["DeadLetterReason"] = "x", ["tenant"] = "acme" });
        var prefill = OutgoingMessage.From(view);
        Assert.Null(prefill.MessageId);
        Assert.Equal("{\"orderId\":42}", prefill.Body);
        Assert.Equal("order.created", prefill.Subject);
        Assert.False(prefill.ApplicationProperties!.ContainsKey("DeadLetterReason"));
        Assert.Equal("acme", prefill.ApplicationProperties["tenant"]);
    }
}
