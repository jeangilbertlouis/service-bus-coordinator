using Azure.Messaging.ServiceBus;

namespace ServiceBusCoordinator.Core.Messages;

/// <summary>Progress of a long-running bulk operation.</summary>
public sealed record OperationProgress(long Processed, string? Note = null);

/// <summary>
/// Extra back-off for bulk operations. The SDK already retries transient failures; this layer waits longer
/// when the namespace is still throttling after those retries, instead of failing a long purge or resubmit.
/// </summary>
public static class Throttling
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    public static ServiceBusRetryOptions DefaultRetryOptions() => new()
    {
        Mode = ServiceBusRetryMode.Exponential,
        MaxRetries = 6,
        Delay = TimeSpan.FromSeconds(0.8),
        MaxDelay = TimeSpan.FromSeconds(30),
        TryTimeout = TimeSpan.FromSeconds(60),
    };

    public static bool IsThrottled(Exception ex) => ex is ServiceBusException { Reason: ServiceBusFailureReason.ServiceBusy };

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<TimeSpan>? onWait,
        CancellationToken ct,
        int maxAttempts = 8,
        TimeSpan? baseDelay = null)
    {
        var delay = baseDelay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (IsThrottled(ex) && attempt < maxAttempts)
            {
                var wait = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxWait.TotalMilliseconds));
                onWait?.Invoke(wait);
                await Task.Delay(wait, ct);
            }
        }
    }

    public static Task RunAsync(Func<CancellationToken, Task> operation, Action<TimeSpan>? onWait, CancellationToken ct) =>
        RunAsync(async c =>
        {
            await operation(c);
            return true;
        }, onWait, ct);
}
