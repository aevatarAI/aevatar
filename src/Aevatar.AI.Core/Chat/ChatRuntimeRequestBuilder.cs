using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;

namespace Aevatar.AI.Core.Chat;

internal static class ChatRuntimeRequestBuilder
{
    public static LLMRequest Build(
        LLMRequest baseRequest,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentProfileTurnCatalog? turnCatalog)
    {
        ArgumentNullException.ThrowIfNull(baseRequest);

        var mergedMetadata = MergeMetadata(baseRequest.Metadata, metadata);
        var effectiveLlmControl = llmControl ?? baseRequest.LlmControl;
        var effectiveToolContext = toolContext is null
            ? AgentToolExecutionContextMapper.FromRequest(baseRequest)
            : AgentToolExecutionContextMapper.MergeExternalMetadata(toolContext, mergedMetadata);
        effectiveToolContext = effectiveLlmControl?.ToToolContext(effectiveToolContext) ?? effectiveToolContext;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            effectiveToolContext = effectiveToolContext with
            {
                Request = effectiveToolContext.Request with { RequestId = requestId.Trim() },
            };
        }

        if (turnCatalog is not null)
        {
            effectiveToolContext = effectiveToolContext with
            {
                ToolVisibility = IntersectVisibility(
                    effectiveToolContext.ToolVisibility,
                    turnCatalog.FinalAllowedToolNames),
            };
        }

        return new LLMRequest
        {
            Messages = baseRequest.Messages,
            RequestId = string.IsNullOrWhiteSpace(requestId) ? baseRequest.RequestId : requestId.Trim(),
            Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(mergedMetadata),
            CallerContext = baseRequest.CallerContext,
            ToolContext = effectiveToolContext,
            RoutingContext = effectiveLlmControl?.ToRoutingContext(baseRequest.RoutingContext) ?? baseRequest.RoutingContext,
            LlmControl = effectiveLlmControl,
            Tools = FilterVisibleTools(baseRequest.Tools, effectiveToolContext.ToolVisibility),
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            ResponseFormat = baseRequest.ResponseFormat,
        };
    }

    private static AgentToolVisibilityScope IntersectVisibility(
        AgentToolVisibilityScope existing,
        IReadOnlySet<string> profileAllowedNames)
    {
        if (!existing.IsRestricted)
            return AgentToolVisibilityScope.FromAllowedToolNames(profileAllowedNames);

        return AgentToolVisibilityScope.FromAllowedToolNames(
            profileAllowedNames.Where(existing.Allows));
    }

    private static IReadOnlyList<IAgentTool>? FilterVisibleTools(
        IReadOnlyList<IAgentTool>? tools,
        AgentToolVisibilityScope visibility)
    {
        if (tools is not { Count: > 0 })
            return null;

        if (!visibility.IsRestricted)
            return tools;

        var visibleTools = tools.Where(tool => visibility.Allows(tool.Name)).ToList();
        return visibleTools.Count > 0 ? visibleTools : null;
    }

    private static IReadOnlyDictionary<string, string>? MergeMetadata(
        IReadOnlyDictionary<string, string>? baseMetadata,
        IReadOnlyDictionary<string, string>? overrideMetadata)
    {
        if ((baseMetadata is null || baseMetadata.Count == 0) &&
            (overrideMetadata is null || overrideMetadata.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseMetadata is not null)
        {
            foreach (var pair in baseMetadata)
                merged[pair.Key] = pair.Value;
        }

        if (overrideMetadata is not null)
        {
            foreach (var pair in overrideMetadata)
                merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
