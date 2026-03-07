using Aevatar.Foundation.Runtime.Actors;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeDedupGuard
{
    private const string RetryAttemptMetadataKey = "aevatar.retry.attempt";
    private const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

    private readonly Func<string> _actorIdAccessor;
    private readonly IEventDeduplicator? _deduplicator;

    public RuntimeEnvelopeDedupGuard(
        Func<string> actorIdAccessor,
        IEventDeduplicator? deduplicator)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _deduplicator = deduplicator;
    }

    public async Task<bool> ShouldDropAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (_deduplicator == null || string.IsNullOrWhiteSpace(envelope.Id))
            return false;

        var dedupKey = BuildDedupKey(envelope);
        return !await _deduplicator.TryRecordAsync(dedupKey);
    }

    private string BuildDedupKey(EventEnvelope envelope)
    {
        var originId = envelope.Metadata.TryGetValue(RetryOriginEventIdMetadataKey, out var metadataOriginId) &&
                       !string.IsNullOrWhiteSpace(metadataOriginId)
            ? metadataOriginId
            : envelope.Id;

        if (string.IsNullOrWhiteSpace(originId))
            originId = envelope.Id ?? string.Empty;

        var attempt = 0;
        if (envelope.Metadata.TryGetValue(RetryAttemptMetadataKey, out var metadataAttempt) &&
            int.TryParse(metadataAttempt, out var parsedAttempt) &&
            parsedAttempt > 0)
        {
            attempt = parsedAttempt;
        }

        return $"{_actorIdAccessor()}:{originId}:{attempt}";
    }
}
