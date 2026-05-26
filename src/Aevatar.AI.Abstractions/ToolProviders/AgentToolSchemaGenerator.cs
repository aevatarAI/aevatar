// ─────────────────────────────────────────────────────────────
// AgentToolSchemaGenerator — automatically generate JSON Schema from C# types
//
// Eliminates the maintenance burden of handwritten ParametersSchema.
// Uses System.Text.Json.Schema.JsonSchemaExporter (.NET 9+).
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Automatically generates JSON Schema from C# types for <see cref="IAgentTool.ParametersSchema"/>
/// and schema generation for <see cref="LLMProviders.LLMResponseFormat"/>.
/// </summary>
public static class AgentToolSchemaGenerator
{
    private static readonly JsonSerializerOptions SchemaSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSchemaExporterOptions SchemaExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
    };

    private static readonly ConcurrentDictionary<Type, string> StringCache = new();
    private static readonly ConcurrentDictionary<Type, JsonElement> ElementCache = new();

    /// <summary>Generates a JSON Schema string from the type parameter.</summary>
    public static string GenerateSchemaString<TParams>() =>
        GenerateSchemaString(typeof(TParams));

    /// <summary>Generates a JSON Schema string from a Type (results are cached by type).</summary>
    public static string GenerateSchemaString(Type paramsType) =>
        StringCache.GetOrAdd(paramsType, static type =>
        {
            var node = GenerateSchemaNode(type);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        });

    /// <summary>Generates a JSON Schema JsonElement from the type parameter.</summary>
    public static JsonElement GenerateSchema<TParams>() =>
        GenerateSchema(typeof(TParams));

    /// <summary>Generates a JSON Schema JsonElement from a Type (results are cached by type).</summary>
    public static JsonElement GenerateSchema(Type paramsType) =>
        ElementCache.GetOrAdd(paramsType, static type =>
        {
            var node = GenerateSchemaNode(type);
            return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
        });

    private static JsonNode GenerateSchemaNode(Type paramsType)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(
            SchemaSerializerOptions,
            paramsType,
            SchemaExporterOptions);

        // Ensure top-level is { "type": "object", ... }
        if (node is JsonObject obj && !obj.ContainsKey("type"))
        {
            obj["type"] = "object";
        }

        return node;
    }
}
