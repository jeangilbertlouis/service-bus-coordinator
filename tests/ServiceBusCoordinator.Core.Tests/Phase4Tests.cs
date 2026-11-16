using Azure.Messaging.ServiceBus;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Tests;

public class EmulatorConnectionStringTests
{
    private const string Emulator = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    [Fact]
    public void Adds_management_port_for_emulator() =>
        Assert.Equal(
            "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
            ConnectionStringInfo.GetAdministrationConnectionString(Emulator));

    [Fact]
    public void Keeps_explicit_port() =>
        Assert.Equal(
            "Endpoint=sb://localhost:7000;SharedAccessKey=x;UseDevelopmentEmulator=true",
            ConnectionStringInfo.GetAdministrationConnectionString("Endpoint=sb://localhost:7000;SharedAccessKey=x;UseDevelopmentEmulator=true"));

    [Fact]
    public void Leaves_cloud_connection_strings_alone()
    {
        const string cs = "Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v";
        Assert.Same(cs, ConnectionStringInfo.GetAdministrationConnectionString(cs));
    }

    [Fact]
    public void Flags_emulator()
    {
        Assert.True(ConnectionStringInfo.TryParse(Emulator, out var info, out _));
        Assert.True(info!.IsEmulator);
        Assert.Equal("localhost (emulator)", info.NamespaceName);
    }

    [Theory]
    [InlineData("Endpoint=sb://contoso-prod.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v", "contoso-prod")]
    [InlineData("Endpoint=sb://orders.servicebus.usgovcloudapi.net/;SharedAccessKeyName=k;SharedAccessKey=v;EntityPath=q", "orders")]
    public void Extracts_namespace_name(string cs, string expected)
    {
        Assert.True(ConnectionStringInfo.TryParse(cs, out var info, out _));
        Assert.Equal(expected, info!.NamespaceName);
    }
}

public class ThrottlingTests
{
    [Fact]
    public async Task Retries_when_service_is_busy()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var result = await Throttling.RunAsync(
            _ => ++calls < 3 ? throw new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy) : Task.FromResult(42),
            waits.Add,
            CancellationToken.None,
            baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits.Count);
        Assert.True(waits[1] > waits[0]);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ServiceBusException>(() => Throttling.RunAsync<int>(
            _ =>
            {
                calls++;
                throw new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
            },
            null,
            CancellationToken.None,
            maxAttempts: 3,
            baseDelay: TimeSpan.FromMilliseconds(1)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_other_failures()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ServiceBusException>(() => Throttling.RunAsync<int>(
            _ =>
            {
                calls++;
                throw new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound);
            },
            null,
            CancellationToken.None,
            baseDelay: TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, calls);
    }
}

public class MessageSourceTests
{
    [Theory]
    [InlineData(MessageSource.Active, "")]
    [InlineData(MessageSource.DeadLetter, "/$deadletterqueue")]
    [InlineData(MessageSource.TransferDeadLetter, "/$transfer/$deadletterqueue")]
    public void Path_suffix(MessageSource source, string suffix) => Assert.Equal(suffix, source.PathSuffix());

    [Fact]
    public void Maps_to_sdk_sub_queue()
    {
        Assert.Equal(SubQueue.None, MessageSource.Active.ToSubQueue());
        Assert.Equal(SubQueue.DeadLetter, MessageSource.DeadLetter.ToSubQueue());
        Assert.Equal(SubQueue.TransferDeadLetter, MessageSource.TransferDeadLetter.ToSubQueue());
    }

    [Fact]
    public void Only_active_messages_of_session_entities_need_a_session()
    {
        var sessionQueue = new EntityInfo(EntityKind.Queue, "q", RequiresSession: true);
        Assert.True(MessageOperations.NeedsSessionReceiver(sessionQueue, MessageSource.Active));
        Assert.False(MessageOperations.NeedsSessionReceiver(sessionQueue, MessageSource.DeadLetter));
        Assert.False(MessageOperations.NeedsSessionReceiver(sessionQueue, MessageSource.TransferDeadLetter));
        Assert.False(MessageOperations.NeedsSessionReceiver(sessionQueue with { RequiresSession = false }, MessageSource.Active));
    }
}
