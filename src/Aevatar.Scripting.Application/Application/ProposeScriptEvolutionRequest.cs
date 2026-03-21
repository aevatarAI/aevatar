namespace Aevatar.Scripting.Application;

public sealed record ProposeScriptEvolutionRequest(
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string Reason,
    string ProposalId,
    string ScopeId = "");
