using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Explorer;
using ServiceBusCoordinator.Core.Messages;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Tests;

/// <summary>
/// Runs only when SBC_TEST_CONNECTION holds a connection string with Manage rights — the local emulator
/// (see emulator/) or a disposable dev namespace. Each test creates and deletes its own queue.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string Variable = "SBC_TEST_CONNECTION";

    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a Service Bus (or emulator) connection string with Manage rights to run.";
        }
    }
}

[Collection("integration")]
public sealed class IntegrationTests : IAsyncLifetime
{
    private static readonly string ConnectionString = Environment.GetEnvironmentVariable(IntegrationFactAttribute.Variable) ?? "";

    private readonly List<string> _queues = [];
    private ServiceBusAdministrationClient? _admin;
    private ServiceBusClient? _client;
    private NamespaceConnection? _connection;

    private ServiceBusAdministrationClient Admin => _admin!;

    private ServiceBusClient Client => _client!;

    private NamespaceConnection Connection => _connection!;

    public Task InitializeAsync()
    {
        if (ConnectionString.Length > 0)
        {
            _admin = new ServiceBusAdministrationClient(ConnectionStringInfo.GetAdministrationConnectionString(ConnectionString));
            _client = new ServiceBusClient(ConnectionString);
            _connection = new NamespaceConnection(new ConnectionProfile { Name = "test", ConnectionString = ConnectionString });
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var q in _queues)
        {
            try
            {
                await Admin.DeleteQueueAsync(q);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    [IntegrationFact]
    public async Task Discovers_queue_with_counts()
    {
        var queue = await CreateQueueAsync();
        await SendAsync(queue, 2);

        var snapshot = await Connection.LoadAsync();

        var found = Assert.Single(snapshot.Queues, q => q.Name == queue.Name);
        Assert.False(snapshot.LimitedAccess);
        Assert.Equal(2, found.Counts!.Active);
    }

    [IntegrationFact]
    public async Task Resubmits_all_dead_letters()
    {
        var queue = await CreateQueueAsync();
        await SendAsync(queue, 3);
        await DeadLetterAllAsync(queue, 3);

        var result = await Connection.Messages.ResubmitAsync(queue, MessageSource.DeadLetter, new ResubmitOptions(queue.Name), null);

        Assert.Equal(3, result.Resubmitted);
        Assert.Empty((await Connection.Messages.PeekAsync(queue, MessageSource.DeadLetter, null, 10)).Messages);
        var active = (await Connection.Messages.PeekAsync(queue, MessageSource.Active, null, 10)).Messages;
        Assert.Equal(3, active.Count);
        Assert.All(active, m => Assert.Equal(1, m.ApplicationProperties[MessageCloner.ResubmitCountKey]));
    }

    [IntegrationFact]
    public async Task Resubmits_only_selected_dead_letters()
    {
        var queue = await CreateQueueAsync();
        await SendAsync(queue, 3);
        await DeadLetterAllAsync(queue, 3);
        var dlq = (await Connection.Messages.PeekAsync(queue, MessageSource.DeadLetter, null, 10)).Messages;
        var pick = dlq[1].SequenceNumber;

        var result = await Connection.Messages.ResubmitAsync(
            queue, MessageSource.DeadLetter, new ResubmitOptions(queue.Name, SequenceNumbers: new HashSet<long> { pick }), null);

        Assert.Equal(1, result.Resubmitted);
        var remaining = (await Connection.Messages.PeekAsync(queue, MessageSource.DeadLetter, null, 10)).Messages;
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, m => m.SequenceNumber == pick);
    }

    [IntegrationFact]
    public async Task Purge_removes_active_deferred_and_scheduled()
    {
        var queue = await CreateQueueAsync();
        await SendAsync(queue, 4);
        await using (var sender = Client.CreateSender(queue.Name))
        {
            await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddMinutes(30));
        }

        await using (var receiver = Client.CreateReceiver(queue.Name))
        {
            var m = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            await receiver.DeferMessageAsync(m);
        }

        var deleted = await Connection.Messages.PurgeAsync(queue, MessageSource.Active, null);

        Assert.Equal(5, deleted);
        Assert.Empty((await Connection.Messages.PeekAsync(queue, MessageSource.Active, null, 10)).Messages);
    }

    [IntegrationFact]
    public async Task Cancels_scheduled_and_deletes_deferred_by_sequence_number()
    {
        var queue = await CreateQueueAsync();
        await SendAsync(queue, 1);
        long scheduled;
        await using (var sender = Client.CreateSender(queue.Name))
        {
            scheduled = await sender.ScheduleMessageAsync(new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddMinutes(30));
        }

        long deferred;
        await using (var receiver = Client.CreateReceiver(queue.Name))
        {
            var m = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            deferred = m.SequenceNumber;
            await receiver.DeferMessageAsync(m);
        }

        Assert.Equal(1, await Connection.Messages.CancelScheduledAsync(queue, [scheduled]));
        Assert.Equal(1, await Connection.Messages.DeleteDeferredAsync(queue, MessageSource.Active, [deferred]));
        Assert.Empty((await Connection.Messages.PeekAsync(queue, MessageSource.Active, null, 10)).Messages);
    }

    [IntegrationFact]
    public async Task Sessions_can_be_found_peeked_and_purged()
    {
        var queue = await CreateQueueAsync(requiresSession: true);
        await using (var sender = Client.CreateSender(queue.Name))
        {
            await sender.SendMessagesAsync([
                new ServiceBusMessage("a1") { SessionId = "A" },
                new ServiceBusMessage("a2") { SessionId = "A" },
                new ServiceBusMessage("b1") { SessionId = "B" },
            ]);
        }

        var sessions = await Connection.Messages.ListSessionsAsync(queue);
        Assert.Equal(["A", "B"], sessions.Select(s => s.SessionId));

        var peekA = await Connection.Messages.PeekAsync(queue, MessageSource.Active, null, 10, "A");
        Assert.Equal("A", peekA.SessionId);
        Assert.Equal(2, peekA.Messages.Count);

        var deleted = await Connection.Messages.PurgeAsync(queue, MessageSource.Active, null);
        Assert.Equal(3, deleted);
        Assert.Empty(await Connection.Messages.ListSessionsAsync(queue));
    }

    private async Task<EntityInfo> CreateQueueAsync(bool requiresSession = false)
    {
        var name = "sbc-test-" + Guid.NewGuid().ToString("N")[..10];
        await Admin.CreateQueueAsync(new CreateQueueOptions(name) { RequiresSession = requiresSession, MaxDeliveryCount = 10 });
        _queues.Add(name);
        return new EntityInfo(EntityKind.Queue, name, RequiresSession: requiresSession);
    }

    private async Task SendAsync(EntityInfo queue, int count)
    {
        await using var sender = Client.CreateSender(queue.Name);
        await sender.SendMessagesAsync(Enumerable.Range(1, count).Select(i => new ServiceBusMessage($"{{\"n\":{i}}}") { MessageId = $"m{i}" }));
    }

    private async Task DeadLetterAllAsync(EntityInfo queue, int count)
    {
        await using var receiver = Client.CreateReceiver(queue.Name);
        var done = 0;
        while (done < count)
        {
            foreach (var m in await receiver.ReceiveMessagesAsync(count - done, TimeSpan.FromSeconds(5)))
            {
                await receiver.DeadLetterMessageAsync(m, "test", "dead-lettered by test");
                done++;
            }
        }
    }
}
