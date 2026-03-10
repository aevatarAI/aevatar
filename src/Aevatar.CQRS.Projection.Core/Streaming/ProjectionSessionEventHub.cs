using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Streaming;

/// <summary>
/// Generic stream-backed session event hub keyed by scope/session.
/// </summary>
public sealed class ProjectionSessionEventHub<TEvent> : IProjectionSessionEventHub<TEvent>
{
    private readonly IStreamProvider _streamProvider;
    private readonly IProjectionSessionEventCodec<TEvent> _codec;
    private readonly ILogger<ProjectionSessionEventHub<TEvent>> _logger;

    public ProjectionSessionEventHub(
        IStreamProvider streamProvider,
        IProjectionSessionEventCodec<TEvent> codec,
        ILogger<ProjectionSessionEventHub<TEvent>>? logger = null)
    {
        _streamProvider = streamProvider;
        _codec = codec;
        _logger = logger ?? NullLogger<ProjectionSessionEventHub<TEvent>>.Instance;
    }

    public Task PublishAsync(
        string scopeId,
        string sessionId,
        TEvent evt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("Scope id is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(_codec.Channel))
            throw new InvalidOperationException("Session event codec channel is required.");
        ArgumentNullException.ThrowIfNull(evt);

        var stream = _streamProvider.GetStream(ResolveStreamId(scopeId, sessionId));
        var message = new ProjectionSessionEventTransportMessage
        {
            ScopeId = scopeId,
            SessionId = sessionId,
            EventType = _codec.GetEventType(evt),
            Payload = _codec.Serialize(evt),
        };
        return stream.ProduceAsync(message, ct);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string scopeId,
        string sessionId,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("Scope id is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(_codec.Channel))
            throw new InvalidOperationException("Session event codec channel is required.");
        ArgumentNullException.ThrowIfNull(handler);

        var stream = _streamProvider.GetStream(ResolveStreamId(scopeId, sessionId));
        return await stream.SubscribeAsync<ProjectionSessionEventTransportMessage>(async message =>
        {
            if (!string.Equals(message.ScopeId, scopeId, StringComparison.Ordinal) ||
                !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (message.Payload == null || message.Payload.IsEmpty)
            {
                _logger.LogWarning(
                    "Dropping projection session event with empty payload. channel={Channel} scopeId={ScopeId} sessionId={SessionId} eventType={EventType}",
                    _codec.Channel,
                    scopeId,
                    sessionId,
                    message.EventType);
                return;
            }

            var evt = _codec.Deserialize(message.EventType, message.Payload);
            if (evt == null)
            {
                _logger.LogWarning(
                    "Dropping undecodable projection session event. channel={Channel} scopeId={ScopeId} sessionId={SessionId} eventType={EventType} payloadBytes={PayloadBytes}",
                    _codec.Channel,
                    scopeId,
                    sessionId,
                    message.EventType,
                    message.Payload.Length);
                return;
            }

            await handler(evt);
        }, ct);
    }

    private string ResolveStreamId(string scopeId, string sessionId) =>
        $"{_codec.Channel}:{scopeId.Trim()}:{sessionId.Trim()}";
}
