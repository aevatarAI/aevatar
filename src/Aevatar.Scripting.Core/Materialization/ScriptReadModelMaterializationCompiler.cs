using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Scripting.Core.Materialization;

public sealed class ScriptReadModelMaterializationCompiler : IScriptReadModelMaterializationCompiler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScriptReadModelMaterializationPlan> _plans = new(StringComparer.Ordinal);

    public ScriptReadModelMaterializationPlan GetOrCompile(
        ScriptBehaviorArtifact artifact,
        string schemaHash,
        string schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!ScriptSchemaDescriptorExtractor.TryExtractFromDescriptor(artifact.Descriptor, out var extraction))
        {
            return new ScriptReadModelMaterializationPlan(
                SchemaId: string.Empty,
                SchemaVersion: string.Empty,
                SchemaHash: string.Empty,
                DocumentIndexScope: string.Empty,
                DocumentMetadata: new ScriptMaterializedDocumentMetadata(
                    IndexName: "script-native-read-models",
                    Mappings: new Dictionary<string, object?>(StringComparer.Ordinal),
                    Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
                    Aliases: new Dictionary<string, object?>(StringComparer.Ordinal)),
                DocumentFields: Array.Empty<ScriptDocumentFieldMaterialization>(),
                GraphRelations: Array.Empty<ScriptGraphRelationMaterialization>());
        }

        var normalizedSchemaHash = string.IsNullOrWhiteSpace(schemaHash)
            ? BuildFallbackSchemaHash(extraction.SchemaSpec)
            : schemaHash.Trim().ToLowerInvariant();
        var cacheKey = string.Concat(
            artifact.Descriptor.ReadModelTypeUrl ?? string.Empty,
            "|",
            normalizedSchemaHash,
            "|",
            extraction.SchemaVersion ?? string.Empty);
        lock (_gate)
        {
            if (_plans.TryGetValue(cacheKey, out var cached))
                return cached;

            var compiled = CompileCore(artifact, extraction, normalizedSchemaHash, schemaVersion);
            _plans[cacheKey] = compiled;
            return compiled;
        }
    }

    private static ScriptReadModelMaterializationPlan CompileCore(
        ScriptBehaviorArtifact artifact,
        ScriptSchemaDescriptorExtraction extraction,
        string schemaHash,
        string schemaVersion)
    {
        var schemaSpec = extraction.SchemaSpec;
        var documentFields = schemaSpec.Fields
            .Select(field => new ScriptDocumentFieldMaterialization(
                field.Name ?? string.Empty,
                field.Type ?? string.Empty,
                field.Path ?? string.Empty,
                field.Nullable,
                ScriptReadModelPathAccessor.Compile(artifact.Descriptor.ReadModelDescriptor, field.Path ?? string.Empty)))
            .ToArray();
        var relationDefinitions = schemaSpec.Relations
            .Where(static x => string.IsNullOrWhiteSpace(x.Provider) || string.Equals(x.Provider, "graph", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var graphRelations = relationDefinitions
            .Select(relation => new ScriptGraphRelationMaterialization(
                relation.Name ?? string.Empty,
                relation.SourcePath ?? string.Empty,
                relation.TargetSchemaId ?? string.Empty,
                relation.TargetPath ?? string.Empty,
                relation.Cardinality ?? string.Empty,
                ScriptReadModelPathAccessor.Compile(artifact.Descriptor.ReadModelDescriptor, relation.SourcePath ?? string.Empty)))
            .ToArray();

        ValidateIndexes(schemaSpec, documentFields);
        ValidateRelations(schemaSpec);

        var normalizedSchemaId = NormalizeToken(schemaSpec.SchemaId);
        var normalizedSchemaVersion = string.IsNullOrWhiteSpace(schemaVersion)
            ? schemaSpec.SchemaVersion ?? string.Empty
            : schemaVersion;
        var indexScope = BuildDocumentIndexScope(normalizedSchemaId, schemaHash);
        var metadata = BuildDocumentMetadata(schemaSpec, indexScope);

        return new ScriptReadModelMaterializationPlan(
            SchemaId: schemaSpec.SchemaId ?? string.Empty,
            SchemaVersion: normalizedSchemaVersion,
            SchemaHash: schemaHash,
            DocumentIndexScope: indexScope,
            DocumentMetadata: metadata,
            DocumentFields: documentFields,
            GraphRelations: graphRelations);
    }

    private static void ValidateIndexes(
        ScriptReadModelSchemaSpec definition,
        IReadOnlyList<ScriptDocumentFieldMaterialization> documentFields)
    {
        var fieldPaths = new HashSet<string>(
            documentFields.Select(static x => x.Path),
            StringComparer.Ordinal);
        foreach (var index in definition.Indexes
                     .Where(static x => string.IsNullOrWhiteSpace(x.Provider) || string.Equals(x.Provider, "document", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(index.Name))
            {
                throw new InvalidOperationException("Read model document index name cannot be empty.");
            }

            foreach (var path in index.Paths)
            {
                if (fieldPaths.Contains(path))
                    continue;

                throw new InvalidOperationException(
                    $"Read model index `{index.Name}` references path `{path}`, but that path is not declared in read model fields.");
            }
        }
    }

    private static void ValidateRelations(ScriptReadModelSchemaSpec definition)
    {
        foreach (var relation in definition.Relations
                     .Where(static x => string.IsNullOrWhiteSpace(x.Provider) || string.Equals(x.Provider, "graph", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(relation.Name))
                throw new InvalidOperationException("Read model relation name cannot be empty.");
            if (string.IsNullOrWhiteSpace(relation.SourcePath))
                throw new InvalidOperationException($"Read model relation `{relation.Name}` requires a source path.");
            if (string.IsNullOrWhiteSpace(relation.TargetSchemaId))
                throw new InvalidOperationException($"Read model relation `{relation.Name}` requires a target schema id.");
            if (string.IsNullOrWhiteSpace(relation.TargetPath))
                throw new InvalidOperationException($"Read model relation `{relation.Name}` requires a target path.");
        }
    }

    private static ScriptMaterializedDocumentMetadata BuildDocumentMetadata(
        ScriptReadModelSchemaSpec definition,
        string indexScope)
    {
        var fieldProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            AssignFieldMapping(
                fieldProperties,
                (field.Path ?? string.Empty)
                .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                BuildLeafFieldMapping(field.Type));
        }

        var rootProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = KeywordMapping(),
            ["script_id"] = KeywordMapping(),
            ["definition_actor_id"] = KeywordMapping(),
            ["revision"] = KeywordMapping(),
            ["schema_id"] = KeywordMapping(),
            ["schema_version"] = KeywordMapping(),
            ["schema_hash"] = KeywordMapping(),
            ["document_index_scope"] = KeywordMapping(),
            ["state_version"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "long",
            },
            ["last_event_id"] = KeywordMapping(),
            ["updated_at"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "date",
            },
            ["fields"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["properties"] = fieldProperties,
            },
        };

        return new ScriptMaterializedDocumentMetadata(
            IndexName: indexScope,
            Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dynamic"] = false,
                ["properties"] = rootProperties,
            },
            Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
            Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    private static void AssignFieldMapping(
        IDictionary<string, object?> current,
        IReadOnlyList<string> segments,
        IReadOnlyDictionary<string, object?> leafMapping)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("Document field mapping requires at least one path segment.");

        IDictionary<string, object?> cursor = current;
        for (var i = 0; i < segments.Count; i++)
        {
            var rawSegment = segments[i];
            var segment = rawSegment.EndsWith("[]", StringComparison.Ordinal)
                ? rawSegment[..^2]
                : rawSegment;
            if (segment.Length == 0)
                throw new InvalidOperationException("Document field mapping contains an empty path segment.");

            var isLeaf = i == segments.Count - 1;
            if (isLeaf)
            {
                cursor[segment] = new Dictionary<string, object?>(leafMapping, StringComparer.Ordinal);
                return;
            }

            if (!cursor.TryGetValue(segment, out var existing) ||
                existing is not IDictionary<string, object?> existingObject)
            {
                existingObject = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                };
                cursor[segment] = existingObject;
            }

            if (!existingObject.TryGetValue("properties", out var properties) ||
                properties is not IDictionary<string, object?> propertyMap)
            {
                propertyMap = new Dictionary<string, object?>(StringComparer.Ordinal);
                existingObject["properties"] = propertyMap;
            }

            cursor = propertyMap;
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildLeafFieldMapping(string? declaredType)
    {
        var normalized = (declaredType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => KeywordMapping(),
            "keyword" => KeywordMapping(),
            "keyword[]" => KeywordMapping(),
            "text" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "text",
            },
            "boolean" => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "boolean",
            },
            "int32" => LongMapping(),
            "int64" => LongMapping(),
            "long" => LongMapping(),
            "integer" => LongMapping(),
            "double" => DoubleMapping(),
            "float" => DoubleMapping(),
            "number" => DoubleMapping(),
            "timestamp" => DateMapping(),
            "date" => DateMapping(),
            _ => throw new InvalidOperationException(
                $"Read model field type `{declaredType}` is not supported for native document materialization."),
        };
    }

    private static Dictionary<string, object?> KeywordMapping() =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "keyword",
        };

    private static Dictionary<string, object?> LongMapping() =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "long",
        };

    private static Dictionary<string, object?> DoubleMapping() =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "double",
        };

    private static Dictionary<string, object?> DateMapping() =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "date",
        };

    private static string BuildDocumentIndexScope(string normalizedSchemaId, string schemaHash)
    {
        var schemaPart = normalizedSchemaId.Length == 0
            ? "script-read-model"
            : normalizedSchemaId;
        var hashPart = schemaHash.Length == 0
            ? "latest"
            : schemaHash[..Math.Min(schemaHash.Length, 12)];
        return $"script-native-{schemaPart}-{hashPart}";
    }

    private static string NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var chars = token
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string BuildFallbackSchemaHash(ScriptReadModelSchemaSpec definition)
    {
        return string.Concat(
            NormalizeToken(definition.SchemaId),
            "-",
            NormalizeToken(definition.SchemaVersion));
    }
}
