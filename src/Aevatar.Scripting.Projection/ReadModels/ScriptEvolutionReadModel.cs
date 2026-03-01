namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptEvolutionReadModel : IProjectionReadModel
{
    public string Id { get; set; } = string.Empty;
    public string ProposalId { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string BaseRevision { get; set; } = string.Empty;
    public string CandidateRevision { get; set; } = string.Empty;
    public string ValidationStatus { get; set; } = string.Empty;
    public string PromotionStatus { get; set; } = string.Empty;
    public string RollbackStatus { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string CatalogActorId { get; set; } = string.Empty;
    public List<string> Diagnostics { get; set; } = [];
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
