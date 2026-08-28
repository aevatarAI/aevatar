using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Delivery;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeRetryPolicy
{
    private const int DefaultMaxAttempts = 3;
    private const int DefaultRetryDelayMs = 1000;
    internal const int RetryUntilResolvedInitialDelayMs = 5000;
    internal const int RetryUntilResolvedMaximumDelayMs = 30000;
    private const int RetryUntilResolvedJitterDivisor = 5;

    private RuntimeEnvelopeRetryPolicy(
        int maxAttempts,
        int retryDelayMs,
        bool retryOnlyRecoverableConcurrencyFailures)
    {
        MaxAttempts = maxAttempts;
        RetryDelayMs = retryDelayMs;
        RetryOnlyRecoverableConcurrencyFailures = retryOnlyRecoverableConcurrencyFailures;
    }

    public int MaxAttempts { get; }
    public int RetryDelayMs { get; }
    public bool Enabled => MaxAttempts > 0;
    public bool RetryOnlyRecoverableConcurrencyFailures { get; }

    public static RuntimeEnvelopeRetryPolicy Disabled { get; } = new(0, 0, true);

    internal int ResolveRetryDelayMs(
        int nextAttempt,
        bool retryUntilResolved,
        string? stableJitterIdentity = null)
    {
        if (!retryUntilResolved)
            return RetryDelayMs;

        var exponent = Math.Clamp(nextAttempt - 1, 0, 30);
        var multiplier = 1L << exponent;
        var nominalDelayMs = checked((int)Math.Min(
            RetryUntilResolvedInitialDelayMs * multiplier,
            RetryUntilResolvedMaximumDelayMs));
        if (string.IsNullOrWhiteSpace(stableJitterIdentity))
            return nominalDelayMs;

        return ResolveStableJitteredDelayMs(
            nominalDelayMs,
            nextAttempt,
            stableJitterIdentity);
    }

    private static int ResolveStableJitteredDelayMs(
        int nominalDelayMs,
        int nextAttempt,
        string stableJitterIdentity)
    {
        int minimumDelayMs;
        int maximumDelayMs;
        if (nominalDelayMs < RetryUntilResolvedMaximumDelayMs)
        {
            minimumDelayMs = nominalDelayMs;
            maximumDelayMs = Math.Min(
                RetryUntilResolvedMaximumDelayMs,
                nominalDelayMs + Math.Max(
                    nominalDelayMs / RetryUntilResolvedJitterDivisor,
                    1));
        }
        else
        {
            maximumDelayMs = RetryUntilResolvedMaximumDelayMs;
            minimumDelayMs = Math.Max(
                RetryUntilResolvedInitialDelayMs,
                maximumDelayMs - Math.Max(
                    maximumDelayMs / RetryUntilResolvedJitterDivisor,
                    1));
        }

        var width = checked(maximumDelayMs - minimumDelayMs + 1);
        var offset = (int)(ComputeStableJitterSeed(stableJitterIdentity, nextAttempt) % (uint)width);
        return checked(minimumDelayMs + offset);
    }

    private static uint ComputeStableJitterSeed(string stableJitterIdentity, int nextAttempt)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        unchecked
        {
            var hash = offsetBasis;
            foreach (var character in stableJitterIdentity)
            {
                hash ^= character;
                hash *= prime;
            }

            var attempt = (uint)nextAttempt;
            for (var byteIndex = 0; byteIndex < sizeof(int); byteIndex++)
            {
                hash ^= (byte)(attempt >> (byteIndex * 8));
                hash *= prime;
            }

            return hash;
        }
    }

    public static RuntimeEnvelopeRetryPolicy FromEnvironment()
    {
        var maxAttemptsRaw = Environment.GetEnvironmentVariable("AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS");
        var retryDelayRaw = Environment.GetEnvironmentVariable("AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS");
        return FromValues(maxAttemptsRaw, retryDelayRaw);
    }

    internal static RuntimeEnvelopeRetryPolicy FromValues(string? maxAttemptsRaw, string? retryDelayRaw)
    {
        // Refactor (iter96/cluster-529):
        //   Old pattern: AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS 未配置 → 默认关闭重试;显式配置后重试所有异常
        //   New principle: 默认开启 OCC-classified retry(仅重试 EventStoreOptimisticConcurrencyException);显式配置 max attempts 仍保留通用重试语义
        var maxAttemptsConfigured = int.TryParse(maxAttemptsRaw, out var configuredMaxAttempts);
        var maxAttempts = maxAttemptsConfigured ? configuredMaxAttempts : DefaultMaxAttempts;
        var retryDelayMs = ParseOrDefault(retryDelayRaw, defaultValue: DefaultRetryDelayMs);
        if (maxAttempts < 0)
            maxAttempts = 0;
        if (retryDelayMs < 0)
            retryDelayMs = 0;

        return maxAttempts == 0
            ? Disabled
            : new RuntimeEnvelopeRetryPolicy(
                maxAttempts,
                retryDelayMs,
                retryOnlyRecoverableConcurrencyFailures: !maxAttemptsConfigured);
    }

    public bool TryBuildRetryEnvelope(
        EventEnvelope originalEnvelope,
        Exception exception,
        out EventEnvelope retryEnvelope,
        out int nextAttempt)
    {
        nextAttempt = GetAttempt(originalEnvelope) + 1;
        var retryUntilResolved = ContainsRuntimeEnvelopeRetryUntilResolvedFailure(exception);
        if (!retryUntilResolved &&
            (!Enabled ||
             nextAttempt > MaxAttempts ||
             !ShouldRetry(exception)))
        {
            retryEnvelope = null!;
            return false;
        }

        retryEnvelope = originalEnvelope.Clone();
        var retry = retryEnvelope.EnsureRuntime().EnsureRetry();
        retry.Attempt = nextAttempt;
        retry.LastErrorType = exception.GetType().Name;
        var originEventId = ResolveOriginEventId(originalEnvelope);
        if (!string.IsNullOrWhiteSpace(originEventId))
            retry.OriginEventId = originEventId;
        return true;
    }

    private bool ShouldRetry(Exception exception) =>
        !RetryOnlyRecoverableConcurrencyFailures || ContainsDefaultRetryableFailure(exception);

    private static bool ContainsDefaultRetryableFailure(Exception exception)
    {
        return exception switch
        {
            IRuntimeEnvelopeRetryableException => true,
            EventStoreOptimisticConcurrencyException => true,
            CommittedStatePublicationException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsDefaultRetryableFailure),
            _ when exception.InnerException is not null =>
                ContainsDefaultRetryableFailure(exception.InnerException),
            _ => false,
        };
    }

    /// <summary>
    /// True when the failure chain indicates the actor's in-memory state and the
    /// event store disagree about the committed history (append conflict, version
    /// drift, or a committed-state publication that could not be persisted).
    /// After such a failure the activation's memory is not trustworthy: keeping
    /// the actor alive lets it consume and drop every subsequent command while
    /// callers see accepted results. The grain should shed the activation so the
    /// next envelope rehydrates from the committed history.
    /// </summary>
    internal static bool ContainsCommitConsistencyFailure(Exception exception)
    {
        return exception switch
        {
            EventStoreOptimisticConcurrencyException => true,
            EventStoreVersionDriftException => true,
            CommittedStatePublicationException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsCommitConsistencyFailure),
            _ when exception.InnerException is not null =>
                ContainsCommitConsistencyFailure(exception.InnerException),
            _ => false,
        };
    }

    /// <summary>
    /// True when the handler explicitly requires the same envelope to remain
    /// unacknowledged after runtime retries are exhausted.
    /// </summary>
    internal static bool ContainsRuntimeEnvelopeRetryableFailure(Exception exception)
    {
        return exception switch
        {
            IRuntimeEnvelopeRetryableException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsRuntimeEnvelopeRetryableFailure),
            _ when exception.InnerException is not null =>
                ContainsRuntimeEnvelopeRetryableFailure(exception.InnerException),
            _ => false,
        };
    }

    /// <summary>
    /// True when the handler is waiting on a transient gate that must be retried from an
    /// actor-owned durable continuation even after the ordinary attempt budget is exhausted.
    /// </summary>
    internal static bool ContainsRuntimeEnvelopeRetryUntilResolvedFailure(Exception exception)
    {
        return exception switch
        {
            IRuntimeEnvelopeRetryUntilResolvedException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsRuntimeEnvelopeRetryUntilResolvedFailure),
            _ when exception.InnerException is not null =>
                ContainsRuntimeEnvelopeRetryUntilResolvedFailure(exception.InnerException),
            _ => false,
        };
    }

    internal static RuntimeEnvelopeRetryCoalescingCursor? ResolveRetryCoalescingCursor(
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RuntimeEnvelopeRetryCoalescingCursor? resolved = null;
        Visit(exception);
        return resolved;

        void Visit(Exception candidate)
        {
            if (candidate is IRuntimeEnvelopeRetryCoalescingException coalescing)
            {
                var cursor = coalescing.RetryCoalescingCursor ??
                    throw new InvalidOperationException(
                        "Runtime retry coalescing exception returned no authoritative cursor.");
                if (resolved != null && resolved != cursor)
                {
                    throw new InvalidOperationException(
                        "One envelope failure cannot coalesce retries for multiple authoritative cursors.");
                }

                resolved = cursor;
            }

            if (candidate is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    Visit(inner);
                return;
            }

            if (candidate.InnerException != null)
                Visit(candidate.InnerException);
        }
    }

    internal static RuntimeEnvelopeRetryLogDisposition ResolveRetryLogDisposition(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (attempt == 1)
            return RuntimeEnvelopeRetryLogDisposition.WarningWithException;

        if (attempt <= 4 || (attempt & (attempt - 1)) == 0)
            return RuntimeEnvelopeRetryLogDisposition.Warning;

        return RuntimeEnvelopeRetryLogDisposition.Debug;
    }

    private static int GetAttempt(EventEnvelope envelope)
    {
        return RuntimeEnvelopeDeliveryIdentity.GetAttempt(envelope);
    }

    private static int ParseOrDefault(string? value, int defaultValue)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static string? ResolveOriginEventId(EventEnvelope envelope)
    {
        return RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(envelope);
    }
}

internal enum RuntimeEnvelopeRetryLogDisposition
{
    WarningWithException = 0,
    Warning = 1,
    Debug = 2,
}
