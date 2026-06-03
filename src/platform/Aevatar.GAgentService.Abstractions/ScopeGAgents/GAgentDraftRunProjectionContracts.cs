using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public interface IGAgentDraftRunProjectionLease
{
    string ActorId { get; }

    string CommandId { get; }
}

public interface IGAgentDraftRunProjectionPort
    : IEventSinkProjectionLifecyclePort<IGAgentDraftRunProjectionLease, AGUIEvent>
{
    // Refactor (iter52/issue-905-public-projection-ensure-ports):
    //   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
    //   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
    Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
        string actorId,
        string commandId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default);
}
