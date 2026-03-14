using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptPromotionDecision(
    bool Accepted,
    string ProposalId,
    string ScriptId,
    string BaseRevision,
    string CandidateRevision,
    string Status,
    string FailureReason,
    string DefinitionActorId,
    string CatalogActorId,
    ScriptEvolutionValidationReport ValidationReport,
    ScriptDefinitionBindingSpec? DefinitionSnapshot = null)
{
    public static ScriptPromotionDecision Rejected(
        ScriptEvolutionProposal proposal,
        string failureReason,
        ScriptEvolutionValidationReport? validation = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return new ScriptPromotionDecision(
            Accepted: false,
            ProposalId: proposal.ProposalId ?? string.Empty,
            ScriptId: proposal.ScriptId ?? string.Empty,
            BaseRevision: proposal.BaseRevision ?? string.Empty,
            CandidateRevision: proposal.CandidateRevision ?? string.Empty,
            Status: ScriptEvolutionStatuses.Rejected,
            FailureReason: failureReason ?? string.Empty,
            DefinitionActorId: string.Empty,
            CatalogActorId: string.Empty,
            ValidationReport: validation ?? ScriptEvolutionValidationReport.Empty,
            DefinitionSnapshot: null);
    }
}
