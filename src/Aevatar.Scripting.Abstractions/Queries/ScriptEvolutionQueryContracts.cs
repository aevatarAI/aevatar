namespace Aevatar.Scripting.Abstractions.Queries;

public sealed class ScriptEvolutionProposalSnapshot
{
    public string ProposalId { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string BaseRevision { get; set; } = string.Empty;
    public string CandidateRevision { get; set; } = string.Empty;
    public string ValidationStatus { get; set; } = string.Empty;
    public string PromotionStatus { get; set; } = string.Empty;
    public string RollbackStatus { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public bool Accepted { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string CatalogActorId { get; set; } = string.Empty;
    public List<string> Diagnostics { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IScriptEvolutionProjectionQueryPort
{
    Task<ScriptEvolutionProposalSnapshot?> GetProposalSnapshotAsync(
        string proposalId,
        CancellationToken ct = default);
}
