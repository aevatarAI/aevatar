namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptReadModelDefinition(
    string SchemaId,
    string SchemaVersion,
    IReadOnlyList<ScriptReadModelFieldDefinition> Fields,
    IReadOnlyList<ScriptReadModelIndexDefinition> Indexes,
    IReadOnlyList<ScriptReadModelRelationDefinition> Relations);

public sealed record ScriptReadModelFieldDefinition(
    string Name,
    string Type,
    string Path,
    bool Nullable);

public sealed record ScriptReadModelIndexDefinition(
    string Name,
    IReadOnlyList<string> Paths,
    bool Unique,
    string Provider);

public sealed record ScriptReadModelRelationDefinition(
    string Name,
    string SourcePath,
    string TargetSchemaId,
    string TargetPath,
    string Cardinality,
    string Provider);
