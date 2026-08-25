using System.Collections.Frozen;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Chat;

internal static class ChatRuntimeRequestBuilder
{
    public static LLMRequest Build(
        LLMRequest baseRequest,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentTurnToolCatalog? turnCatalog)
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

        AgentTurnToolCatalog? effectiveCatalog = null;
        IReadOnlyList<IAgentTool>? exactTools;
        AgentTurnToolCatalogProof? catalogProof;
        if (turnCatalog is not null)
        {
            effectiveCatalog = turnCatalog
                .BindFinalExactTools(baseRequest.Tools ?? [])
                .NarrowToAllowedToolNames(turnCatalog.FinalAllowedToolNames.Where(
                    effectiveToolContext.ToolVisibility.Allows));
            effectiveToolContext = effectiveToolContext with
            {
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                    effectiveCatalog.FinalAllowedToolNames),
            };
            exactTools = effectiveCatalog.Proof.ToolDescriptors
                .Select(descriptor => effectiveCatalog.ExactTools[descriptor.Name])
                .ToArray();
            catalogProof = effectiveCatalog.Proof;
        }
        else
        {
            exactTools = FilterVisibleTools(
                MergeExactTools(baseRequest.Tools),
                effectiveToolContext.ToolVisibility);
            catalogProof = baseRequest.ToolCatalogProof;
            catalogProof?.AssertMatchesExactTools(exactTools ?? []);
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
            RouteTarget = baseRequest.RouteTarget?.Clone(),
            Tools = exactTools is { Count: > 0 } ? exactTools : null,
            ToolCatalogProof = catalogProof,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            AllowMultipleToolCalls = baseRequest.AllowMultipleToolCalls,
            ResponseFormat = baseRequest.ResponseFormat,
        };
    }

    internal static AuthorizationFence CaptureAuthorizationFence(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AuthorizationFence(
            request.Tools ?? [],
            (request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request)).ToolVisibility,
            request.ToolCatalogProof);
    }

    private static IReadOnlyList<IAgentTool>? MergeExactTools(
        IEnumerable<IAgentTool>? tools)
    {
        var merged = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                    AgentTurnToolCatalogFailureCode.InvalidToolName,
                    "A model-visible tool must have a non-empty name."));
            }

            var name = tool.Name.Trim();
            if (!merged.TryGetValue(name, out var existing))
            {
                merged.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.ToolNameCollision,
                $"Model-visible tool name '{name}' resolves to different exact objects.",
                name));
        }

        return merged.Count == 0
            ? null
            : merged.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value)
                .ToArray();
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
        private readonly AgentTurnToolCatalogProof? _catalogProof;

        public AuthorizationFence(
            IEnumerable<IAgentTool> schemaTools,
            AgentToolVisibilityScope toolVisibility,
            AgentTurnToolCatalogProof? catalogProof)
        {
            _schemaTools = FreezeExactTools(schemaTools);
            _toolVisibility = FreezeVisibility(toolVisibility);
            _catalogProof = catalogProof;
            _catalogProof?.AssertMatchesExactTools(_schemaTools.Values);
        }

        public LLMRequest Apply(LLMRequest request, bool forceCopy = false)
        {
            ArgumentNullException.ThrowIfNull(request);
            var toolContext = request.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(request);
            var immutableTools = ApplyExactTools(_toolVisibility);
            AgentToolVisibilityScope visibility;
            IReadOnlyList<IAgentTool>? tools;
            if (_catalogProof is not null)
            {
                if (!HasSameExactTools(request.Tools, immutableTools))
                {
                    throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                        AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
                        "An LLM extension attempted to mutate the frozen turn tool catalog."));
                }

                visibility = CopyVisibility(_toolVisibility);
                tools = immutableTools;
                _catalogProof.AssertMatchesExactTools(tools ?? []);
            }
            else
            {
                visibility = CopyVisibility(IntersectVisibility(toolContext.ToolVisibility, _toolVisibility));
                tools = ApplyLegacyExactTools(request.Tools, visibility);
            }
            var toolsWereAttenuated = !HasSameExactTools(request.Tools, tools);
            var visibilityWasAttenuated = !HasSameVisibility(toolContext.ToolVisibility, visibility);
            var proofWasReplaced = !ReferenceEquals(request.ToolCatalogProof, _catalogProof);
            if (!forceCopy &&
                !toolsWereAttenuated &&
                !visibilityWasAttenuated &&
                !proofWasReplaced &&
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
                RouteTarget = request.RouteTarget?.Clone(),
                Tools = tools is { Count: > 0 } ? tools : null,
                ToolCatalogProof = _catalogProof,
                Model = request.Model,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                AllowMultipleToolCalls = request.AllowMultipleToolCalls,
                ResponseFormat = request.ResponseFormat,
            };
        }

        private IReadOnlyList<IAgentTool>? ApplyExactTools(AgentToolVisibilityScope visibility)
        {
            if (_schemaTools.Count == 0)
                return null;

            var accepted = _schemaTools
                .Where(pair => visibility.Allows(pair.Key))
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value)
                .ToArray();

            return accepted.Length == 0 ? null : accepted;
        }

        private IReadOnlyList<IAgentTool>? ApplyLegacyExactTools(
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
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                        AgentTurnToolCatalogFailureCode.InvalidToolName,
                        "A frozen authorization fence tool must have a non-empty name."));
                }
                var name = tool.Name.Trim();
                if (!exact.TryGetValue(name, out var existing))
                {
                    exact.Add(name, tool);
                    continue;
                }
                if (ReferenceEquals(existing, tool))
                    continue;

                throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                    AgentTurnToolCatalogFailureCode.ToolNameCollision,
                    $"Frozen authorization fence tool name '{name}' resolves to different exact objects.",
                    name));
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

        private static bool HasSameVisibility(
            AgentToolVisibilityScope left,
            AgentToolVisibilityScope right)
        {
            if (left.IsRestricted != right.IsRestricted)
                return false;

            return !left.IsRestricted || left.AllowedToolNames!.SetEquals(right.AllowedToolNames!);
        }

        private static bool HasSameExactTools(
            IReadOnlyList<IAgentTool>? left,
            IReadOnlyList<IAgentTool>? right)
        {
            if ((left?.Count ?? 0) != (right?.Count ?? 0))
                return false;
            if (left is null || right is null)
                return true;

            return left.Zip(right).All(static pair => ReferenceEquals(pair.First, pair.Second));
        }
    }
}
