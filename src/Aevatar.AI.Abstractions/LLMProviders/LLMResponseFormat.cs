// ─────────────────────────────────────────────────────────────
// LLMResponseFormat — 结构化输出约束
//
// 三种模式：Text（默认自由文本）、JsonObject（JSON 模式）、
// JsonSchema（带 schema 约束的严格 JSON）。
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>LLM 响应格式约束。</summary>
public class LLMResponseFormat
{
    /// <summary>自由文本（默认）。</summary>
    public static LLMResponseFormat Text { get; } = new() { Kind = LLMResponseFormatKind.Text };

    /// <summary>JSON 模式，不指定 schema。</summary>
    public static LLMResponseFormat JsonObject { get; } = new() { Kind = LLMResponseFormatKind.JsonObject };

    /// <summary>带 JSON Schema 约束的严格 JSON。</summary>
    public static LLMResponseFormat ForJsonSchema(
        JsonElement schema,
        string? schemaName = null,
        string? schemaDescription = null) =>
        new LLMResponseFormatJsonSchema(schema, schemaName, schemaDescription);

    /// <summary>从 C# 类型自动生成 JSON Schema 约束。</summary>
    public static LLMResponseFormat ForJsonSchema<T>(
        string? schemaName = null,
        string? schemaDescription = null) =>
        new LLMResponseFormatJsonSchema(
            AgentToolSchemaGenerator.GenerateSchema<T>(),
            schemaName ?? SanitizeTypeName(typeof(T)),
            schemaDescription);

    /// <summary>格式类型。</summary>
    public LLMResponseFormatKind Kind { get; protected init; } = LLMResponseFormatKind.Text;

    /// <summary>将 CLR 类型名清理为 provider 安全的 schema 名称。</summary>
    internal static string SanitizeTypeName(Type type)
    {
        var name = type.Name;
        // 移除泛型后缀如 `1, `2 etc.
        var idx = name.IndexOf('`');
        if (idx >= 0) name = name[..idx];
        // 替换非字母数字字符
        return Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
    }
}

/// <summary>响应格式类型枚举。</summary>
public enum LLMResponseFormatKind
{
    /// <summary>自由文本（默认）。</summary>
    Text = 0,

    /// <summary>JSON 模式，无 schema 约束。</summary>
    JsonObject = 1,

    /// <summary>带 JSON Schema 的严格 JSON。</summary>
    JsonSchema = 2,
}

/// <summary>带 JSON Schema 的严格 JSON 格式约束。</summary>
public sealed class LLMResponseFormatJsonSchema : LLMResponseFormat
{
    public LLMResponseFormatJsonSchema(
        JsonElement schema,
        string? schemaName = null,
        string? schemaDescription = null)
    {
        Kind = LLMResponseFormatKind.JsonSchema;
        // Clone to decouple from caller's JsonDocument lifetime
        Schema = schema.Clone();
        SchemaName = schemaName;
        SchemaDescription = schemaDescription;
    }

    /// <summary>JSON Schema。</summary>
    public JsonElement Schema { get; }

    /// <summary>Schema 名称（某些 provider 需要）。</summary>
    public string? SchemaName { get; }

    /// <summary>Schema 描述。</summary>
    public string? SchemaDescription { get; }
}
