using System.Globalization;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeRetryCoordinator
{
    private const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

    private readonly Func<string> _actorIdAccessor;
    private readonly RuntimeEnvelopeRetryPolicy _retryPolicy;
    private readonly ILogger _logger;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;

    public RuntimeEnvelopeRetryCoordinator(
        Func<string> actorIdAccessor,
        RuntimeEnvelopeRetryPolicy retryPolicy,
        ILogger logger,
        Aevatar.Foundation.Abstractions.IStreamProvider streams,
        IActorRuntimeCallbackScheduler callbackScheduler)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _retryPolicy = retryPolicy;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _callbackScheduler = callbackScheduler ?? throw new ArgumentNullException(nameof(callbackScheduler));
    }

    public async Task<bool> TryScheduleRetryAsync(EventEnvelope envelope, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(ex);

        if (!_retryPolicy.TryBuildRetryEnvelope(
                envelope,
                ex,
                out var retryEnvelope,
                out var nextAttempt))
        {
            return false;
        }

        if (_retryPolicy.RetryDelayMs > 0)
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = _actorIdAccessor(),
                    CallbackId = BuildRuntimeRetryCallbackId(envelope, nextAttempt),
                    DueTime = TimeSpan.FromMilliseconds(_retryPolicy.RetryDelayMs),
                    TriggerEnvelope = retryEnvelope,
                });
        }
        else
        {
            await _streams.GetStream(_actorIdAccessor()).ProduceAsync(retryEnvelope);
        }

        _logger.LogWarning(
            ex,
            "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}.",
            _actorIdAccessor(),
            nextAttempt,
            _retryPolicy.MaxAttempts);
        return true;
    }

    private string BuildRuntimeRetryCallbackId(EventEnvelope envelope, int nextAttempt)
    {
        var originId = envelope.Metadata.TryGetValue(RetryOriginEventIdMetadataKey, out var metadataOriginId) &&
                       !string.IsNullOrWhiteSpace(metadataOriginId)
            ? metadataOriginId
            : envelope.Id;

        if (string.IsNullOrWhiteSpace(originId))
            originId = envelope.Id ?? Guid.NewGuid().ToString("N");

        return RuntimeCallbackKeyComposer.BuildCallbackId(
            "runtime-envelope-retry",
            originId,
            nextAttempt.ToString(CultureInfo.InvariantCulture));
    }
}
