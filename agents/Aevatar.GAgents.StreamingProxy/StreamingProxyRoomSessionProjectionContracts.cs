using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgents.StreamingProxy;

public interface IStreamingProxyRoomSessionProjectionLease
{
    string ActorId { get; }

    string SessionId { get; }
}

public interface IStreamingProxyRoomSessionProjectionPort
    : IEventSinkProjectionLifecyclePort<IStreamingProxyRoomSessionProjectionLease, StreamingProxyRoomSessionEnvelope>
{
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default);

    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingSubscriptionProjectionAsync(
        string actorId,
        string subscriptionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default);
}
