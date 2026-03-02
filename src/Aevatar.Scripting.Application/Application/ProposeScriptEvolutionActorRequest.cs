namespace Aevatar.Scripting.Application;

public sealed record ProposeScriptEvolutionActorRequest(
    string ProposalId,
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string Reason);
