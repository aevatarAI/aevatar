namespace Aevatar.Scripting.Core.Materialization;

public sealed record ScriptReadModelMaterializationPlan(
    string SchemaId,
    string SchemaVersion,
    string SchemaHash,
    string DocumentIndexScope,
    ScriptMaterializedDocumentMetadata DocumentMetadata,
    IReadOnlyList<ScriptDocumentFieldMaterialization> DocumentFields,
    IReadOnlyList<ScriptGraphRelationMaterialization> GraphRelations)
{
    public bool SupportsDocument => DocumentFields.Count > 0;

    public bool SupportsGraph => GraphRelations.Count > 0;
}

public sealed record ScriptDocumentFieldMaterialization(
    string Name,
    string Type,
    string Path,
    bool Nullable,
    ScriptReadModelPathAccessor Accessor);

public sealed record ScriptGraphRelationMaterialization(
    string Name,
    string SourcePath,
    string TargetSchemaId,
    string TargetPath,
    string Cardinality,
    ScriptReadModelPathAccessor SourceAccessor);

public sealed record ScriptMaterializedDocumentMetadata(
    string IndexName,
    IReadOnlyDictionary<string, object?> Mappings,
    IReadOnlyDictionary<string, object?> Settings,
    IReadOnlyDictionary<string, object?> Aliases);
