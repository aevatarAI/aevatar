using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Sends a single accumulated reply snapshot to the actor that owns the visible reply delivery.
/// The actor/run flow decides throttling, pending text, and final ordering before calling this sink.
/// </summary>
/// <remarks>
/// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
///   Old pattern: timer callback directly inspects/mutates pending business output and dispatches actor command from callback thread
///   New principle: actor-owned run flow keeps pending text and invokes this sink only for the snapshot it has decided to publish.
/// Refactor (iter20/cluster-004):
///   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
///   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
/// Refactor (iter149/issue1132): Old pattern: streaming reply sink waited on handled-dispatch for chunk delivery.  New principle: sink emits accepted-only chunk commands; conversation actor state/projection own completion visibility.
/// </remarks>
public sealed class TurnStreamingReplySink : IStreamingReplySink, IDisposable
{
    private const string PublisherActorId = "channel-runtime.streaming-reply";

    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly string _targetActorId;
    private readonly string _correlationId;
    private readonly string _registrationId;
    private readonly ChatActivity _activityTemplate;
    private readonly string _replyToken;
    private readonly long _replyTokenExpiresAtUnixMs;
    private readonly string _runId;
    private readonly bool _cardMode;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private bool _disposed;

    public TurnStreamingReplySink(
        IActorDispatchPort actorDispatchPort,
        string targetActorId,
        string correlationId,
        string registrationId,
        ChatActivity activityTemplate,
        string? replyToken,
        long replyTokenExpiresAtUnixMs,
        string? runId,
        TimeProvider timeProvider,
        ILogger? logger = null,
        bool cardMode = false)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        if (string.IsNullOrWhiteSpace(targetActorId))
            throw new ArgumentException("Target actor id is required.", nameof(targetActorId));
        _targetActorId = targetActorId.Trim();
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        _correlationId = correlationId.Trim();
        _registrationId = registrationId ?? string.Empty;
        _activityTemplate = activityTemplate ?? throw new ArgumentNullException(nameof(activityTemplate));
        _replyToken = replyToken ?? string.Empty;
        _replyTokenExpiresAtUnixMs = replyTokenExpiresAtUnixMs;
        _runId = runId ?? string.Empty;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
        _cardMode = cardMode;
    }

    public int ChunksEmitted { get; private set; }

    public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
        DispatchAsync(accumulatedText, false, ct);

    public Task FinalizeAsync(string finalText, CancellationToken ct) =>
        DispatchAsync(finalText, true, ct);

    public void Dispose() => _disposed = true;

    public async Task DispatchAsync(string text, bool isFinal, CancellationToken ct)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
            return;

        // Card mode dispatches a structurally distinct message type so persistence layers
        // cannot silently re-route a replayed event back to the card sink. The two proto
        // types carry identical payloads; the type identity itself signals routing.
        IMessage chunk = _cardMode
            ? new LlmReplyCardStreamChunkEvent
            {
                CorrelationId = _correlationId,
                RegistrationId = _registrationId,
                Activity = _activityTemplate.Clone(),
                AccumulatedText = text,
                ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                ReplyToken = _replyToken,
                ReplyTokenExpiresAtUnixMs = _replyTokenExpiresAtUnixMs,
                RunId = _runId,
                IsFinal = isFinal,
            }
            : new LlmReplyStreamChunkEvent
            {
                CorrelationId = _correlationId,
                RegistrationId = _registrationId,
                Activity = _activityTemplate.Clone(),
                AccumulatedText = text,
                ChunkAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                ReplyToken = _replyToken,
                ReplyTokenExpiresAtUnixMs = _replyTokenExpiresAtUnixMs,
            };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(chunk),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, _targetActorId),
        };

        try
        {
            await _actorDispatchPort.DispatchAsync(_targetActorId, envelope, ct).ConfigureAwait(false);
            ChunksEmitted++;
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
