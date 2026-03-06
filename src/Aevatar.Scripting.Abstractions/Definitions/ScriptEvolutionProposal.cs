namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptEvolutionProposal(
    string ProposalId,
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string Reason);
