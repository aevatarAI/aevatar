using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public sealed class InvokeGAgentToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeGAgentToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new InvokeGAgentTool(_dispatcher)]);
}

public sealed class InvokeTeamToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeTeamToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new InvokeTeamTool(_dispatcher)]);
}

public sealed class StartWorkflowToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public StartWorkflowToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new StartWorkflowTool(_dispatcher)]);
}

public sealed class ObserveRunToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public ObserveRunToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new ObserveRunTool(_dispatcher)]);
}

internal sealed class InvokeGAgentTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeGAgentTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_invoke_gagent";

    public string Description =>
        "Invoke a single Aevatar GAgent by actor_id or caller-scoped agent_kind with a typed chat payload.";

    public string ParametersSchema => AevatarInvocationToolSchemas.InvokeGAgent;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.InvokeGAgentAsync(argumentsJson, ct);
}

internal sealed class InvokeTeamTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeTeamTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_invoke_team";

    public string Description =>
        "Invoke a Studio team entry endpoint by team_id and endpoint_id with a typed chat payload.";

    public string ParametersSchema => AevatarInvocationToolSchemas.InvokeTeam;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.InvokeTeamAsync(argumentsJson, ct);
}

internal sealed class StartWorkflowTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public StartWorkflowTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_start_workflow";

    public string Description =>
        "Start an Aevatar workflow by workflow_id with typed inputs. " +
        "When use_skill returns inline workflow_yamls, pass that bundle in workflow_yamls instead of treating the YAMLs as ordinary text.";

    public string ParametersSchema => AevatarInvocationToolSchemas.StartWorkflow;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.StartWorkflowAsync(argumentsJson, ct);

    public string SideEffectKind => "workflow.managed-child-start";

    public AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string resultJson)
    {
        var workflowRuntime = AgentToolRequestContext.Current?.WorkflowRuntime ?? AgentWorkflowRuntimeContext.Empty;
        if (!workflowRuntime.HasManagedParent)
            return null;

        var invocation = ParseInvocationToolResult(resultJson);
        if (invocation == null || !IsAcceptedManagedWorkflowStart(invocation))
            return null;

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            SideEffectKind = SideEffectKind,
            ResultJson = resultJson ?? string.Empty,
            ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
            {
                ParentActorId = workflowRuntime.ParentActorId?.Trim() ?? string.Empty,
                ParentRunId = workflowRuntime.ParentRunId?.Trim() ?? string.Empty,
                ParentStepId = workflowRuntime.ParentStepId?.Trim() ?? string.Empty,
                InvocationId = invocation.RunId,
                ChildRunId = invocation.RunId,
                StreamTopic = invocation.StreamTopic,
            },
        };
    }

    private static ManagedWorkflowStartResult? ParseInvocationToolResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _))
                return null;

            return new ManagedWorkflowStartResult(
                ReadString(root, "run_id"),
                ReadString(root, "status"),
                ReadString(root, "actor_id"),
                ReadString(root, "stream_topic"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsAcceptedManagedWorkflowStart(ManagedWorkflowStartResult invocation) =>
        !string.IsNullOrWhiteSpace(invocation.RunId) &&
        !string.IsNullOrWhiteSpace(invocation.ActorId) &&
        string.Equals(invocation.Status, "accepted", StringComparison.Ordinal);

    private sealed record ManagedWorkflowStartResult(
        string RunId,
        string Status,
        string ActorId,
        string StreamTopic);
}

internal sealed class ObserveRunTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public ObserveRunTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_observe_run";

    public string Description =>
        "Observe a previously accepted Aevatar run through one explicitly selected readmodel target.";

    public string ParametersSchema => AevatarInvocationToolSchemas.ObserveRun;

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.ObserveRunAsync(argumentsJson, ct);
}
