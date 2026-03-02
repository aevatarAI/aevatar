namespace Aevatar.Scripting.Application;

public sealed record ProposeScriptEvolutionCommand(
    string ProposalId,
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string Reason,
    string DefinitionActorId,
    string CatalogActorId,
    string RequestedByActorId,
    string DecisionRequestId = "",
    string DecisionReplyStreamId = "");
