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
    Task<IStreamingProxyRoomSessionProjectionLease?> EnsureRoomProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default) =>
        EnsureChatProjectionAsync(actorId, sessionId, ct);

    Task<IStreamingProxyRoomSessionProjectionLease?> EnsureChatProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default);

    Task<IStreamingProxyRoomSessionProjectionLease?> EnsureSubscriptionProjectionAsync(
        string actorId,
        string subscriptionId,
        CancellationToken ct = default);

    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
    Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default);
}
