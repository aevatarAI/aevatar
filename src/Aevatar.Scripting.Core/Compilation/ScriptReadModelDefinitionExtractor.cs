using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;

namespace Aevatar.Scripting.Core.Compilation;

internal static class ScriptReadModelDefinitionExtractor
{
    public static bool TryExtractFromContract(
        ScriptGAgentContract? contract,
        out ScriptReadModelDefinitionExtraction extraction)
    {
        var definition = contract?.ReadModelDefinition;
        if (definition == null)
        {
            extraction = ScriptReadModelDefinitionExtraction.Empty;
            return false;
        }

        var schemaSpec = BuildSchemaSpec(definition);
        var capabilities = ResolveStoreCapabilities(
            definition.Indexes,
            definition.Relations,
            contract!.StoreKinds ?? Array.Empty<string>());
        extraction = new ScriptReadModelDefinitionExtraction(
            definition,
            Any.Pack(schemaSpec),
            ComputeSha256(schemaSpec.ToByteArray()),
            definition.SchemaVersion ?? string.Empty,
            capabilities);
        return true;
    }

    private static ScriptReadModelSchemaSpec BuildSchemaSpec(ScriptReadModelDefinition definition)
    {
        var spec = new ScriptReadModelSchemaSpec
        {
            SchemaId = definition.SchemaId ?? string.Empty,
            SchemaVersion = definition.SchemaVersion ?? string.Empty,
        };
        if (definition.Fields != null)
        {
            foreach (var field in definition.Fields)
            {
                spec.Fields.Add(new ScriptReadModelFieldSpec
                {
                    Name = field.Name ?? string.Empty,
                    Type = field.Type ?? string.Empty,
                    Path = field.Path ?? string.Empty,
                    Nullable = field.Nullable,
                });
            }
        }

        if (definition.Indexes != null)
        {
            foreach (var index in definition.Indexes)
            {
                var indexSpec = new ScriptReadModelIndexSpec
                {
                    Name = index.Name ?? string.Empty,
                    Unique = index.Unique,
                    Provider = index.Provider ?? string.Empty,
                };
                if (index.Paths != null)
                    indexSpec.Paths.Add(index.Paths.Where(x => !string.IsNullOrWhiteSpace(x)));
                spec.Indexes.Add(indexSpec);
            }
        }

        if (definition.Relations != null)
        {
            foreach (var relation in definition.Relations)
            {
                spec.Relations.Add(new ScriptReadModelRelationSpec
                {
                    Name = relation.Name ?? string.Empty,
                    SourcePath = relation.SourcePath ?? string.Empty,
                    TargetSchemaId = relation.TargetSchemaId ?? string.Empty,
                    TargetPath = relation.TargetPath ?? string.Empty,
                    Cardinality = relation.Cardinality ?? string.Empty,
                    Provider = relation.Provider ?? string.Empty,
                });
            }
        }

        return spec;
    }

    private static string ComputeSha256(byte[] content)
    {
        var bytes = SHA256.HashData(content ?? Array.Empty<byte>());
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IReadOnlyList<string> ResolveStoreCapabilities(
        IReadOnlyList<ScriptReadModelIndexDefinition> indexes,
        IReadOnlyList<ScriptReadModelRelationDefinition> relations,
        IReadOnlyList<string> declaredCapabilities)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declared in declaredCapabilities)
        {
            if (!string.IsNullOrWhiteSpace(declared))
                providers.Add(declared.Trim());
        }

        foreach (var index in indexes)
        {
            if (!string.IsNullOrWhiteSpace(index.Provider))
                providers.Add(index.Provider.Trim());
        }

        foreach (var relation in relations)
        {
            if (!string.IsNullOrWhiteSpace(relation.Provider))
                providers.Add(relation.Provider.Trim());
        }

        return providers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal sealed record ScriptReadModelDefinitionExtraction(
    ScriptReadModelDefinition Definition,
    Any SchemaPayload,
    string SchemaHash,
    string SchemaVersion,
    IReadOnlyList<string> StoreCapabilities)
{
    public static ScriptReadModelDefinitionExtraction Empty { get; } = new(
        new ScriptReadModelDefinition(
            string.Empty,
            string.Empty,
            Array.Empty<ScriptReadModelFieldDefinition>(),
            Array.Empty<ScriptReadModelIndexDefinition>(),
            Array.Empty<ScriptReadModelRelationDefinition>()),
        Any.Pack(new Empty()),
        string.Empty,
        string.Empty,
        Array.Empty<string>());
}
