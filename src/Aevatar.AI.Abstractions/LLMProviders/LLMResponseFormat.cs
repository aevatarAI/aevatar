// ─────────────────────────────────────────────────────────────
// LLMResponseFormat — structured output constraints
//
// Three modes: Text (default free text), JsonObject (JSON mode),
// and JsonSchema (strict JSON with schema constraints).
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>LLM response format constraints.</summary>
public class LLMResponseFormat
{
    /// <summary>Free text (default).</summary>
    public static LLMResponseFormat Text { get; } = new() { Kind = LLMResponseFormatKind.Text };

    /// <summary>JSON mode without a schema.</summary>
    public static LLMResponseFormat JsonObject { get; } = new() { Kind = LLMResponseFormatKind.JsonObject };

    /// <summary>Strict JSON constrained by a JSON Schema.</summary>
    public static LLMResponseFormat ForJsonSchema(
        JsonElement schema,
        string? schemaName = null,
        string? schemaDescription = null) =>
        new LLMResponseFormatJsonSchema(schema, schemaName, schemaDescription);

    /// <summary>Automatically generates JSON Schema constraints from a C# type.</summary>
    public static LLMResponseFormat ForJsonSchema<T>(
        string? schemaName = null,
        string? schemaDescription = null) =>
        new LLMResponseFormatJsonSchema(
            AgentToolSchemaGenerator.GenerateSchema<T>(),
            schemaName ?? SanitizeTypeName(typeof(T)),
            schemaDescription);

    /// <summary>The format kind.</summary>
    public LLMResponseFormatKind Kind { get; protected init; } = LLMResponseFormatKind.Text;

    /// <summary>Sanitizes a CLR type name into a provider-safe schema name.</summary>
    internal static string SanitizeTypeName(Type type)
    {
        var name = type.Name;
        // Remove generic suffixes such as `1, `2, etc.
        var idx = name.IndexOf('`');
        if (idx >= 0) name = name[..idx];
        // Replace non-alphanumeric characters
        return Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
    }
}

/// <summary>Response format kind enumeration.</summary>
public enum LLMResponseFormatKind
{
    /// <summary>Free text (default).</summary>
    Text = 0,

    /// <summary>JSON mode without schema constraints.</summary>
    JsonObject = 1,

    /// <summary>Strict JSON with a JSON Schema.</summary>
    JsonSchema = 2,
}

/// <summary>Strict JSON format constraints with a JSON Schema.</summary>
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

    /// <summary>The JSON Schema.</summary>
    public JsonElement Schema { get; }

    /// <summary>The schema name (required by some providers).</summary>
    public string? SchemaName { get; }

    /// <summary>The schema description.</summary>
    public string? SchemaDescription { get; }
}
