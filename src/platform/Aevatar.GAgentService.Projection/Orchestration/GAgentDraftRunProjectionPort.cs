using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunProjectionPort
    : EventSinkProjectionLifecyclePortBase<IGAgentDraftRunProjectionLease, GAgentDraftRunRuntimeLease, AGUIEvent>,
      IGAgentDraftRunProjectionPort
{
    public GAgentDraftRunProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<GAgentDraftRunRuntimeLease> activationService,
        IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
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
}
