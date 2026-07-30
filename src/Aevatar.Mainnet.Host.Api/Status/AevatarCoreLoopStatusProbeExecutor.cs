using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.Executors;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Mainnet.Host.Api.Status;

internal sealed class AevatarCoreLoopStatusProbeExecutor : IHealthProbeExecutor
{
    private static readonly string[] RequiredToolNames =
    [
        "aevatar_invoke_gagent",
        "aevatar_invoke_team",
        "aevatar_invoke_member",
        "aevatar_start_workflow",
        "aevatar_observe_run",
        "aevatar_read_workflow_run_artifact",
    ];

    private readonly IToolSetRegistry _toolSetRegistry;

    public AevatarCoreLoopStatusProbeExecutor(IToolSetRegistry toolSetRegistry)
    {
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
    }

    public string Kind => "aevatar_core_loop";

    public async Task<HealthProbeOutcome> ProbeAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var toolSetName = ReadParam(descriptor, "ToolSet") ?? ToolSetNames.WorkspaceDefault;
        ToolSetResolveResult toolSet;
        try
        {
            toolSet = _toolSetRegistry.Resolve(toolSetName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure("tool_set_resolution_failed", ex.Message);
        }

        if (!toolSet.IsSuccess)
        {
            return Failure(
                toolSet.Error?.Code ?? "tool_set_unavailable",
                toolSet.Error?.Message ?? $"Tool set '{toolSetName}' is not registered.");
        }

        var requireWorkspaceSources = ReadBoolParam(descriptor, "RequireWorkspaceSources", fallback: true);
        if (requireWorkspaceSources)
        {
            var sourceCheck = VerifyWorkspaceSources(toolSet.Sources);
            if (sourceCheck is not null)
                return sourceCheck;
        }

        var candidateSources = requireWorkspaceSources
            ? toolSet.Sources.Where(IsRequiredInvocationSource).ToArray()
            : toolSet.Sources;
        var discovered = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var source in candidateSources)
        {
            IReadOnlyList<IAgentTool> tools;
            try
            {
                tools = await source.DiscoverToolsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failure(
                    "tool_discovery_failed",
                    $"Tool source '{source.GetType().Name}' failed discovery: {ex.Message}");
            }

            foreach (var tool in tools)
            {
                if (RequiredToolNames.Contains(tool.Name, StringComparer.Ordinal))
                    discovered.TryAdd(tool.Name, tool);
            }
        }

        var missingTools = RequiredToolNames
            .Where(tool => !discovered.ContainsKey(tool))
            .ToArray();
        if (missingTools.Length > 0)
        {
            return Failure(
                "missing_invocation_tools",
                $"Tool set '{toolSet.Name}' is missing: {string.Join(", ", missingTools)}.");
        }

        foreach (var toolName in RequiredToolNames)
        {
            var tool = discovered[toolName];
            if (string.IsNullOrWhiteSpace(tool.Description))
            {
                return Failure(
                    "tool_description_missing",
                    $"Tool '{toolName}' has an empty description.");
            }

            if (string.IsNullOrWhiteSpace(tool.ParametersSchema))
            {
                return Failure(
                    "tool_schema_missing",
                    $"Tool '{toolName}' has an empty parameters schema.");
            }

            if (tool is not IAevatarInvocationTool)
            {
                return Failure(
                    "tool_contract_mismatch",
                    $"Tool '{toolName}' is not an IAevatarInvocationTool.");
            }
        }

        var routeCheck = VerifyRouteMigration();
        if (routeCheck is not null)
            return routeCheck;

        var completionCheck = VerifyCompletionObservationTools(discovered);
        if (completionCheck is not null)
            return completionCheck;

        return new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = $"core_loop_tools_{RequiredToolNames.Length}",
        };
    }

    private static HealthProbeOutcome? VerifyWorkspaceSources(IReadOnlyList<IAgentToolSource> sources)
    {
        var requiredSourceTypes = new HashSet<System.Type>
        {
            typeof(InvokeGAgentToolSource),
            typeof(InvokeTeamToolSource),
            typeof(InvokeMemberToolSource),
            typeof(StartWorkflowToolSource),
            typeof(ObserveRunToolSource),
            typeof(ReadWorkflowRunArtifactToolSource),
        };

        foreach (var source in sources)
            requiredSourceTypes.Remove(source.GetType());

        if (requiredSourceTypes.Count == 0)
            return null;

        return Failure(
            "missing_invocation_tool_sources",
            $"Workspace tool set is missing sources: {string.Join(", ", requiredSourceTypes.Select(static t => t.Name))}.");
    }

    private static HealthProbeOutcome? VerifyRouteMigration()
    {
        var gagentHint = new ChatRouteToolChoiceHint
        {
            ToolName = "aevatar_invoke_gagent",
            PrefilledArguments = new Struct
            {
                Fields =
                {
                    ["actor_id"] = Value.ForString("status-agent"),
                },
            },
        };
        var teamHint = new ChatRouteToolChoiceHint
        {
            ToolName = "aevatar_invoke_team",
            PrefilledArguments = new Struct
            {
                Fields =
                {
                    ["team_id"] = Value.ForString("status-team"),
                    ["endpoint_id"] = Value.ForString("chat"),
                },
            },
        };
        var workflowHint = new ChatRouteToolChoiceHint
        {
            ToolName = "aevatar_start_workflow",
            PrefilledArguments = new Struct
            {
                Fields =
                {
                    ["workflow_id"] = Value.ForString("status-workflow"),
                },
            },
        };
        var snapshot = new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = "status-probe-model" },
            },
            [
                new ChatRouteRule
                {
                    RuleId = "status-gagent",
                    Priority = 1,
                    Match = new ChatRouteMatch { CommandName = "/agent" },
                    Action = new ChatRouteAction
                    {
                        ForwardToModel = new ForwardToModel
                        {
                            ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
                            ToolChoiceHint = gagentHint,
                        },
                    },
                },
                new ChatRouteRule
                {
                    RuleId = "status-team",
                    Priority = 2,
                    Match = new ChatRouteMatch { CommandName = "/team" },
                    Action = new ChatRouteAction
                    {
                        ForwardToModel = new ForwardToModel
                        {
                            ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
                            ToolChoiceHint = teamHint,
                        },
                    },
                },
                new ChatRouteRule
                {
                    RuleId = "status-workflow",
                    Priority = 3,
                    Match = new ChatRouteMatch { CommandName = "/workflow" },
                    Action = new ChatRouteAction
                    {
                        ForwardToModel = new ForwardToModel
                        {
                            ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
                            ToolChoiceHint = workflowHint,
                        },
                    },
                },
            ]);

        var cases = new[]
        {
            new RouteToolCase("/agent", "aevatar_invoke_gagent", "actor_id", "status-agent"),
            new RouteToolCase("/team", "aevatar_invoke_team", "team_id", "status-team"),
            new RouteToolCase("/workflow", "aevatar_start_workflow", "workflow_id", "status-workflow"),
        };

        foreach (var routeCase in cases)
        {
            var action = snapshot.Rules
                .FirstOrDefault(rule => string.Equals(
                    rule.Match?.CommandName,
                    routeCase.CommandName,
                    StringComparison.Ordinal))
                ?.Action;
            var hint = action?.ForwardToModel?.ToolChoiceHint;
            if (!string.Equals(hint?.ToolName, routeCase.ToolName, StringComparison.Ordinal))
            {
                return Failure(
                    "route_tool_hint_mismatch",
                    $"Route '{routeCase.CommandName}' resolved to '{hint?.ToolName ?? "<none>"}', expected '{routeCase.ToolName}'.");
            }

            if (hint is null)
            {
                return Failure(
                    "route_tool_hint_missing",
                    $"Route '{routeCase.CommandName}' did not resolve a tool choice hint.");
            }

            if (hint.PrefilledArguments.Fields.TryGetValue(routeCase.ArgumentName, out var value) &&
                string.Equals(value.StringValue, routeCase.ArgumentValue, StringComparison.Ordinal))
            {
                continue;
            }

            return Failure(
                "route_prefill_mismatch",
                $"Route '{routeCase.CommandName}' did not prefill '{routeCase.ArgumentName}'.");
        }

        return null;
    }

    private static bool IsRequiredInvocationSource(IAgentToolSource source) =>
        source is InvokeGAgentToolSource or
            InvokeTeamToolSource or
            InvokeMemberToolSource or
            StartWorkflowToolSource or
            ObserveRunToolSource or
            ReadWorkflowRunArtifactToolSource;

    private static HealthProbeOutcome? VerifyCompletionObservationTools(
        IReadOnlyDictionary<string, IAgentTool> discovered)
    {
        foreach (var toolName in RequiredToolNames)
        {
            if (!discovered.ContainsKey(toolName))
                return Failure(
                    "completion_tool_missing",
                    $"Tool '{toolName}' is not registered for accepted dispatch or readmodel observation.");
        }

        if (discovered["aevatar_observe_run"] is not IAevatarInvocationTool observeRun || !observeRun.IsReadOnly)
            return Failure(
                "completion_observe_tool_not_read_only",
                "aevatar_observe_run must remain a read-only readmodel observation tool.");

        if (discovered["aevatar_read_workflow_run_artifact"] is not IAevatarInvocationTool readWorkflowRunArtifact ||
            !readWorkflowRunArtifact.IsReadOnly)
            return Failure(
                "workflow_run_artifact_tool_not_read_only",
                "aevatar_read_workflow_run_artifact must remain a read-only workflow-run artifact query tool.");

        return null;
    }

    private static string? ReadParam(HealthProbeTargetDescriptor descriptor, string key)
    {
        foreach (var (k, v) in descriptor.Parameters)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return null;
    }

    private static bool ReadBoolParam(HealthProbeTargetDescriptor descriptor, string key, bool fallback)
    {
        var raw = ReadParam(descriptor, key);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private static HealthProbeOutcome Failure(string detail, string message) =>
        new()
        {
            Status = HealthOutcomeStatus.Down,
            Detail = detail,
            ErrorMessage = message,
        };

    private sealed record RouteToolCase(
        string CommandName,
        string ToolName,
        string ArgumentName,
        string ArgumentValue);
}
