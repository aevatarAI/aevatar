namespace Aevatar.Scripting.Application;

public sealed record ProposeScriptEvolutionRequest(
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string Reason,
    string DefinitionActorId,
    string CatalogActorId,
    string RequestedByActorId,
    string ProposalId,
    string ManagerActorId);
