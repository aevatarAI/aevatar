using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptPromotionResult(
    string DefinitionActorId,
    string CatalogActorId,
    string PromotedRevision);

public sealed record ScriptRollbackRequest(
    string ProposalId,
    string ScriptId,
    string TargetRevision,
    string CatalogActorId,
    string Reason);

public enum ScriptEvolutionFlowStatus
{
    PolicyRejected = 0,
    ValidationFailed = 1,
    PromotionFailed = 2,
    Promoted = 3,
}

public sealed record ScriptEvolutionFlowResult(
    ScriptEvolutionFlowStatus Status,
    ScriptEvolutionValidationReport ValidationReport,
    ScriptPromotionResult? Promotion,
    string FailureReason)
{
    public static ScriptEvolutionFlowResult PolicyRejected(string failureReason) =>
        new(
            ScriptEvolutionFlowStatus.PolicyRejected,
            ScriptEvolutionValidationReport.Empty,
            null,
            failureReason ?? string.Empty);

    public static ScriptEvolutionFlowResult ValidationFailed(ScriptEvolutionValidationReport validation) =>
        new(
            ScriptEvolutionFlowStatus.ValidationFailed,
            validation ?? ScriptEvolutionValidationReport.Empty,
            null,
            string.Join("; ", validation?.Diagnostics ?? Array.Empty<string>()));

    public static ScriptEvolutionFlowResult PromotionFailed(
        ScriptEvolutionValidationReport validation,
        string failureReason) =>
        new(
            ScriptEvolutionFlowStatus.PromotionFailed,
            validation ?? ScriptEvolutionValidationReport.Empty,
            null,
            failureReason ?? string.Empty);

    public static ScriptEvolutionFlowResult Promoted(
        ScriptEvolutionValidationReport validation,
        ScriptPromotionResult promotion) =>
        new(
            ScriptEvolutionFlowStatus.Promoted,
            validation ?? ScriptEvolutionValidationReport.Empty,
            promotion ?? throw new ArgumentNullException(nameof(promotion)),
            string.Empty);
}

public interface IScriptEvolutionFlowPort
{
    Task<ScriptEvolutionFlowResult> ExecuteAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);

    Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct);
}
