using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

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

public sealed class InvokeMemberToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeMemberToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new InvokeMemberTool(_dispatcher)]);
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

public sealed class ReadWorkflowRunArtifactToolSource : IAgentToolSource
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly IWorkflowRunBindingReader _runBindingReader;

    public ReadWorkflowRunArtifactToolSource(
        IWorkflowExecutionQueryApplicationService queryService,
        IWorkflowRunBindingReader runBindingReader)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _runBindingReader = runBindingReader ?? throw new ArgumentNullException(nameof(runBindingReader));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new ReadWorkflowRunArtifactTool(_queryService, _runBindingReader)]);
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

internal sealed class InvokeMemberTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeMemberTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_invoke_member";

    public string Description =>
        "Invoke a Studio member by member_id with a typed chat payload. endpoint_id is optional and defaults to chat, which is the standard Studio workflow member endpoint; pass endpoint_id only when a different published endpoint is explicitly known.";

    public string ParametersSchema => AevatarInvocationToolSchemas.InvokeMember;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.InvokeMemberAsync(argumentsJson, ct);
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
        "Start a mounted/imported Aevatar Scope Workflow by workflow_id with typed inputs. " +
        "Use inline workflow_yamls only as an explicit fallback when Scope Workflow mounting/import is unavailable; Ornn workflow YAMLs from use_skill are templates/import sources, not page-visible runnable workflow authority by themselves. " +
        "Use wait=stream only when the current surface can deliver or observe the workflow terminal result; channel bots without workflow result delivery should not start background-only runs.";

    public string ParametersSchema => AevatarInvocationToolSchemas.StartWorkflow;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.StartWorkflowAsync(argumentsJson, ct);

    public string SideEffectKind => "workflow.managed-child-start";

    public AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string resultJson)
    {
        var invocation = ParseInvocationToolResult(resultJson);
        if (invocation == null)
            return null;

        var workflowRuntime = AgentToolRequestContext.Current?.WorkflowRuntime ?? AgentWorkflowRuntimeContext.Empty;
        var receipt = new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            SideEffectKind = SideEffectKind,
            SubjectKind = AevatarInvocationReceiptJson.InvocationRunSubjectKind,
            SubjectId = invocation.RunId,
            ResultJson = resultJson ?? string.Empty,
        };
        if (workflowRuntime.HasManagedParent && IsAcceptedManagedWorkflowStart(invocation))
        {
            receipt.ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
            {
                ParentActorId = workflowRuntime.ParentActorId?.Trim() ?? string.Empty,
                ParentRunId = workflowRuntime.ParentRunId?.Trim() ?? string.Empty,
                ParentStepId = workflowRuntime.ParentStepId?.Trim() ?? string.Empty,
                InvocationId = invocation.RunId,
                ChildRunId = invocation.RunId,
                StreamTopic = invocation.StreamTopic,
            };
        }

        if (invocation.WorkflowRunDelivery is not null)
            receipt.WorkflowRunDelivery = invocation.WorkflowRunDelivery.Clone();

        return receipt.ManagedWorkflowHandoff is not null || receipt.WorkflowRunDelivery is not null
            ? receipt
            : null;
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
                ReadString(root, "stream_topic"),
                TryReadWorkflowRunDeliveryReceipt(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkflowRunBackgroundDeliveryReceipt? TryReadWorkflowRunDeliveryReceipt(JsonElement root)
    {
        if (!root.TryGetProperty("workflow_run_delivery", out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var deliveryActorId = ReadString(value, "delivery_actor_id");
        var workflowActorId = ReadString(value, "workflow_actor_id");
        var workflowCommandId = ReadString(value, "workflow_command_id");
        if (string.IsNullOrWhiteSpace(deliveryActorId) ||
            string.IsNullOrWhiteSpace(workflowActorId) ||
            string.IsNullOrWhiteSpace(workflowCommandId))
        {
            return null;
        }

        return new WorkflowRunBackgroundDeliveryReceipt
        {
            DeliveryActorId = deliveryActorId,
            WorkflowActorId = workflowActorId,
            WorkflowRunId = ReadString(value, "workflow_run_id"),
            WorkflowCommandId = workflowCommandId,
            WorkflowCorrelationId = ReadString(value, "workflow_correlation_id"),
            StreamTopic = ReadString(value, "stream_topic"),
            ChannelPlatform = ReadString(value, "channel_platform"),
            ReplyMessageId = ReadString(value, "reply_message_id"),
            PlatformMessageId = ReadString(value, "platform_message_id"),
            RegistrationScopeId = ReadString(value, "registration_scope_id"),
        };
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
        string StreamTopic,
        WorkflowRunBackgroundDeliveryReceipt? WorkflowRunDelivery);
}

internal sealed class ObserveRunTool : IAevatarInvocationReadOnlyTool
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

    public string ReadOnlySubjectIdPropertyName => "run_id";

    public IReadOnlyList<AevatarInvocationReceiptJson.ResultPropertyRequirement> ReadOnlyResultRequirements { get; } = new[]
    {
        AevatarInvocationReceiptJson.StringProperty("run_id"),
        AevatarInvocationReceiptJson.StringProperty("status"),
    };

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.ObserveRunAsync(argumentsJson, ct);
}

internal sealed class ReadWorkflowRunArtifactTool : IAevatarInvocationReadOnlyTool
{
    private const int DefaultTimelineTake = 50;
    private const int DefaultGraphTake = 200;
    private const int MaxTimelineTake = 200;
    private const int MaxGraphTake = 500;
    private const int DefaultGraphDepth = 2;
    private const int MaxGraphDepth = 5;
    private const int DefaultReportWaitMs = 8000;
    private const int MaxReportWaitMs = 20000;
    private static readonly TimeSpan ReportPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly IWorkflowRunBindingReader _runBindingReader;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public ReadWorkflowRunArtifactTool(
        IWorkflowExecutionQueryApplicationService queryService,
        IWorkflowRunBindingReader runBindingReader,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _runBindingReader = runBindingReader ?? throw new ArgumentNullException(nameof(runBindingReader));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public string Name => "aevatar_read_workflow_run_artifact";

    public string Description =>
        "Read a workflow run's projected artifact/export by workflow_run_id. " +
        "Use this after aevatar_start_workflow returns a run_id; long workflow actor IDs are also accepted. " +
        "For report reads, the tool waits briefly for projection materialization and returns pending if the artifact is not visible yet; do not infer the final workflow output from a pending result. " +
        "This tool reads workflow-run report/timeline/graph artifacts only and does not inspect live actor state.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "workflow_run_id": {
              "type": "string",
              "description": "Workflow run ID returned by aevatar_start_workflow"
            },
            "view": {
              "type": "string",
              "enum": ["report", "timeline", "graph_edges", "graph_subgraph"],
              "description": "Artifact/export view to read. Default: report"
            },
            "take": {
              "type": "integer",
              "description": "Maximum timeline or graph items to return"
            },
            "graph_depth": {
              "type": "integer",
              "description": "Graph traversal depth for graph_subgraph, max 5"
            },
            "wait_ms": {
              "type": "integer",
              "description": "For report reads, wait up to this many milliseconds for projection materialization. Default 8000, max 20000"
            },
            "edge_types": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional graph edge type filters"
            }
          },
          "required": ["workflow_run_id"]
        }
        """;

    public bool IsReadOnly => true;

    public string ReadOnlySubjectIdPropertyName => "workflow_run_id";

    public IReadOnlyList<AevatarInvocationReceiptJson.ResultPropertyRequirement> ReadOnlyResultRequirements { get; } = new[]
    {
        AevatarInvocationReceiptJson.StringProperty("workflow_run_id"),
        AevatarInvocationReceiptJson.StringProperty("artifact"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        WorkflowRunArtifactArguments args;
        try
        {
            args = WorkflowRunArtifactArguments.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return AevatarInvocationJson.Error(Error(
                "invalid_arguments",
                "arguments must be a JSON object."));
        }

        if (string.IsNullOrWhiteSpace(args.WorkflowRunId))
        {
            return AevatarInvocationJson.Error(Error(
                "invalid_arguments",
                "workflow_run_id is required.",
                "workflow_run_id"));
        }

        return args.View switch
        {
            "timeline" => await ReadTimelineAsync(args, await ResolveArtifactTargetAsync(args.WorkflowRunId, ct), ct),
            "graph_edges" => await ReadGraphEdgesAsync(args, await ResolveArtifactTargetAsync(args.WorkflowRunId, ct), ct),
            "graph_subgraph" => await ReadGraphSubgraphAsync(args, await ResolveArtifactTargetAsync(args.WorkflowRunId, ct), ct),
            _ => await ReadReportAsync(args, ct),
        };
    }

    private async Task<string> ReadReportAsync(
        WorkflowRunArtifactArguments args,
        CancellationToken ct)
    {
        var waitMs = Math.Clamp(args.WaitMs ?? DefaultReportWaitMs, 0, MaxReportWaitMs);
        var (report, artifactTarget) = await ReadReportForRunAsync(
            args.WorkflowRunId,
            TimeSpan.FromMilliseconds(waitMs),
            ct);
        if (report == null)
        {
            return AevatarInvocationJson.ToJson(new
            {
                workflow_run_id = args.WorkflowRunId,
                artifact_actor_id = EmptyToNull(artifactTarget.ArtifactWorkflowRunId),
                artifact = "report",
                status = "pending",
                pending = true,
                waited_ms = waitMs,
                retry_after_ms = 1000,
                message = "Workflow run report artifact is not materialized yet. Retry this tool instead of inferring the final workflow output.",
            });
        }

        return AevatarInvocationJson.ToJson(new
        {
            workflow_run_id = args.WorkflowRunId,
            artifact_actor_id = EmptyToNull(artifactTarget.ArtifactWorkflowRunId),
            artifact = "report",
            workflow_name = EmptyToNull(report.WorkflowName),
            status = report.CompletionStatus.ToString(),
            state_version = report.StateVersion,
            command_id = EmptyToNull(report.CommandId),
            root_actor_id = EmptyToNull(report.RootActorId),
            success = report.Success,
            started_at = ToIso(report.StartedAt),
            ended_at = ToIso(report.EndedAt),
            updated_at = ToIso(report.UpdatedAt),
            duration_ms = report.DurationMs,
            input = Truncate(report.Input, 500),
            final_output = Truncate(report.FinalOutput, 2000),
            final_error = EmptyToNull(report.FinalError),
            summary = new
            {
                total_steps = report.Summary.TotalSteps,
                requested_steps = report.Summary.RequestedSteps,
                completed_steps = report.Summary.CompletedSteps,
                role_reply_count = report.Summary.RoleReplyCount,
                step_type_counts = report.Summary.StepTypeCounts,
            },
            steps = report.Steps.Select(static step => new
            {
                step_id = EmptyToNull(step.StepId),
                step_type = EmptyToNull(step.StepType),
                target_role = EmptyToNull(step.TargetRole),
                success = step.Success,
                duration_ms = step.DurationMs,
                output_preview = Truncate(step.OutputPreview, 500),
                error = EmptyToNull(step.Error),
            }).ToArray(),
            role_replies = report.RoleReplies.Select(static reply => new
            {
                timestamp = ToIso(reply.Timestamp),
                role_id = EmptyToNull(reply.RoleId),
                content = Truncate(reply.Content, 1000),
                content_length = reply.ContentLength,
            }).ToArray(),
        });
    }

    private async Task<string> ReadTimelineAsync(
        WorkflowRunArtifactArguments args,
        WorkflowRunArtifactTarget artifactTarget,
        CancellationToken ct)
    {
        var take = Math.Clamp(args.Take ?? DefaultTimelineTake, 1, MaxTimelineTake);
        var timeline = await _queryService.ListWorkflowRunTimelineExportAsync(artifactTarget.ArtifactWorkflowRunId, take, ct);

        return AevatarInvocationJson.ToJson(new
        {
            workflow_run_id = args.WorkflowRunId,
            artifact_actor_id = EmptyToNull(artifactTarget.ArtifactWorkflowRunId),
            artifact = "timeline",
            events = timeline.Select(static item => new
            {
                timestamp = ToIso(item.Timestamp),
                stage = EmptyToNull(item.Stage),
                message = Truncate(item.Message, 500),
                agent_id = EmptyToNull(item.AgentId),
                step_id = EmptyToNull(item.StepId),
                step_type = EmptyToNull(item.StepType),
                event_type = EmptyToNull(item.EventType),
                data = item.Data.Count > 0 ? TruncateData(item.Data) : null,
            }).ToArray(),
            count = timeline.Count,
        });
    }

    private async Task<string> ReadGraphEdgesAsync(
        WorkflowRunArtifactArguments args,
        WorkflowRunArtifactTarget artifactTarget,
        CancellationToken ct)
    {
        var take = Math.Clamp(args.Take ?? DefaultGraphTake, 1, MaxGraphTake);
        var options = BuildGraphOptions(args);
        var edges = await _queryService.ListWorkflowRunGraphExportEdgesAsync(
            artifactTarget.ArtifactWorkflowRunId,
            take,
            options,
            ct);

        return AevatarInvocationJson.ToJson(new
        {
            workflow_run_id = args.WorkflowRunId,
            artifact_actor_id = EmptyToNull(artifactTarget.ArtifactWorkflowRunId),
            artifact = "graph_edges",
            edges = edges.Select(static edge => new
            {
                id = EmptyToNull(edge.EdgeId),
                from = EmptyToNull(edge.FromNodeId),
                to = EmptyToNull(edge.ToNodeId),
                type = EmptyToNull(edge.EdgeType),
                updated_at = ToIso(edge.UpdatedAt),
                properties = edge.Properties.Count > 0 ? edge.Properties : null,
            }).ToArray(),
            count = edges.Count,
        });
    }

    private async Task<string> ReadGraphSubgraphAsync(
        WorkflowRunArtifactArguments args,
        WorkflowRunArtifactTarget artifactTarget,
        CancellationToken ct)
    {
        var depth = Math.Clamp(args.GraphDepth ?? DefaultGraphDepth, 1, MaxGraphDepth);
        var take = Math.Clamp(args.Take ?? DefaultGraphTake, 1, MaxGraphTake);
        var options = BuildGraphOptions(args);
        var subgraph = await _queryService.GetWorkflowRunGraphExportSubgraphAsync(
            artifactTarget.ArtifactWorkflowRunId,
            depth,
            take,
            options,
            ct);

        return AevatarInvocationJson.ToJson(new
        {
            workflow_run_id = args.WorkflowRunId,
            artifact_actor_id = EmptyToNull(artifactTarget.ArtifactWorkflowRunId),
            artifact = "graph_subgraph",
            root = EmptyToNull(subgraph.RootNodeId),
            nodes = subgraph.Nodes.Select(static node => new
            {
                id = EmptyToNull(node.NodeId),
                type = EmptyToNull(node.NodeType),
                updated_at = ToIso(node.UpdatedAt),
                properties = node.Properties.Count > 0 ? node.Properties : null,
            }).ToArray(),
            edges = subgraph.Edges.Select(static edge => new
            {
                id = EmptyToNull(edge.EdgeId),
                from = EmptyToNull(edge.FromNodeId),
                to = EmptyToNull(edge.ToNodeId),
                type = EmptyToNull(edge.EdgeType),
                updated_at = ToIso(edge.UpdatedAt),
                properties = edge.Properties.Count > 0 ? edge.Properties : null,
            }).ToArray(),
            node_count = subgraph.Nodes.Count,
            edge_count = subgraph.Edges.Count,
        });
    }

    private async Task<(WorkflowRunReport? Report, WorkflowRunArtifactTarget Target)> ReadReportForRunAsync(
        string workflowRunId,
        TimeSpan wait,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(wait);
        WorkflowRunArtifactTarget artifactTarget;

        while (true)
        {
            artifactTarget = await ResolveArtifactTargetAsync(workflowRunId, ct);
            var report = await TryReadReportForTargetOnceAsync(artifactTarget, ct);
            if (report != null || wait <= TimeSpan.Zero)
                return (report, artifactTarget);

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return (null, artifactTarget);

            await _delayAsync(Min(ReportPollInterval, remaining), ct);
        }
    }

    private async Task<WorkflowRunReport?> TryReadReportForTargetOnceAsync(
        WorkflowRunArtifactTarget artifactTarget,
        CancellationToken ct)
    {
        var report = await _queryService.GetWorkflowRunReportArtifactAsync(artifactTarget.RequestedWorkflowRunId, ct);
        if (report != null || string.Equals(
                artifactTarget.RequestedWorkflowRunId,
                artifactTarget.ArtifactWorkflowRunId,
                StringComparison.Ordinal))
        {
            return report;
        }

        return await _queryService.GetWorkflowRunReportArtifactAsync(artifactTarget.ArtifactWorkflowRunId, ct);
    }

    private async Task<WorkflowRunArtifactTarget> ResolveArtifactTargetAsync(
        string workflowRunId,
        CancellationToken ct)
    {
        var normalized = workflowRunId.Trim();
        IReadOnlyList<WorkflowActorBinding> bindings;
        try
        {
            bindings = await _runBindingReader.ListByRunIdAsync(normalized, take: 1, ct);
        }
        catch (ArgumentException)
        {
            bindings = [];
        }

        var actorId = bindings
            .Where(static binding => binding.ActorKind == WorkflowActorKind.Run)
            .Select(static binding => binding.ActorId?.Trim() ?? string.Empty)
            .FirstOrDefault(static value => value.Length > 0);

        return new WorkflowRunArtifactTarget(
            normalized,
            string.IsNullOrWhiteSpace(actorId) ? normalized : actorId);
    }

    private static WorkflowRunGraphExportQueryOptions? BuildGraphOptions(WorkflowRunArtifactArguments args) =>
        args.EdgeTypes.Count == 0
            ? null
            : new WorkflowRunGraphExportQueryOptions
            {
                EdgeTypes = args.EdgeTypes,
            };

    private static InvocationToolError Error(string code, string message, string? field = null) =>
        new()
        {
            Code = string.IsNullOrWhiteSpace(code) ? "workflow_run_artifact_error" : code.Trim().ToLowerInvariant(),
            Message = message,
            Field = field ?? string.Empty,
        };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max] + "...";

    private static string? ToIso(DateTimeOffset value) =>
        value == default ? null : value.UtcDateTime.ToString("O");

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private static Dictionary<string, string> TruncateData(IDictionary<string, string> data) =>
        data.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Length > 500 ? pair.Value[..500] + "..." : pair.Value,
            StringComparer.Ordinal);

    private sealed record WorkflowRunArtifactTarget(
        string RequestedWorkflowRunId,
        string ArtifactWorkflowRunId);

    private sealed record WorkflowRunArtifactArguments(
        string WorkflowRunId,
        string View,
        int? Take,
        int? GraphDepth,
        int? WaitMs,
        IReadOnlyList<string> EdgeTypes)
    {
        public static WorkflowRunArtifactArguments Parse(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return new WorkflowRunArtifactArguments(string.Empty, "report", null, null, null, []);

            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("arguments must be an object");

            var root = document.RootElement;
            var workflowRunId = ReadString(root, "workflow_run_id");
            var view = ReadString(root, "view");
            if (string.IsNullOrWhiteSpace(view))
                view = "report";

            return new WorkflowRunArtifactArguments(
                workflowRunId.Trim(),
                NormalizeView(view),
                ReadInt(root, "take"),
                ReadInt(root, "graph_depth"),
                ReadInt(root, "wait_ms"),
                ReadStringArray(root, "edge_types"));
        }

        private static string NormalizeView(string view)
        {
            var normalized = view.Trim().ToLowerInvariant();
            return normalized is "report" or "timeline" or "graph_edges" or "graph_subgraph"
                ? normalized
                : "report";
        }

        private static string ReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value))
                return string.Empty;

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.GetRawText().Trim('"');
        }

        private static int? ReadInt(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
                return result;

            return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result)
                ? result
                : null;
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
                return [];

            return value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim() ?? string.Empty)
                .Where(static item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }
}
