using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunProjectionPort
    : EventSinkProjectionLifecyclePortBase<IGAgentDraftRunProjectionLease, GAgentDraftRunRuntimeLease, AGUIEvent>,
      IGAgentDraftRunProjectionPort
{
    private readonly IActorRuntime _runtime;

    public GAgentDraftRunProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<GAgentDraftRunRuntimeLease> activationService,
        IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IActorRuntime runtime)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ServiceProjectionKinds.DraftRunSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = commandId,
            },
            ct);

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
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = ServiceProjectionKinds.DraftRunSession,
            SessionId = commandId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(lease, liveSinkLease);
    }
}
