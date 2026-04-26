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
}
