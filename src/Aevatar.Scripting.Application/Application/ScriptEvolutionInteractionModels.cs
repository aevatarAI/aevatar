using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public enum ScriptEvolutionStartError
{
    None = 0,
    ProjectionDisabled = 1,
}

public sealed record ScriptEvolutionAcceptedReceipt(
    string ManagerActorId,
    string SessionActorId,
    string ProposalId,
    string CommandId,
    string CorrelationId);

public sealed record ScriptEvolutionInteractionCompletion(
    bool Accepted,
    string ProposalId,
    string Status,
    string FailureReason,
    string DefinitionActorId,
    string CatalogActorId,
    ScriptEvolutionValidationReport ValidationReport)
{
    public static ScriptEvolutionInteractionCompletion Pending { get; } = new(
        Accepted: false,
        ProposalId: string.Empty,
        Status: "pending",
        FailureReason: string.Empty,
        DefinitionActorId: string.Empty,
        CatalogActorId: string.Empty,
        ValidationReport: ScriptEvolutionValidationReport.Empty);

    public ScriptPromotionDecision ToPromotionDecision(ScriptEvolutionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return new ScriptPromotionDecision(
            Accepted: Accepted,
            ProposalId: string.IsNullOrWhiteSpace(ProposalId) ? proposal.ProposalId : ProposalId,
            ScriptId: proposal.ScriptId ?? string.Empty,
            BaseRevision: proposal.BaseRevision ?? string.Empty,
            CandidateRevision: proposal.CandidateRevision ?? string.Empty,
            Status: Status ?? string.Empty,
            FailureReason: FailureReason ?? string.Empty,
            DefinitionActorId: DefinitionActorId ?? string.Empty,
            CatalogActorId: CatalogActorId ?? string.Empty,
            ValidationReport: ValidationReport ?? ScriptEvolutionValidationReport.Empty);
    }

    public static ScriptEvolutionInteractionCompletion FromDecision(ScriptPromotionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new ScriptEvolutionInteractionCompletion(
            Accepted: decision.Accepted,
            ProposalId: decision.ProposalId ?? string.Empty,
            Status: decision.Status ?? string.Empty,
            FailureReason: decision.FailureReason ?? string.Empty,
            DefinitionActorId: decision.DefinitionActorId ?? string.Empty,
            CatalogActorId: decision.CatalogActorId ?? string.Empty,
            ValidationReport: decision.ValidationReport ?? ScriptEvolutionValidationReport.Empty);
    }
}
