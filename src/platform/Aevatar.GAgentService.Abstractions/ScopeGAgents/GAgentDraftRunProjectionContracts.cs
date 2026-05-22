using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Presentation.AGUI;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public interface IGAgentDraftRunProjectionLease
{
    string ActorId { get; }

    string CommandId { get; }
}

public interface IGAgentDraftRunProjectionPort
    : IEventSinkProjectionLifecyclePort<IGAgentDraftRunProjectionLease, AGUIEvent>
{
    Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default);

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
