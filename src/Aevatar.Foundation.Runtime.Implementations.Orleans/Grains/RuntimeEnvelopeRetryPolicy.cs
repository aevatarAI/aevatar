using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Delivery;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeRetryPolicy
{
    private const int DefaultMaxAttempts = 3;
    private const int DefaultRetryDelayMs = 1000;

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
        if (!Enabled ||
            nextAttempt > MaxAttempts ||
            !ShouldRetry(exception))
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
        !RetryOnlyRecoverableConcurrencyFailures || ContainsRecoverableConcurrencyFailure(exception);

    private static bool ContainsRecoverableConcurrencyFailure(Exception exception)
    {
        return exception switch
        {
            EventStoreOptimisticConcurrencyException => true,
            CommittedStatePublicationException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsRecoverableConcurrencyFailure),
            _ when exception.InnerException is not null =>
                ContainsRecoverableConcurrencyFailure(exception.InnerException),
            _ => false,
        };
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
