// ─────────────────────────────────────────────────────────────
// AgentToolAIFunction — bridges IAgentTool to MEAI AIFunction
//
// Exposes the tool's real ParametersSchema (with slug, path, etc.)
// instead of wrapping in a single (string input) delegate that
// collapses all parameters into one string argument.
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.AI;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// Custom <see cref="AIFunction"/> that preserves the tool's real JSON schema
/// so the LLM sees individual parameters (slug, path, method, body, etc.)
/// instead of a single "input" string parameter.
/// </summary>
internal sealed class AgentToolAIFunction : AIFunction
{
    private readonly IAgentTool _tool;
    private readonly JsonElement _jsonSchema;

    public AgentToolAIFunction(IAgentTool tool)
    {
        _tool = tool;
        _jsonSchema = ParseSchema(tool.ParametersSchema);
    }

    public override string Name => _tool.Name;
    public override string Description => _tool.Description;
    public override JsonElement JsonSchema => _jsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Serialize the arguments dictionary back to JSON for ExecuteAsync.
        var argsJson = arguments.Count > 0
            ? JsonSerializer.Serialize(arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value))
            : "{}";

        return await _tool.ExecuteAsync(argsJson, cancellationToken);
    }

    private static JsonElement ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return EmptyObjectSchema();

        try
        {
            using var doc = JsonDocument.Parse(schema);
            return doc.RootElement.Clone();
        }
        catch
        {
            return EmptyObjectSchema();
        }
    }

    private static JsonElement EmptyObjectSchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return doc.RootElement.Clone();
    }
}
