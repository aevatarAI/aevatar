using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Materialization;

public sealed class ScriptNativeDocumentMaterializer : IScriptNativeDocumentMaterializer
{
    public ScriptNativeDocumentReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        string sourceEventId,
        DateTimeOffset updatedAt,
        ScriptNativeDocumentProjection nativeDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentNullException.ThrowIfNull(nativeDocument);

        return new ScriptNativeDocumentReadModel
        {
            Id = actorId,
            ScriptId = scriptId ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            Revision = revision ?? string.Empty,
            SchemaId = nativeDocument.SchemaId ?? string.Empty,
            SchemaVersion = nativeDocument.SchemaVersion ?? string.Empty,
            SchemaHash = nativeDocument.SchemaHash ?? string.Empty,
            DocumentIndexScope = nativeDocument.DocumentIndexScope ?? string.Empty,
            FieldsValue = nativeDocument.FieldsValue?.Clone() ?? new Google.Protobuf.WellKnownTypes.Struct(),
            StateVersion = fact.StateVersion,
            LastEventId = sourceEventId,
            UpdatedAt = updatedAt,
        };
    }
}
