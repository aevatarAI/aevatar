// ─────────────────────────────────────────────────────────────
// AgentToolAIFunction — bridges IAgentTool to MEAI AIFunction
//
// Exposes the tool's real ParametersSchema (with slug, path, etc.)
// instead of wrapping in a single (string input) delegate that
// collapses all parameters into one string argument.
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly JsonElement _jsonSchema;

    public AgentToolAIFunction(IAgentTool tool, IAgentToolExecutionPort toolExecutionPort)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
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

        var ambientContext = AgentToolRequestContext.Current
            ?? throw new InvalidOperationException(
                "MEAI function invocation requires a stable tool execution context.");
        var invocationContext = FunctionInvokingChatClient.CurrentContext;
        var effectiveCallId = ResolveEffectiveCallId(ambientContext, invocationContext);
        var executionContext = string.Equals(effectiveCallId, ambientContext.Request.CallId, StringComparison.Ordinal)
            ? ambientContext
            : ambientContext.WithCallId(effectiveCallId);
        if (string.IsNullOrWhiteSpace(executionContext.Request.RequestId) ||
            string.IsNullOrWhiteSpace(executionContext.Request.CallId))
        {
            throw new InvalidOperationException(
                "MEAI function invocation requires stable request and function-call identities.");
        }
        if (executionContext.ExecutionOwner.Kind == AgentToolExecutionOwnerKind.Unspecified ||
            string.IsNullOrWhiteSpace(executionContext.ExecutionOwner.OwnerId))
        {
            throw new InvalidOperationException(
                "MEAI function invocation requires a stable execution owner.");
        }

        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                _tool,
                argsJson,
                executionContext,
                AgentToolApprovalContinuationMode.None,
                null),
            cancellationToken).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private static string? ResolveEffectiveCallId(
        AgentToolExecutionContext ambientContext,
        FunctionInvocationContext? invocationContext)
    {
        if (invocationContext is null)
            return ambientContext.Request.CallId;

        var invocationCallId = Normalize(invocationContext.CallContent?.CallId);
        if (invocationCallId is not null)
            return invocationCallId;

        var requestId = Normalize(ambientContext.Request.RequestId)
            ?? throw new InvalidOperationException(
                "MEAI function invocation requires stable request and function-call identities.");
        return $"meai-{requestId}-iteration-{invocationContext.Iteration}-function-{invocationContext.FunctionCallIndex}";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return EmptyObjectSchema();

        try
        {
            var node = JsonNode.Parse(schema);
            if (node is null)
                return EmptyObjectSchema();

            CoerceAdditionalPropertiesToBoolean(node);
            return JsonSerializer.SerializeToElement(node);
        }
        catch
        {
            return EmptyObjectSchema();
        }
    }

    // Microsoft.Extensions.AI's OpenAI adapter (OpenAIClientExtensions.ToOpenAIFunctionParameters)
    // reads a tool parameter schema's `additionalProperties` as a System.Boolean. A caller-supplied
    // tool (e.g. codex's) whose schema expresses a map type with `additionalProperties: { …schema… }`
    // then throws "The JSON value could not be converted to System.Boolean" and fails the whole turn.
    // OpenAI's tool schema only accepts a boolean there anyway, so recursively coerce any non-boolean
    // `additionalProperties` to `false` (the strict-compatible value) before the schema reaches MEAI.
    private static void CoerceAdditionalPropertiesToBoolean(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("additionalProperties", out var additionalProperties)
                    && additionalProperties?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    obj["additionalProperties"] = false;
                }

                foreach (var property in obj.ToArray())
                {
                    if (property.Value is not null)
                        CoerceAdditionalPropertiesToBoolean(property.Value);
                }

                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    if (item is not null)
                        CoerceAdditionalPropertiesToBoolean(item);
                }

                break;
        }
    }

    private static JsonElement EmptyObjectSchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return doc.RootElement.Clone();
    }
}
