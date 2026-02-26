namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeRetryPolicy
{
    private const string RetryAttemptMetadataKey = "aevatar.retry.attempt";
    private const string RetryLastErrorMetadataKey = "aevatar.retry.last_error";
    private const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

    private RuntimeEnvelopeRetryPolicy(int maxAttempts, int retryDelayMs)
    {
        MaxAttempts = maxAttempts;
        RetryDelayMs = retryDelayMs;
    }

    public int MaxAttempts { get; }
    public int RetryDelayMs { get; }
    public bool Enabled => MaxAttempts > 0;

    public static RuntimeEnvelopeRetryPolicy Disabled { get; } = new(0, 0);

    public static RuntimeEnvelopeRetryPolicy FromEnvironment()
    {
        var maxAttemptsRaw = Environment.GetEnvironmentVariable("AEVATAR_RUNTIME_AUTO_RETRY_MAX_ATTEMPTS");
        var retryDelayRaw = Environment.GetEnvironmentVariable("AEVATAR_RUNTIME_AUTO_RETRY_DELAY_MS");
        return FromValues(maxAttemptsRaw, retryDelayRaw);
    }

    internal static RuntimeEnvelopeRetryPolicy FromValues(string? maxAttemptsRaw, string? retryDelayRaw)
    {
        var maxAttempts = ParseOrDefault(maxAttemptsRaw, defaultValue: 0);
        var retryDelayMs = ParseOrDefault(retryDelayRaw, defaultValue: 1000);
        if (maxAttempts < 0)
            maxAttempts = 0;
        if (retryDelayMs < 0)
            retryDelayMs = 0;

        return maxAttempts == 0 ? Disabled : new RuntimeEnvelopeRetryPolicy(maxAttempts, retryDelayMs);
    }

    public bool TryBuildRetryEnvelope(
        EventEnvelope originalEnvelope,
        Exception exception,
        out EventEnvelope retryEnvelope,
        out int nextAttempt)
    {
        nextAttempt = GetAttempt(originalEnvelope) + 1;
        if (!Enabled || nextAttempt > MaxAttempts)
        {
            retryEnvelope = null!;
            return false;
        }

        retryEnvelope = originalEnvelope.Clone();
        retryEnvelope.Metadata[RetryAttemptMetadataKey] = nextAttempt.ToString();
        retryEnvelope.Metadata[RetryLastErrorMetadataKey] = exception.GetType().Name;
        var originEventId = ResolveOriginEventId(originalEnvelope);
        if (!string.IsNullOrWhiteSpace(originEventId))
            retryEnvelope.Metadata[RetryOriginEventIdMetadataKey] = originEventId;
        return true;
    }

    private static int GetAttempt(EventEnvelope envelope)
    {
        if (!envelope.Metadata.TryGetValue(RetryAttemptMetadataKey, out var value))
            return 0;
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static int ParseOrDefault(string? value, int defaultValue)
    {
        if (int.TryParse(value, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static string? ResolveOriginEventId(EventEnvelope envelope)
    {
        if (envelope.Metadata.TryGetValue(RetryOriginEventIdMetadataKey, out var originEventId) &&
            !string.IsNullOrWhiteSpace(originEventId))
            return originEventId;
        return string.IsNullOrWhiteSpace(envelope.Id) ? null : envelope.Id;
    }
}
