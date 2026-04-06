// ─────────────────────────────────────────────────────────────
// AgentToolAIFunction — bridges IAgentTool to MEAI AIFunction
//
// Uses AIFunctionFactory.CreateDeclaration for the tool declaration
// (ensuring Name/Description/Schema are properly initialized for
// OpenAI SDK serialization), then wraps with a delegating AIFunction
// for actual invocation.
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.AI;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// Bridges <see cref="IAgentTool"/> to MEAI's <see cref="AIFunction"/>.
/// Uses <see cref="AIFunctionFactory.CreateDeclaration"/> to build a properly
/// initialized declaration, then wraps invocation to route through <see cref="IAgentTool.ExecuteAsync"/>.
/// </summary>
internal sealed class AgentToolAIFunction : AIFunction
{
    private readonly IAgentTool _tool;
    private readonly AIFunctionDeclaration _declaration;

    private AgentToolAIFunction(IAgentTool tool, AIFunctionDeclaration declaration)
    {
        _tool = tool;
        _declaration = declaration;
    }

    public override string Name => _declaration.Name;
    public override string Description => _declaration.Description;
    public override JsonElement JsonSchema => _declaration.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var argsJson = arguments.Count > 0
            ? JsonSerializer.Serialize(arguments.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value))
            : "{}";

        return await _tool.ExecuteAsync(argsJson, cancellationToken);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> from an <see cref="IAgentTool"/>.
    /// The underlying declaration is built via <see cref="AIFunctionFactory.CreateDeclaration"/>
    /// to ensure proper serialization of Name/Description/Schema by the OpenAI SDK.
    /// </summary>
    public static AIFunction CreateFrom(IAgentTool tool)
    {
        var schema = ParseSchema(tool.ParametersSchema);
        var declaration = AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            schema);
        return new AgentToolAIFunction(tool, declaration);
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
