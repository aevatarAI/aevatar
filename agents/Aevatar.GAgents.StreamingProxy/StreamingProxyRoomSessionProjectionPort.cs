using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter37/cluster-037-agent-session-observation-attach-only):
//   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
//   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
public sealed class StreamingProxyRoomSessionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IStreamingProxyRoomSessionProjectionLease, StreamingProxyRoomSessionRuntimeLease, StreamingProxyRoomSessionEnvelope>,
      IStreamingProxyRoomSessionProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease> _attachExistingLeaseLookup;

    public StreamingProxyRoomSessionProjectionPort(
        IProjectionScopeReleaseService<StreamingProxyRoomSessionRuntimeLease> releaseService,
        IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease> attachExistingLeaseLookup)
        : base(
            static () => true,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
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
        // Refactor (iter101/cluster-104): Old streaming proxy port could inherit ensure activation; new attach path only observes sessions already activated by projection binders.
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        // Refactor (iter51/issue-898-projection-attach-existing-side-read):
        //   Old pattern: Feature projection ports duplicated IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()) for attach-existing checks (post-#884 #884 fixed 3 ports but more remained).
        //   New principle: All attach-existing lease lookups go through typed IProjectionScopeAttachExistingLeaseLookup<TLease>; CI guard prevents recurrence.
        var lease = await _attachExistingLeaseLookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = actorId,
            ProjectionKind = projectionKind,
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = sessionId,
        }, ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(lease, liveSinkLease);
    }
}
