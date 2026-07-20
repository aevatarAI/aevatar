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

    internal static AuthorizationFence CaptureAuthorizationFence(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AuthorizationFence(
            request.Tools?.Select(static tool => tool.Name) ?? [],
            (request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request)).ToolVisibility);
    }

    private static AgentToolVisibilityScope IntersectVisibility(
        AgentToolVisibilityScope existing,
        IReadOnlySet<string> profileAllowedNames)
        => IntersectVisibility(
            existing,
            AgentToolVisibilityScope.FromAllowedToolNames(profileAllowedNames));

    private static AgentToolVisibilityScope IntersectVisibility(
        AgentToolVisibilityScope existing,
        AgentToolVisibilityScope ceiling)
    {
        if (!existing.IsRestricted)
            return ceiling;
        if (!ceiling.IsRestricted)
            return existing;

        return AgentToolVisibilityScope.FromAllowedToolNames(
            ceiling.AllowedToolNames!.Where(existing.Allows));
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

    internal sealed class AuthorizationFence
    {
        private readonly IReadOnlySet<string> _schemaToolNames;
        private readonly AgentToolVisibilityScope _toolVisibility;

        public AuthorizationFence(
            IEnumerable<string> schemaToolNames,
            AgentToolVisibilityScope toolVisibility)
        {
            _schemaToolNames = new HashSet<string>(
                schemaToolNames
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            _toolVisibility = toolVisibility.IsRestricted
                ? AgentToolVisibilityScope.FromAllowedToolNames(toolVisibility.AllowedToolNames)
                : AgentToolVisibilityScope.Unrestricted;
        }

        public LLMRequest Apply(LLMRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var toolContext = request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request);
            var visibility = IntersectVisibility(toolContext.ToolVisibility, _toolVisibility);
            var tools = request.Tools?
                .Where(tool => _schemaToolNames.Contains(tool.Name.Trim()) && visibility.Allows(tool.Name))
                .ToList();
            var toolsWereAttenuated = (request.Tools?.Count ?? 0) != (tools?.Count ?? 0);
            var visibilityWasAttenuated = !HasSameVisibility(toolContext.ToolVisibility, visibility);
            if (!toolsWereAttenuated && !visibilityWasAttenuated && ReferenceEquals(request.ToolContext, toolContext))
                return request;

            return new LLMRequest
            {
                Messages = request.Messages,
                RequestId = request.RequestId,
                Metadata = request.Metadata,
                CallerContext = request.CallerContext,
                ToolContext = toolContext with { ToolVisibility = visibility },
                RoutingContext = request.RoutingContext,
                LlmControl = request.LlmControl,
                Tools = tools is { Count: > 0 } ? tools : null,
                Model = request.Model,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                ResponseFormat = request.ResponseFormat,
            };
        }

        private static bool HasSameVisibility(
            AgentToolVisibilityScope left,
            AgentToolVisibilityScope right)
        {
            if (left.IsRestricted != right.IsRestricted)
                return false;

            return !left.IsRestricted || left.AllowedToolNames!.SetEquals(right.AllowedToolNames!);
        }
    }
}
