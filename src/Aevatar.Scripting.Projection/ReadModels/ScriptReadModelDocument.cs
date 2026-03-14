using Google.Protobuf.WellKnownTypes;
using System.Text.Json.Serialization;
using Aevatar.Scripting.Projection.Serialization;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptReadModelDocument : IProjectionReadModel
    , IProjectionReadModelCloneable<ScriptReadModelDocument>
{
    public string Id { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string ReadModelTypeUrl { get; set; } = string.Empty;
    [JsonConverter(typeof(ProtobufAnyBase64JsonConverter))]
    public Any? ReadModelPayload { get; set; }
    public long StateVersion { get; set; }
    public string LastEventId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }

    public ScriptReadModelDocument DeepClone() =>
        new()
        {
            Id = Id,
            ScriptId = ScriptId,
            DefinitionActorId = DefinitionActorId,
            Revision = Revision,
            ReadModelTypeUrl = ReadModelTypeUrl,
            ReadModelPayload = ReadModelPayload?.Clone(),
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            UpdatedAt = UpdatedAt,
        };
}
