using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Abstractions.Evolution;

public interface IScriptEvolutionProjectionLease
{
    string ActorId { get; }

    string ProposalId { get; }
}

public interface IScriptEvolutionProjectionPort
    : IEventSinkProjectionLifecyclePort<IScriptEvolutionProjectionLease, ScriptEvolutionSessionCompletedEvent>
{
    Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default);
}
