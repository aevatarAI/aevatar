namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptPromotionRequest(
    string ProposalId,
    string ScriptId,
    string CandidateRevision,
    string CandidateSource,
    string CandidateSourceHash,
    string DefinitionActorId,
    string CatalogActorId);

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

public interface IScriptPromotionPort
{
    Task<ScriptPromotionResult> PromoteAsync(
        ScriptPromotionRequest request,
        CancellationToken ct);

    Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct);
}
