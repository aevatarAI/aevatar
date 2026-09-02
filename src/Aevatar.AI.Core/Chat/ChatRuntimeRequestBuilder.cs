using System.Collections.Frozen;
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
        if (effectiveToolContext.Request.IssuedAtUnixMs <= 0)
        {
            effectiveToolContext = effectiveToolContext with
            {
                Request = effectiveToolContext.Request with
                {
                    IssuedAtUnixMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                },
            };
        }
        if (baseRequest.ToolContext?.ExecutionOwner is { } executionOwner &&
            executionOwner.Kind != AgentToolExecutionOwnerKind.Unspecified &&
            !string.IsNullOrWhiteSpace(executionOwner.OwnerId))
        {
            effectiveToolContext = effectiveToolContext with
            {
                ExecutionOwner = executionOwner.Clone(),
            };
        }
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
            Tools = FilterVisibleTools(
                MergeExactTools(baseRequest.Tools, turnCatalog?.RouteOwnedTools.Values),
                effectiveToolContext.ToolVisibility),
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
            request.Tools ?? [],
            (request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request)).ToolVisibility);
    }

    private static IReadOnlyList<IAgentTool>? MergeExactTools(
        IEnumerable<IAgentTool>? baseTools,
        IEnumerable<IAgentTool>? routeOwnedTools)
    {
        var merged = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in (baseTools ?? []).Concat(routeOwnedTools ?? []))
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
                continue;

            var name = tool.Name.Trim();
            if (collisions.Contains(name))
                continue;
            if (!merged.TryGetValue(name, out var existing))
            {
                merged.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            merged.Remove(name);
            collisions.Add(name);
        }

        return merged.Count == 0 ? null : merged.Values.ToArray();
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
        private readonly IReadOnlyDictionary<string, IAgentTool> _schemaTools;
        private readonly AgentToolVisibilityScope _toolVisibility;

        public AuthorizationFence(
            IEnumerable<IAgentTool> schemaTools,
            AgentToolVisibilityScope toolVisibility)
        {
            _schemaTools = FreezeExactTools(schemaTools);
            _toolVisibility = FreezeVisibility(toolVisibility);
        }

        public LLMRequest Apply(LLMRequest request, bool forceCopy = false)
        {
            ArgumentNullException.ThrowIfNull(request);
            var toolContext = request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request);
            var visibility = CopyVisibility(IntersectVisibility(toolContext.ToolVisibility, _toolVisibility));
            var tools = ApplyExactTools(request.Tools, visibility);
            var toolsWereAttenuated = (request.Tools?.Count ?? 0) != (tools?.Count ?? 0);
            var visibilityWasAttenuated = !HasSameVisibility(toolContext.ToolVisibility, visibility);
            if (!forceCopy &&
                !toolsWereAttenuated &&
                !visibilityWasAttenuated &&
                ReferenceEquals(request.ToolContext, toolContext))
            {
                return request;
            }

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

        private IReadOnlyList<IAgentTool>? ApplyExactTools(
            IReadOnlyList<IAgentTool>? requestTools,
            AgentToolVisibilityScope visibility)
        {
            if (requestTools is not { Count: > 0 })
                return null;

            var accepted = new List<IAgentTool>();
            foreach (var group in requestTools
                         .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                         .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (!_schemaTools.TryGetValue(group.Key, out var exact) ||
                    !visibility.Allows(group.Key) ||
                    group.Any(tool => !ReferenceEquals(tool, exact)))
                {
                    continue;
                }

                accepted.Add(exact);
            }

            return accepted.Count == 0 ? null : accepted;
        }

        private static IReadOnlyDictionary<string, IAgentTool> FreezeExactTools(IEnumerable<IAgentTool> tools)
        {
            var exact = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools.Where(static tool => !string.IsNullOrWhiteSpace(tool.Name)))
            {
                var name = tool.Name.Trim();
                if (collisions.Contains(name))
                    continue;
                if (!exact.TryGetValue(name, out var existing))
                {
                    exact.Add(name, tool);
                    continue;
                }
                if (ReferenceEquals(existing, tool))
                    continue;

                exact.Remove(name);
                collisions.Add(name);
            }

            return exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        private static AgentToolVisibilityScope FreezeVisibility(AgentToolVisibilityScope visibility) =>
            visibility.IsRestricted
                ? new AgentToolVisibilityScope(
                    visibility.AllowedToolNames!.ToFrozenSet(StringComparer.OrdinalIgnoreCase))
                : AgentToolVisibilityScope.Unrestricted;

        private static AgentToolVisibilityScope CopyVisibility(AgentToolVisibilityScope visibility) =>
            visibility.IsRestricted
                ? AgentToolVisibilityScope.FromAllowedToolNames(visibility.AllowedToolNames)
                : AgentToolVisibilityScope.Unrestricted;

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
