using Aevatar.CQRS.Projection.Stores.Abstractions;
using System.Text.Json.Serialization;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptNativeDocumentReadModel
    : IDynamicDocumentIndexedReadModel,
      IProjectionReadModelCloneable<ScriptNativeDocumentReadModel>
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("script_id")]
    public string ScriptId { get; set; } = string.Empty;

    [JsonPropertyName("definition_actor_id")]
    public string DefinitionActorId { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("schema_id")]
    public string SchemaId { get; set; } = string.Empty;

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("schema_hash")]
    public string SchemaHash { get; set; } = string.Empty;

    [JsonPropertyName("document_index_scope")]
    public string DocumentIndexScope { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("state_version")]
    public long StateVersion { get; set; }

    [JsonPropertyName("last_event_id")]
    public string LastEventId { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public DocumentIndexMetadata DocumentMetadata { get; set; } = new(
        IndexName: "script-native-read-models",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    public ScriptNativeDocumentReadModel DeepClone()
    {
        return new ScriptNativeDocumentReadModel
        {
            Id = Id,
            ScriptId = ScriptId,
            DefinitionActorId = DefinitionActorId,
            Revision = Revision,
            SchemaId = SchemaId,
            SchemaVersion = SchemaVersion,
            SchemaHash = SchemaHash,
            DocumentIndexScope = DocumentIndexScope,
            Fields = Fields.ToDictionary(
                static pair => pair.Key,
                static pair => ScriptNativeReadModelCloneSupport.CloneObjectGraph(pair.Value),
                StringComparer.Ordinal),
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            UpdatedAt = UpdatedAt,
            DocumentMetadata = new DocumentIndexMetadata(
                DocumentMetadata.IndexName,
                DocumentMetadata.Mappings.ToDictionary(
                    static pair => pair.Key,
                    static pair => ScriptNativeReadModelCloneSupport.CloneObjectGraph(pair.Value),
                    StringComparer.Ordinal),
                DocumentMetadata.Settings.ToDictionary(
                    static pair => pair.Key,
                    static pair => ScriptNativeReadModelCloneSupport.CloneObjectGraph(pair.Value),
                    StringComparer.Ordinal),
                DocumentMetadata.Aliases.ToDictionary(
                    static pair => pair.Key,
                    static pair => ScriptNativeReadModelCloneSupport.CloneObjectGraph(pair.Value),
                    StringComparer.Ordinal)),
        };
    }
}
