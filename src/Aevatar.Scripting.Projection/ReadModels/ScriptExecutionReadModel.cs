namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptExecutionReadModel : IProjectionReadModel
{
    public string Id { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string LastRunId { get; set; } = string.Empty;
    public string LastEventType { get; set; } = string.Empty;
    public string LastDomainEventPayloadJson { get; set; } = string.Empty;
    public string DecisionStatus { get; set; } = string.Empty;
    public bool ManualReviewRequired { get; set; }
    public string StatePayloadJson { get; set; } = string.Empty;
    public string ReadModelPayloadJson { get; set; } = string.Empty;
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
