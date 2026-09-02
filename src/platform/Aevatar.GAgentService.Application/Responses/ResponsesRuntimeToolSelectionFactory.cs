using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Application.Responses;

/// <summary>
/// Single builder for the durable <see cref="LlmSessionRuntimeToolSelection"/> written into an
/// <c>LlmRunRequested</c> command. Shared by all three OpenAI-compatible ingress facades
/// (responses / messages / chat-completions) so the persisted tool-selection shape — including the
/// route <c>tool_set_name</c> the off-grain run executor re-resolves against — cannot drift between
/// ingresses (was previously copy-pasted three times).
/// </summary>
internal static class ResponsesRuntimeToolSelectionFactory
{
    public static LlmSessionRuntimeToolSelection Create(
        ResponsesToolClassification classification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        string? routeToolSetName)
    {
        var selection = new LlmSessionRuntimeToolSelection
        {
            SubstitutedToolNames = { classification.SubstitutedToolNames },
            AdditiveToolNames = { classification.AdditiveToolNames },
            OwnedToolNames = { classification.OwnedToolNames },
        };

        // Route tool set the facade classified against. The off-grain LlmRunCore re-resolves this
        // name from IToolSetRegistry to re-materialize the same sources; empty = always-on DI
        // providers only. Without it the run drops every route-tool-set tool (nyxid_*, invoke_*, ...).
        if (!string.IsNullOrWhiteSpace(routeToolSetName))
            selection.ToolSetName = routeToolSetName;

        if (!toolChoiceHintPlan.IsEmpty)
        {
            selection.ToolChoiceHintName = toolChoiceHintPlan.ToolName;
            selection.ToolChoiceHintArgumentsJson = toolChoiceHintPlan.PrefilledArgumentsJson();
            selection.ToolChoiceHintArguments = toolChoiceHintPlan.PrefilledArgumentsStruct();
        }

        selection.ForwardedTools.AddRange(classification.ForwardedTools.Select(static tool =>
            new LlmSessionRuntimeToolDeclaration
            {
                ToolName = tool.Name,
                Description = tool.Description,
                ParametersJson = tool.ParametersJson,
                Parameters = ResponsesProtoPayloads.ParseStruct(tool.ParametersJson),
                SchemaHash = tool.SchemaHash,
            }));

        return selection;
    }
}
