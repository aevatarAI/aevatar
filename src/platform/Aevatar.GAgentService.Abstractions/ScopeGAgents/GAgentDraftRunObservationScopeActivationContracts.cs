namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

// Refactor (issue-1687):
//   Old pattern: draft-run application services owned explicit pre-dispatch projection activation.
//   New principle: this port only prepares observation scope leases for one interaction attempt.
//   It does not promise command admission and is not query/read-model priming.
public sealed record GAgentDraftRunObservationScopeLeasePreparation(
    string ActorId,
    string CommandId,
    string CorrelationId);

public interface IGAgentDraftRunObservationScopeLeasePreparationPort
{
    Task<GAgentDraftRunObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        string correlationId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        GAgentDraftRunObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}
