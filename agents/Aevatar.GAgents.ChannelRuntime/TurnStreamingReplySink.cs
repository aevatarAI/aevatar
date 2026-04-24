using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Drives progressive (edit-in-place) rendering of an LLM reply for a single turn by dispatching
/// <see cref="LlmReplyStreamChunkEvent"/>s to the conversation actor that owns the relay reply
/// token. State is per-invocation (instance fields on the sink, never a service-level map) so
/// different turns run on different sink instances by construction.
/// </summary>
/// <remarks>
/// The sink is responsible only for accumulating and throttling deltas; placeholder send, edit
/// dispatch, and streaming disable/fallback decisions are all owned by the conversation actor.
/// Chunk dispatches are awaited so the actor observes chunks in the same order they arrived from
/// the LLM stream, matching the ordering invariant required by the edit-in-place protocol.
/// </remarks>
internal sealed class TurnStreamingReplySink : IStreamingReplySink
{
    private readonly IActor _targetActor;
    private readonly string _correlationId;
    private readonly string _registrationId;
    private readonly ChatActivity _activityTemplate;
    private readonly TimeSpan _throttle;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private string _lastEmittedText = string.Empty;
    private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
    private int _chunksEmitted;

    public TurnStreamingReplySink(
        IActor targetActor,
        string correlationId,
        string registrationId,
        ChatActivity activityTemplate,
        TimeSpan throttle,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _targetActor = targetActor ?? throw new ArgumentNullException(nameof(targetActor));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        _correlationId = correlationId.Trim();
        _registrationId = registrationId ?? string.Empty;
        _activityTemplate = activityTemplate ?? throw new ArgumentNullException(nameof(activityTemplate));
        _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    public int ChunksEmitted => _chunksEmitted;

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        FlushAsync(accumulatedText, isFinal: false, ct);

    /// <summary>
    /// Applies the final accumulated text, bypassing the throttle so the actor can drive the final
    /// edit once the stream ends.
    /// </summary>
    public Task FinalizeAsync(string finalText, CancellationToken ct) =>
        FlushAsync(finalText, isFinal: true, ct);

    private async Task FlushAsync(string text, bool isFinal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (string.Equals(text, _lastEmittedText, StringComparison.Ordinal))
            return;
        if (!isFinal && (_timeProvider.GetUtcNow() - _lastEmitAt) < _throttle)
            return;

        var chunk = new LlmReplyStreamChunkEvent
        {
            CorrelationId = _correlationId,
            RegistrationId = _registrationId,
            Activity = _activityTemplate.Clone(),
            AccumulatedText = text,
            ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(chunk),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = _targetActor.Id },
            },
        };

        try
        {
            await _targetActor.HandleEventAsync(envelope, ct);
            _lastEmittedText = text;
            _lastEmitAt = _timeProvider.GetUtcNow();
            _chunksEmitted++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to dispatch LLM reply stream chunk to conversation actor; dropping. correlationId={CorrelationId}",
                _correlationId);
        }
    }
}
