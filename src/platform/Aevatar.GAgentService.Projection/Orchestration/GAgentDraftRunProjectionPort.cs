using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunProjectionPort
    : EventSinkProjectionLifecyclePortBase<IGAgentDraftRunProjectionLease, GAgentDraftRunRuntimeLease, AGUIEvent>,
      IGAgentDraftRunProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<GAgentDraftRunRuntimeLease> _attachExistingLeaseLookup;

    public GAgentDraftRunProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<GAgentDraftRunRuntimeLease> attachExistingLeaseLookup)
        : base(
            () => options.Enabled,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    // Refactor (iter52/issue-905-public-projection-ensure-ports):
    //   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
    //   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
    public async Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
        string actorId,
        string commandId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default)
    {
        // Refactor (iter101/cluster-104): Old event-sink lifecycle base allowed derived request ports to ensure projection sessions; new request-facing API is attach-existing only.
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            actorId,
            ServiceProjectionKinds.DraftRunSession,
            ProjectionRuntimeMode.SessionObservation,
            commandId);
        // Refactor (iter49/cluster-049-gagentservice-runtime-attach-existing-side-read):
        //   Old pattern: Capability projection ports duplicated runtime existence checks via IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()).
        //   New principle: Projection Core exposes typed attach-existing lease/session lookup contract; capability ports delegate to contract instead of runtime actor-id side reads.
        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                Mode = scopeKey.Mode,
                SessionId = scopeKey.SessionId,
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(lease, liveSinkLease);
    }
}
