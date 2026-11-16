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

public sealed record PeekResult(IReadOnlyList<MessageView> Messages, string? SessionId);

/// <summary>Peek, purge, resubmit, scheduled/deferred handling and session tools for one namespace.</summary>
public sealed class MessageOperations
{
    private const int PurgeBatchSize = 500;
    private const int ReceiveBatchSize = 100;
    private const int SweepPageSize = 250;
    private const int DeferredBatchSize = 100;
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for "next available session" before concluding there is none.</summary>
    public static TimeSpan SessionAcceptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly NamespaceConnection _connection;

    internal MessageOperations(NamespaceConnection connection) => _connection = connection;

    private ServiceBusClient Client => _connection.Client;

    // ----------------------------------------------------------------- Peek

    /// <summary>
    /// Non-destructive peek. For the active messages of a session-enabled entity, <paramref name="sessionId"/> picks
    /// the session; when null the next available session is used and returned in the result.
    /// </summary>
    public async Task<PeekResult> PeekAsync(
        EntityInfo entity, MessageSource source, long? fromSequenceNumber, int maxMessages, string? sessionId = null, CancellationToken ct = default)
    {
        EnsureHasMessages(entity);

        if (NeedsSessionReceiver(entity, source))
        {
            await using var sessionReceiver = string.IsNullOrEmpty(sessionId)
                ? await TryAcceptNextSessionAsync(entity, ServiceBusReceiveMode.PeekLock, ct)
                : await AcceptSessionAsync(entity, sessionId, ServiceBusReceiveMode.PeekLock, ct);
            if (sessionReceiver is null)
            {
                return new PeekResult([], null);
            }

            var sessionMessages = await sessionReceiver.PeekMessagesAsync(maxMessages, fromSequenceNumber, ct);
            return new PeekResult(sessionMessages.Select(MessageView.From).ToList(), sessionReceiver.SessionId);
        }

        await using var receiver = CreateReceiver(entity, source, ServiceBusReceiveMode.PeekLock);
        var messages = await receiver.PeekMessagesAsync(maxMessages, fromSequenceNumber, ct);
        return new PeekResult(messages.Select(MessageView.From).ToList(), null);
    }

    /// <summary>Peeks a whole sub-queue (or one session) page by page, up to <paramref name="maxMessages"/>. Used for export.</summary>
    public async Task<IReadOnlyList<MessageView>> PeekAllAsync(
        EntityInfo entity, MessageSource source, string? sessionId, int maxMessages, IProgress<OperationProgress>? progress, CancellationToken ct = default)
    {
        var all = new List<MessageView>();
        long? from = null;
        while (all.Count < maxMessages)
        {
            var page = await Throttling.RunAsync(
                c => PeekAsync(entity, source, from, Math.Min(SweepPageSize, maxMessages - all.Count), sessionId, c), null, ct);
            if (page.Messages.Count == 0)
            {
                break;
            }

            // Pin the session after the first page so "next available" doesn't hop between sessions.
            sessionId ??= page.SessionId;
            all.AddRange(page.Messages);
            from = page.Messages[^1].SequenceNumber + 1;
            progress?.Report(new OperationProgress(all.Count));
        }

        return all;
    }

    // ----------------------------------------------------------------- Send

    /// <summary>Sends (or schedules) a message. Returns the sequence number when scheduled, otherwise null.</summary>
    public async Task<long?> SendAsync(string entityPath, OutgoingMessage outgoing, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityPath))
        {
            throw new ArgumentException("A target entity is required.", nameof(entityPath));
        }

        await using var sender = Client.CreateSender(entityPath);
        var message = outgoing.ToServiceBusMessage();
        if (outgoing.ScheduledEnqueueTime is { } at && at > DateTimeOffset.UtcNow)
        {
            return await sender.ScheduleMessageAsync(message, at, ct);
        }

        await sender.SendMessageAsync(message, ct);
        return null;
    }

    // ----------------------------------------------------------------- Sessions

    /// <summary>
    /// Finds sessions that currently have messages by briefly locking each one (consumers are blocked from those
    /// sessions for the few seconds this takes). Service Bus has no API to list sessions without locking them.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(EntityInfo entity, int maxSessions = 100, CancellationToken ct = default)
    {
        EnsureHasMessages(entity);
        var held = new List<ServiceBusSessionReceiver>();
        var result = new List<SessionSummary>();
        try
        {
            while (held.Count < maxSessions)
            {
                var receiver = await TryAcceptNextSessionAsync(entity, ServiceBusReceiveMode.PeekLock, ct);
                if (receiver is null)
                {
                    break;
                }

                held.Add(receiver);
                var state = await receiver.GetSessionStateAsync(ct);
                result.Add(new SessionSummary(receiver.SessionId, state?.ToMemory().Length ?? 0));
            }
        }
        finally
        {
            foreach (var receiver in held)
            {
                await receiver.DisposeAsync();
            }
        }

        return result.OrderBy(s => s.SessionId, StringComparer.Ordinal).ToList();
    }

    public async Task<byte[]?> GetSessionStateAsync(EntityInfo entity, string sessionId, CancellationToken ct = default)
    {
        await using var receiver = await AcceptSessionAsync(entity, sessionId, ServiceBusReceiveMode.PeekLock, ct);
        var state = await receiver.GetSessionStateAsync(ct);
        return state?.ToArray();
    }

    public async Task ClearSessionStateAsync(EntityInfo entity, string sessionId, CancellationToken ct = default)
    {
        await using var receiver = await AcceptSessionAsync(entity, sessionId, ServiceBusReceiveMode.PeekLock, ct);
        await receiver.SetSessionStateAsync(null, ct);
    }

    // ----------------------------------------------------------------- Purge

    /// <summary>
    /// Deletes every message enqueued before the purge started, including deferred messages and (for queues) scheduled
    /// messages. Session-enabled entities are purged session by session. Returns the number of messages removed.
    /// </summary>
    public async Task<long> PurgeAsync(
        EntityInfo entity, MessageSource source, IProgress<OperationProgress>? progress, CancellationToken ct = default)
    {
        EnsureHasMessages(entity);
        var cutoff = DateTimeOffset.UtcNow;
        var counter = new Counter(progress);

        if (NeedsSessionReceiver(entity, source))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                await using var sessionReceiver = await TryAcceptNextSessionAsync(entity, ServiceBusReceiveMode.ReceiveAndDelete, ct);
                if (sessionReceiver is null || !visited.Add(sessionReceiver.SessionId))
                {
                    break;
                }

                counter.Note = $"session '{sessionReceiver.SessionId}'";
                await DrainAsync(sessionReceiver, cutoff, counter, ct);
                await SweepDeferredAndScheduledAsync(entity, source, sessionReceiver, cutoff, counter, ct);
            }

            // Note: sessions holding only scheduled or deferred messages are never offered by AcceptNextSession,
            // so those messages can't be reached without knowing the session id.
            return counter.Total;
        }

        await using var receiver = CreateReceiver(entity, source, ServiceBusReceiveMode.ReceiveAndDelete);
        await DrainAsync(receiver, cutoff, counter, ct);
        await SweepDeferredAndScheduledAsync(entity, source, receiver, cutoff, counter, ct);
        return counter.Total;
    }

    /// <summary>
    /// Removes messages with a ReceiveAndDelete receiver until empty or until it reaches messages that arrived after
    /// the purge started (so a busy entity can't keep it looping). The SDK's server-side batch delete is still internal.
    /// </summary>
    private static async Task DrainAsync(ServiceBusReceiver receiver, DateTimeOffset cutoff, Counter counter, CancellationToken ct)
    {
        while (true)
        {
            var batch = await Throttling.RunAsync(
                c => receiver.ReceiveMessagesAsync(PurgeBatchSize, ReceiveWait, c), counter.Throttled, ct);
            if (batch.Count == 0)
            {
                return;
            }

            counter.Add(batch.Count);
            if (batch.Any(m => m.EnqueuedTime > cutoff))
            {
                return;
            }
        }
    }

    /// <summary>Deferred messages can't be received normally, and scheduled ones aren't visible yet; find them by peeking.</summary>
    private async Task SweepDeferredAndScheduledAsync(
        EntityInfo entity, MessageSource source, ServiceBusReceiver receiveAndDeleteReceiver, DateTimeOffset cutoff, Counter counter, CancellationToken ct)
    {
        var (deferred, scheduled) = await FindDeferredAndScheduledAsync(receiveAndDeleteReceiver, cutoff, ct);

        foreach (var chunk in deferred.Chunk(DeferredBatchSize))
        {
            var removed = await Throttling.RunAsync(
                c => receiveAndDeleteReceiver.ReceiveDeferredMessagesAsync(chunk, c), counter.Throttled, ct);
            counter.Add(removed.Count);
        }

        if (scheduled.Count > 0 && entity.Kind == EntityKind.Queue && source == MessageSource.Active)
        {
            await CancelScheduledInternalAsync(entity, scheduled, counter, ct);
        }
    }

    private static async Task<(List<long> Deferred, List<long> Scheduled)> FindDeferredAndScheduledAsync(
        ServiceBusReceiver receiver, DateTimeOffset cutoff, CancellationToken ct)
    {
        var deferred = new List<long>();
        var scheduled = new List<long>();
        long? from = null;
        while (true)
        {
            var page = await receiver.PeekMessagesAsync(SweepPageSize, from, ct);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var m in page.Where(m => m.EnqueuedTime <= cutoff))
            {
                if (m.State == ServiceBusMessageState.Deferred)
                {
                    deferred.Add(m.SequenceNumber);
                }
                else if (m.State == ServiceBusMessageState.Scheduled)
                {
                    scheduled.Add(m.SequenceNumber);
                }
            }

            from = page[^1].SequenceNumber + 1;
        }

        return (deferred, scheduled);
    }

    // ----------------------------------------------------------------- Scheduled & deferred

    /// <summary>Cancels scheduled messages on a queue. (Topic schedules live on the topic and can't be peeked.)</summary>
    public async Task<int> CancelScheduledAsync(EntityInfo entity, IReadOnlyCollection<long> sequenceNumbers, CancellationToken ct = default)
    {
        if (entity.Kind != EntityKind.Queue)
        {
            throw new NotSupportedException("Scheduled messages can only be cancelled on queues.");
        }

        var counter = new Counter(null);
        await CancelScheduledInternalAsync(entity, sequenceNumbers, counter, ct);
        return (int)counter.Total;
    }

    private async Task CancelScheduledInternalAsync(EntityInfo entity, IEnumerable<long> sequenceNumbers, Counter counter, CancellationToken ct)
    {
        await using var sender = Client.CreateSender(entity.Name);
        foreach (var chunk in sequenceNumbers.Chunk(DeferredBatchSize))
        {
            await Throttling.RunAsync(c => sender.CancelScheduledMessagesAsync(chunk, c), counter.Throttled, ct);
            counter.Add(chunk.Length);
        }
    }

    /// <summary>Deletes deferred messages by sequence number. Session entities need the session id.</summary>
    public async Task<int> DeleteDeferredAsync(
        EntityInfo entity, MessageSource source, IReadOnlyCollection<long> sequenceNumbers, string? sessionId = null, CancellationToken ct = default)
    {
        EnsureHasMessages(entity);
        if (NeedsSessionReceiver(entity, source))
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new InvalidOperationException("Open a session first: deferred messages in a session can only be reached through it.");
            }

            await using var sessionReceiver = await AcceptSessionAsync(entity, sessionId, ServiceBusReceiveMode.ReceiveAndDelete, ct);
            return (await sessionReceiver.ReceiveDeferredMessagesAsync(sequenceNumbers, ct)).Count;
        }

        await using var receiver = CreateReceiver(entity, source, ServiceBusReceiveMode.ReceiveAndDelete);
        return (await receiver.ReceiveDeferredMessagesAsync(sequenceNumbers, ct)).Count;
    }

    // ----------------------------------------------------------------- Resubmit

    /// <summary>
    /// Moves dead-lettered (or transfer dead-lettered) messages to <see cref="ResubmitOptions.TargetEntityPath"/>.
    /// Each send + complete runs in a cross-entity transaction when the service allows it; otherwise it sends then
    /// completes (at-least-once).
    /// </summary>
    public async Task<ResubmitResult> ResubmitAsync(
        EntityInfo source, MessageSource subQueue, ResubmitOptions options, IProgress<OperationProgress>? progress, CancellationToken ct = default)
    {
        EnsureHasMessages(source);
        if (!subQueue.IsDeadLetter())
        {
            throw new InvalidOperationException("Only dead-letter and transfer dead-letter messages can be resubmitted.");
        }

        if (string.IsNullOrWhiteSpace(options.TargetEntityPath))
        {
            throw new ArgumentException("A target entity is required.", nameof(options));
        }

        var errors = new List<string>();
        var held = new Dictionary<long, ServiceBusReceivedMessage>();
        var selection = options.SequenceNumbers;
        var sourcePath = source.Path + subQueue.PathSuffix();
        var counter = new Counter(progress);
        int done = 0, failed = 0;
        var useTransactions = true;

        await using var txClient = new ServiceBusClient(
            _connection.Profile.ConnectionString,
            new ServiceBusClientOptions { EnableCrossEntityTransactions = true, RetryOptions = Throttling.DefaultRetryOptions() });

        // With cross-entity transactions the first operation fixes the "via" entity, so receive comes first.
        var receiver = CreateReceiver(txClient, source, subQueue, ServiceBusReceiveMode.PeekLock);
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

                var batch = await Throttling.RunAsync(
                    c => receiver.ReceiveMessagesAsync(ReceiveBatchSize, ReceiveWait, c), counter.Throttled, ct);
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
                        message, options.StripDeadLetterProperties, options.StampResubmitHeaders, sourcePath, DateTimeOffset.UtcNow);

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
                            plainSender ??= Client.CreateSender(options.TargetEntityPath);
                            await Throttling.RunAsync(c => plainSender.SendMessageAsync(outgoing, c), counter.Throttled, ct);
                            await receiver.CompleteMessageAsync(message, ct);
                        }

                        done++;
                        counter.Add(1);
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

    // ----------------------------------------------------------------- Helpers

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

    private static void EnsureHasMessages(EntityInfo entity)
    {
        if (!entity.HasMessages)
        {
            throw new InvalidOperationException("Topics do not hold messages. Select one of its subscriptions.");
        }
    }

    /// <summary>Dead-letter sub-queues never require sessions, even on session-enabled entities.</summary>
    internal static bool NeedsSessionReceiver(EntityInfo entity, MessageSource source) =>
        entity.RequiresSession && source == MessageSource.Active;

    private ServiceBusReceiver CreateReceiver(EntityInfo entity, MessageSource source, ServiceBusReceiveMode mode) =>
        CreateReceiver(Client, entity, source, mode);

    private static ServiceBusReceiver CreateReceiver(ServiceBusClient client, EntityInfo entity, MessageSource source, ServiceBusReceiveMode mode)
    {
        var options = new ServiceBusReceiverOptions
        {
            ReceiveMode = mode,
            SubQueue = source.ToSubQueue(),
            PrefetchCount = 0,
        };

        return entity.Kind == EntityKind.Subscription
            ? client.CreateReceiver(entity.TopicName, entity.Name, options)
            : client.CreateReceiver(entity.Name, options);
    }

    private async Task<ServiceBusSessionReceiver> AcceptSessionAsync(
        EntityInfo entity, string sessionId, ServiceBusReceiveMode mode, CancellationToken ct)
    {
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = mode, PrefetchCount = 0 };
        return entity.Kind == EntityKind.Subscription
            ? await Client.AcceptSessionAsync(entity.TopicName, entity.Name, sessionId, options, ct)
            : await Client.AcceptSessionAsync(entity.Name, sessionId, options, ct);
    }

    private async Task<ServiceBusSessionReceiver?> TryAcceptNextSessionAsync(EntityInfo entity, ServiceBusReceiveMode mode, CancellationToken ct)
    {
        var options = new ServiceBusSessionReceiverOptions { ReceiveMode = mode, PrefetchCount = 0 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SessionAcceptTimeout);
        try
        {
            return entity.Kind == EntityKind.Subscription
                ? await Client.AcceptNextSessionAsync(entity.TopicName, entity.Name, options, timeout.Token)
                : await Client.AcceptNextSessionAsync(entity.Name, options, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            return null;
        }
    }

    /// <summary>Running total plus throttle notes, reported through one progress sink.</summary>
    private sealed class Counter(IProgress<OperationProgress>? progress)
    {
        public long Total { get; private set; }

        public string? Note { get; set; }

        public void Add(long count)
        {
            Total += count;
            progress?.Report(new OperationProgress(Total, Note));
        }

        public void Throttled(TimeSpan wait) =>
            progress?.Report(new OperationProgress(Total, $"namespace is throttling — waiting {wait.TotalSeconds:N0}s"));
    }
}
