using System.Transactions;
using Azure.Messaging.ServiceBus;
using ServiceBusCoordinator.Core.Explorer;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.Core.Messages;

public sealed record ResubmitOptions(
    string TargetEntityPath,
    bool StripDeadLetterProperties = false,
    bool StampResubmitHeaders = true,
    IReadOnlySet<long>? SequenceNumbers = null,
    long? MaxMessages = null);

public sealed record ResubmitResult(int Resubmitted, int Failed, bool UsedTransactions, IReadOnlyList<string> Errors);

/// <summary>Peek, purge and dead-letter resubmit for one namespace.</summary>
public sealed class MessageOperations
{
    private const int PurgeBatchSize = 500;
    private const int ReceiveBatchSize = 100;
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(3);

    private readonly NamespaceConnection _connection;

    internal MessageOperations(NamespaceConnection connection) => _connection = connection;

    /// <summary>Non-destructive peek starting at <paramref name="fromSequenceNumber"/>.</summary>
    public async Task<IReadOnlyList<MessageView>> PeekAsync(
        EntityInfo entity, bool deadLetter, long? fromSequenceNumber, int maxMessages, CancellationToken ct = default)
    {
        EnsureSupported(entity, deadLetter);
        await using var receiver = CreateReceiver(_connection.Client, entity, deadLetter, ServiceBusReceiveMode.PeekLock);
        var messages = await receiver.PeekMessagesAsync(maxMessages, fromSequenceNumber, ct);
        return messages.Select(MessageView.From).ToList();
    }

    /// <summary>Deletes every message enqueued before the purge started. Returns the number deleted.</summary>
    public async Task<long> PurgeAsync(EntityInfo entity, bool deadLetter, IProgress<long>? progress, CancellationToken ct = default)
    {
        EnsureSupported(entity, deadLetter);
        var cutoff = DateTimeOffset.UtcNow;
        long total = 0;

        // The SDK's server-side batch delete is not public in the stable package yet, so drain with ReceiveAndDelete.
        // Stop once we reach messages that arrived after the purge started, so a busy entity can't keep us looping.
        await using var receiver = CreateReceiver(_connection.Client, entity, deadLetter, ServiceBusReceiveMode.ReceiveAndDelete);
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(PurgeBatchSize, ReceiveWait, ct);
            if (batch.Count == 0)
            {
                return total;
            }

            total += batch.Count;
            progress?.Report(total);
            if (batch.Any(m => m.EnqueuedTime > cutoff))
            {
                return total;
            }
        }
    }

    /// <summary>
    /// Moves dead-lettered messages to <see cref="ResubmitOptions.TargetEntityPath"/>. Each send + complete runs in a
    /// cross-entity transaction when the service allows it; otherwise it sends then completes (at-least-once).
    /// </summary>
    public async Task<ResubmitResult> ResubmitAsync(
        EntityInfo source, ResubmitOptions options, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (!source.HasMessages)
        {
            throw new InvalidOperationException("Select a queue or subscription.");
        }

        if (string.IsNullOrWhiteSpace(options.TargetEntityPath))
        {
            throw new ArgumentException("A target entity is required.", nameof(options));
        }

        var errors = new List<string>();
        var held = new Dictionary<long, ServiceBusReceivedMessage>();
        var selection = options.SequenceNumbers;
        int done = 0, failed = 0;
        var useTransactions = true;

        await using var txClient = new ServiceBusClient(
            _connection.Profile.ConnectionString,
            new ServiceBusClientOptions { EnableCrossEntityTransactions = true });

        // With cross-entity transactions the first operation fixes the "via" entity, so receive comes first.
        var receiver = CreateReceiver(txClient, source, deadLetter: true, ServiceBusReceiveMode.PeekLock);
        var txSender = txClient.CreateSender(options.TargetEntityPath);
        ServiceBusSender? plainSender = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (selection is not null && done + failed >= selection.Count)
                {
                    break;
                }

                if (options.MaxMessages is { } max && done + failed >= max)
                {
                    break;
                }

                var batch = await receiver.ReceiveMessagesAsync(ReceiveBatchSize, ReceiveWait, ct);
                if (batch.Count == 0)
                {
                    break;
                }

                var sawNew = false;
                foreach (var message in batch)
                {
                    if (selection is not null && !selection.Contains(message.SequenceNumber))
                    {
                        // Keep the lock so the next receive moves past it; released at the end.
                        sawNew |= !held.ContainsKey(message.SequenceNumber);
                        held[message.SequenceNumber] = message;
                        continue;
                    }

                    sawNew = true;
                    var outgoing = MessageCloner.Clone(
                        message, options.StripDeadLetterProperties, options.StampResubmitHeaders, source.Path, DateTimeOffset.UtcNow);

                    try
                    {
                        var moved = false;
                        if (useTransactions)
                        {
                            try
                            {
                                using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
                                await txSender.SendMessageAsync(outgoing, ct);
                                await receiver.CompleteMessageAsync(message, ct);
                                scope.Complete();
                                moved = true;
                            }
                            catch (Exception ex) when (done == 0 && ex is not OperationCanceledException)
                            {
                                // Transactions rejected for this entity pair — use send-then-complete from now on.
                                useTransactions = false;
                            }
                        }

                        if (!moved)
                        {
                            plainSender ??= _connection.Client.CreateSender(options.TargetEntityPath);
                            await plainSender.SendMessageAsync(outgoing, ct);
                            await receiver.CompleteMessageAsync(message, ct);
                        }

                        done++;
                        progress?.Report(done);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        errors.Add($"#{message.SequenceNumber}: {SecretScrubber.Scrub(ex.Message)}");
                        await TryAbandonAsync(receiver, message);
                    }
                }

                if (!sawNew)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var message in held.Values)
            {
                await TryAbandonAsync(receiver, message);
            }

            if (plainSender is not null)
            {
                await plainSender.DisposeAsync();
            }
        }

        return new ResubmitResult(done, failed, useTransactions && done > 0, errors);
    }

    private static async Task TryAbandonAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message);
        }
        catch (Exception)
        {
            // Lock may already be lost; the message becomes available again when it expires.
        }
    }

    private static void EnsureSupported(EntityInfo entity, bool deadLetter)
    {
        if (!entity.HasMessages)
        {
            throw new InvalidOperationException("Topics do not hold messages. Select one of its subscriptions.");
        }

        if (entity.RequiresSession && !deadLetter)
        {
            throw new NotSupportedException(
                "This entity is session-enabled. Browsing active session messages is planned (phase 4); the dead-letter queue can be used now.");
        }
    }

    private static ServiceBusReceiver CreateReceiver(
        ServiceBusClient client, EntityInfo entity, bool deadLetter, ServiceBusReceiveMode mode)
    {
        var options = new ServiceBusReceiverOptions
        {
            ReceiveMode = mode,
            SubQueue = deadLetter ? SubQueue.DeadLetter : SubQueue.None,
            PrefetchCount = 0,
        };

        return entity.Kind == EntityKind.Subscription
            ? client.CreateReceiver(entity.TopicName, entity.Name, options)
            : client.CreateReceiver(entity.Name, options);
    }
}
