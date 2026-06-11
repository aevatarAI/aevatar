using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Streaming;

/// <summary>
/// Generic stream-backed session event hub keyed by root actor/session.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: session hub called the first key scopeId.
// Refactor (iter367/cluster-issue377): Old pattern: transport tag 1 held RootActorId under a scope alias.
// Refactor (iter367/cluster-issue377): New principle: first key is RootActorId across hub, entry, and proto.
// Refactor (iter367/cluster-issue377): New principle: stream routing remains RootActorId + SessionId.
// Refactor (iter34/cluster-003-projection-session-legacy-payload):
//   Old pattern: Projection session event transport carries both protobuf bytes and legacy string payload compatibility (legacy_payload string field in proto + legacy codec interface + legacy payload write path).
//   New principle: Projection session event transport carries protobuf payload only; obsolete legacy codec surface is deleted; tests/docs updated; protobuf legacy_payload field reserved per protobuf evolution rules; no concrete codec depended on the legacy interface.
public sealed class ProjectionSessionEventHub<TEvent> : IProjectionSessionEventHub<TEvent>
    where TEvent : class
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

    // Refactor (iter367/cluster-issue377): Old pattern: PublishAsync accepted scopeId for root actor routing.
    // Refactor (iter367/cluster-issue377): Old pattern: message.ScopeId hid the true invariant in generated code.
    // Refactor (iter367/cluster-issue377): New principle: PublishAsync accepts rootActorId and writes root_actor_id.
    // Refactor (iter367/cluster-issue377): New principle: validation errors name rootActorId honestly.
    public Task PublishAsync(
        string rootActorId,
        string sessionId,
        TEvent evt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            throw new ArgumentException("Root actor id is required.", nameof(rootActorId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(_codec.Channel))
            throw new InvalidOperationException("Session event codec channel is required.");
        ArgumentNullException.ThrowIfNull(evt);

        var stream = _streamProvider.GetStream(ResolveStreamId(rootActorId, sessionId));
        var message = new ProjectionSessionEventTransportMessage
        {
            RootActorId = rootActorId,
            SessionId = sessionId,
            EventType = _codec.GetEventType(evt),
            Payload = _codec.Serialize(evt),
        };
        return stream.ProduceAsync(message, ct);
    }

    // Refactor (iter367/cluster-issue377): Old pattern: SubscribeAsync accepted scopeId for root actor routing.
    // Refactor (iter367/cluster-issue377): Old pattern: subscription filters compared message.ScopeId.
    // Refactor (iter367/cluster-issue377): New principle: subscription filters compare message.RootActorId.
    // Refactor (iter367/cluster-issue377): New principle: route key names match the typed projection context.
    public async Task<IAsyncDisposable> SubscribeAsync(
        string rootActorId,
        string sessionId,
        Func<TEvent, ValueTask> handler,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            throw new ArgumentException("Root actor id is required.", nameof(rootActorId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(_codec.Channel))
            throw new InvalidOperationException("Session event codec channel is required.");
        ArgumentNullException.ThrowIfNull(handler);

        var stream = _streamProvider.GetStream(ResolveStreamId(rootActorId, sessionId));
        return await stream.SubscribeAsync<ProjectionSessionEventTransportMessage>(async message =>
        {
            if (!string.Equals(message.RootActorId, rootActorId, StringComparison.Ordinal) ||
                !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (message.Payload == null || message.Payload.IsEmpty)
            {
                _logger.LogWarning(
                    "Dropping projection session event with empty protobuf payload. channel={Channel} rootActorId={RootActorId} sessionId={SessionId} eventType={EventType}",
                    _codec.Channel,
                    rootActorId,
                    sessionId,
                    message.EventType);
                return;
            }

            var evt = _codec.Deserialize(message.EventType, message.Payload);

            if (evt == null)
            {
                _logger.LogWarning(
                    "Dropping undecodable projection session event. channel={Channel} rootActorId={RootActorId} sessionId={SessionId} eventType={EventType} payloadBytes={PayloadBytes}",
                    _codec.Channel,
                    rootActorId,
                    sessionId,
                    message.EventType,
                    message.Payload.Length);
                return;
            }

            await handler(evt);
        }, ct);
    }

    private string ResolveStreamId(string rootActorId, string sessionId) =>
        $"{_codec.Channel}:{rootActorId.Trim()}:{sessionId.Trim()}";
}
