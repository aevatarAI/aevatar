// ─────────────────────────────────────────────────────────────
// AgentToolSchemaGenerator — 从 C# 类型自动生成 JSON Schema
//
// 消除手写 ParametersSchema 的维护负担。
// 使用 System.Text.Json.Schema.JsonSchemaExporter（.NET 9+）。
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// 从 C# 类型自动生成 JSON Schema，用于 <see cref="IAgentTool.ParametersSchema"/>
/// 和 <see cref="LLMProviders.LLMResponseFormat"/> 的 schema 生成。
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

    /// <summary>从类型参数生成 JSON Schema 字符串。</summary>
    public static string GenerateSchemaString<TParams>() =>
        GenerateSchemaString(typeof(TParams));

    /// <summary>从 Type 生成 JSON Schema 字符串（结果按类型缓存）。</summary>
    public static string GenerateSchemaString(Type paramsType) =>
        StringCache.GetOrAdd(paramsType, static type =>
        {
            var node = GenerateSchemaNode(type);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        });

    /// <summary>从类型参数生成 JSON Schema JsonElement。</summary>
    public static JsonElement GenerateSchema<TParams>() =>
        GenerateSchema(typeof(TParams));

    /// <summary>从 Type 生成 JSON Schema JsonElement（结果按类型缓存）。</summary>
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
