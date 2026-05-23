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

    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        return await AttachExistingProjectionAsync(
            actorId,
            sessionId,
            StreamingProxyProjectionKinds.RoomChatSession,
            sink,
            ct).ConfigureAwait(false);
    }

    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingSubscriptionProjectionAsync(
        string actorId,
        string subscriptionId,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct = default)
    {
        return await AttachExistingProjectionAsync(
            actorId,
            subscriptionId,
            StreamingProxyProjectionKinds.RoomSubscriptionSession,
            sink,
            ct).ConfigureAwait(false);
    }

    private async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingProjectionAsync(
        string actorId,
        string sessionId,
        string projectionKind,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        CancellationToken ct)
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
            projectionKind,
            ProjectionRuntimeMode.SessionObservation,
            sessionId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new StreamingProxyRoomSessionRuntimeLease(new StreamingProxyRoomSessionProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = projectionKind,
            SessionId = sessionId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(lease, liveSinkLease);
    }
}
