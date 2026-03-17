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
    string Reason,
    string ExpectedCurrentRevision);

public sealed record ScriptCatalogBaselineResolution(
    string CatalogActorId,
    ScriptCatalogEntrySnapshot? Baseline,
    string BaselineSource,
    string FailureReason)
{
    public bool HasFailure => !string.IsNullOrWhiteSpace(FailureReason);
}

public interface IScriptEvolutionPolicyEvaluator
{
    string EvaluateFailure(ScriptEvolutionProposal proposal);
}

public interface IScriptEvolutionValidationService
{
    Task<ScriptEvolutionValidationReport> ValidateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);
}

public interface IScriptCatalogBaselineReader
{
    Task<ScriptCatalogBaselineResolution> ReadAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);
}

public interface IScriptPromotionCompensationService
{
    Task<string> TryCompensateAsync(
        string catalogActorId,
        ScriptEvolutionProposal proposal,
        ScriptCatalogEntrySnapshot? catalogBefore,
        CancellationToken ct);
}

public interface IScriptEvolutionRollbackService
{
    Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct);
}
