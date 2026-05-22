using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter37/cluster-037-agent-session-observation-attach-only):
//   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
//   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
public sealed class StreamingProxyRoomSessionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IStreamingProxyRoomSessionProjectionLease, StreamingProxyRoomSessionRuntimeLease, StreamingProxyRoomSessionEnvelope>,
      IStreamingProxyRoomSessionProjectionPort
{
    private readonly IActorRuntime _runtime;

    public StreamingProxyRoomSessionProjectionPort(
        IProjectionScopeActivationService<StreamingProxyRoomSessionRuntimeLease> activationService,
        IProjectionScopeReleaseService<StreamingProxyRoomSessionRuntimeLease> releaseService,
        IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope> sessionEventHub,
        IActorRuntime runtime)
        : base(
            static () => true,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureRoomProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default)
    {
        return await EnsureChatProjectionAsync(actorId, sessionId, ct);
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureChatProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default)
    {
        return await EnsureProjectionAsync(actorId, sessionId, StreamingProxyProjectionKinds.RoomChatSession, ct);
    }

    public async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureSubscriptionProjectionAsync(
        string actorId,
        string subscriptionId,
        CancellationToken ct = default)
    {
        return await EnsureProjectionAsync(actorId, subscriptionId, StreamingProxyProjectionKinds.RoomSubscriptionSession, ct);
    }

    private async Task<IStreamingProxyRoomSessionProjectionLease?> EnsureProjectionAsync(
        string actorId,
        string sessionId,
        string projectionKind,
        CancellationToken ct)
    {
        return await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);
    }

    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
    public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            actorId,
            StreamingProxyProjectionKinds.RoomChatSession,
            ProjectionRuntimeMode.SessionObservation,
            sessionId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new StreamingProxyRoomSessionRuntimeLease(new StreamingProxyRoomSessionProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = StreamingProxyProjectionKinds.RoomChatSession,
            SessionId = sessionId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(lease, liveSinkLease);
    }
}
