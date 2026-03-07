namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptEvolutionDecision(
    string ProposalId,
    string ScriptId,
    string SessionActorId,
    bool Accepted,
    string Status,
    string FailureReason,
    string DefinitionActorId,
    string CandidateRevision,
    string CatalogActorId);
