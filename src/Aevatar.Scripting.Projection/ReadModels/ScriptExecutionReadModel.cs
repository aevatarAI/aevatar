using Google.Protobuf.WellKnownTypes;
namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptExecutionReadModel : IProjectionReadModel
{
    public string Id { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string ReadModelSchemaVersion { get; set; } = string.Empty;
    public string ReadModelSchemaHash { get; set; } = string.Empty;
    public string LastRunId { get; set; } = string.Empty;
    public string LastEventType { get; set; } = string.Empty;
    public Any? LastDomainEventPayload { get; set; }
    public Dictionary<string, Any> StatePayloads { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Any> ReadModelPayloads { get; set; } = new(StringComparer.Ordinal);
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
