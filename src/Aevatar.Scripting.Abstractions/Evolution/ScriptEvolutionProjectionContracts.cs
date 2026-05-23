using Aevatar.Scripting.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.Scripting.Abstractions.Evolution;

public interface IScriptEvolutionProjectionLease
{
    string ActorId { get; }

    string ProposalId { get; }
}

public interface IScriptEvolutionProjectionPort
    : IEventSinkProjectionLifecyclePort<IScriptEvolutionProjectionLease, ScriptEvolutionSessionCompletedEvent>
{
    // Refactor (iter41/cluster-041-command-observation-projection-activation):
    //   Old pattern: command observation binders ensure/activate projection/readmodel sessions before dispatch.
    //   New principle: observation binders attach only to existing projection-owned sessions;
    //   activation happens in projection-owned startup/background/committed-state lifecycle.
    Task<EventSinkProjectionAttachment<IScriptEvolutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct = default);
}
