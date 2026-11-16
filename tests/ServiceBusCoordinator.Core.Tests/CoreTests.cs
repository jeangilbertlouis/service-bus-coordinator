using System.Text;
using Azure.Messaging.ServiceBus;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Tests;

public class ConnectionStringInfoTests
{
    [Fact]
    public void Parses_namespace_and_key_name()
    {
        const string cs = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc=";
        Assert.True(ConnectionStringInfo.TryParse(cs, out var info, out _));
        Assert.Equal("contoso.servicebus.windows.net", info!.FullyQualifiedNamespace);
        Assert.Equal("RootManageSharedAccessKey", info.KeyName);
        Assert.Null(info.EntityPath);
    }

    [Fact]
    public void Parses_entity_path()
    {
        const string cs = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=abc=;EntityPath=orders";
        Assert.True(ConnectionStringInfo.TryParse(cs, out var info, out _));
        Assert.Equal("orders", info!.EntityPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a connection string")]
    public void Rejects_invalid(string cs)
    {
        Assert.False(ConnectionStringInfo.TryParse(cs, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public class SecretScrubberTests
{
    [Fact]
    public void Hides_keys_and_signatures()
    {
        var text = "Endpoint=sb://x/;SharedAccessKeyName=n;SharedAccessKey=s3cr3t+/=;EntityPath=q and SharedAccessSignature=SharedAccessSignature sr=x";
        var scrubbed = SecretScrubber.Scrub(text);
        Assert.DoesNotContain("s3cr3t", scrubbed);
        Assert.Contains("SharedAccessKey=***", scrubbed);
        Assert.Contains("SharedAccessKeyName=n", scrubbed);
    }
}

public class ConnectionRepositoryTests
{
    [Fact]
    public void Round_trips_profiles()
    {
        var store = new InMemorySecretStore();
        var repo = new ConnectionRepository(store);
        var profile = new ConnectionProfile { Name = "dev", ConnectionString = "Endpoint=sb://a/;SharedAccessKeyName=k;SharedAccessKey=v", ManualEntities = ["orders"] };

        repo.Save([profile]);
        var loaded = repo.Load();

        var single = Assert.Single(loaded);
        Assert.Equal(profile.Id, single.Id);
        Assert.Equal("dev", single.Name);
        Assert.Equal(profile.ConnectionString, single.ConnectionString);
        Assert.Equal(["orders"], single.ManualEntities);
    }

    [Fact]
    public void Empty_store_loads_nothing() => Assert.Empty(new ConnectionRepository(new InMemorySecretStore()).Load());
}

public class EntityInfoTests
{
    [Fact]
    public void Parses_queue() => Assert.Equal(new EntityInfo(EntityKind.Queue, "orders", IsManual: true), EntityInfo.ParseManual(" orders "));

    [Theory]
    [InlineData("events/subscriptions/audit")]
    [InlineData("events/audit")]
    public void Parses_subscription(string path)
    {
        var e = EntityInfo.ParseManual(path);
        Assert.Equal(EntityKind.Subscription, e.Kind);
        Assert.Equal("events", e.TopicName);
        Assert.Equal("audit", e.Name);
        Assert.Equal("events/subscriptions/audit", e.Path);
        Assert.Equal("events", e.DefaultResubmitTarget);
    }

    [Fact]
    public void Rejects_bad_path() => Assert.Throws<FormatException>(() => EntityInfo.ParseManual("a/b/c/d"));
}

public class MessageClonerTests
{
    private static ServiceBusReceivedMessage DeadLettered(IDictionary<string, object>? extra = null)
    {
        var props = new Dictionary<string, object>
        {
            ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
            ["DeadLetterErrorDescription"] = "too many tries",
            ["tenant"] = "acme",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, object>())
        {
            props[k] = v;
        }

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{\"id\":1}"),
            messageId: "m-1",
            sessionId: "s-1",
            correlationId: "c-1",
            subject: "order.created",
            contentType: "application/json",
            properties: props,
            sequenceNumber: 42,
            deliveryCount: 10);
    }

    [Fact]
    public void Copies_body_and_identity()
    {
        var clone = MessageCloner.Clone(DeadLettered(), false, false, "orders", DateTimeOffset.UnixEpoch);
        Assert.Equal("{\"id\":1}", clone.Body.ToString());
        Assert.Equal("m-1", clone.MessageId);
        Assert.Equal("s-1", clone.SessionId);
        Assert.Equal("c-1", clone.CorrelationId);
        Assert.Equal("order.created", clone.Subject);
        Assert.Equal("acme", clone.ApplicationProperties["tenant"]);
        Assert.Equal(default, clone.ScheduledEnqueueTime);
    }

    [Fact]
    public void Strips_dead_letter_properties_when_asked()
    {
        var clone = MessageCloner.Clone(DeadLettered(), true, false, "orders", DateTimeOffset.UnixEpoch);
        Assert.False(clone.ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.False(clone.ApplicationProperties.ContainsKey("DeadLetterErrorDescription"));
    }

    [Fact]
    public void Keeps_dead_letter_properties_by_default()
    {
        var clone = MessageCloner.Clone(DeadLettered(), false, false, "orders", DateTimeOffset.UnixEpoch);
        Assert.Equal("MaxDeliveryCountExceeded", clone.ApplicationProperties["DeadLetterReason"]);
    }

    [Fact]
    public void Stamps_and_increments_resubmit_headers()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var clone = MessageCloner.Clone(DeadLettered(new Dictionary<string, object> { ["x-resubmit-count"] = 2 }), false, true, "orders/$deadletterqueue", now);
        Assert.Equal(3, clone.ApplicationProperties[MessageCloner.ResubmitCountKey]);
        Assert.Equal("orders/$deadletterqueue", clone.ApplicationProperties[MessageCloner.ResubmittedFromKey]);
        Assert.Equal(42L, clone.ApplicationProperties[MessageCloner.OriginalSequenceNumberKey]);
        Assert.Equal("2026-11-16T12:00:00.0000000Z", clone.ApplicationProperties[MessageCloner.ResubmittedAtKey]);
    }
}

public class BodyFormatterTests
{
    [Fact]
    public void Pretty_prints_json()
    {
        var (kind, text) = BodyFormatter.Format(Encoding.UTF8.GetBytes("{\"a\":1}"));
        Assert.Equal(BodyKind.Json, kind);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void Formats_xml()
    {
        var (kind, text) = BodyFormatter.Format(Encoding.UTF8.GetBytes("<a><b>1</b></a>"));
        Assert.Equal(BodyKind.Xml, kind);
        Assert.Contains("<b>1</b>", text);
    }

    [Fact]
    public void Plain_text_stays_text() => Assert.Equal((BodyKind.Text, "hello"), BodyFormatter.Format("hello"u8));

    [Fact]
    public void Binary_becomes_hex()
    {
        var (kind, text) = BodyFormatter.Format(new byte[] { 0x00, 0xFF, 0x10 });
        Assert.Equal(BodyKind.Binary, kind);
        Assert.StartsWith("00000000  00 FF 10", text);
    }

    [Fact]
    public void Empty_body() => Assert.Equal(BodyKind.Empty, BodyFormatter.Format([]).Kind);
}
