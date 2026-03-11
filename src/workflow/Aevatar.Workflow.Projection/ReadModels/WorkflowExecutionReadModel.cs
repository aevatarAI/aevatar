using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.ReadModels;

public enum WorkflowExecutionProjectionScope
{
    ActorShared = 0,
    RunIsolated = 1,
}

public enum WorkflowExecutionTopologySource
{
    RuntimeSnapshot = 0,
}

public enum WorkflowExecutionCompletionStatus
{
    Running = 0,
    Completed = 1,
    TimedOut = 2,
    Failed = 3,
    Stopped = 4,
    NotFound = 5,
    Disabled = 6,
    WaitingForSignal = 7,
    Unknown = 99,
}

/// <summary>
/// Read model for one workflow execution.
/// </summary>
public sealed class WorkflowExecutionReport
    : AevatarReadModelBase,
      IHasProjectionTimeline,
      IHasProjectionRoleReplies,
      IGraphReadModel
{
    private const string UnknownToken = "unknown";

    public string GraphScope => WorkflowExecutionGraphConstants.Scope;

    public IReadOnlyList<ProjectionGraphNode> GraphNodes => BuildGraphNodes();

    public IReadOnlyList<ProjectionGraphEdge> GraphEdges => BuildGraphEdges();

    public string RootActorId { get; set; } = "";
    public string CommandId { get; set; } = "";
    public string ReportVersion { get; set; } = "1.0";
    public WorkflowExecutionProjectionScope ProjectionScope { get; set; } = WorkflowExecutionProjectionScope.RunIsolated;
    public WorkflowExecutionTopologySource TopologySource { get; set; } = WorkflowExecutionTopologySource.RuntimeSnapshot;
    public WorkflowExecutionCompletionStatus CompletionStatus { get; set; } = WorkflowExecutionCompletionStatus.Unknown;
    public string WorkflowName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool? Success { get; set; }
    public string Input { get; set; } = "";
    public string FinalOutput { get; set; } = "";
    public string FinalError { get; set; } = "";
    public List<WorkflowExecutionTopologyEdge> Topology { get; set; } = [];
    public List<WorkflowExecutionStepTrace> Steps { get; set; } = [];
    public List<WorkflowExecutionRoleReply> RoleReplies { get; set; } = [];
    public List<WorkflowExecutionTimelineEvent> Timeline { get; set; } = [];
    public WorkflowExecutionSummary Summary { get; set; } = new();

    public void AddTimeline(ProjectionTimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);

        Timeline.Add(new WorkflowExecutionTimelineEvent
        {
            Timestamp = timelineEvent.Timestamp,
            Stage = timelineEvent.Stage,
            Message = timelineEvent.Message,
            AgentId = timelineEvent.AgentId,
            StepId = timelineEvent.StepId,
            StepType = timelineEvent.StepType,
            EventType = timelineEvent.EventType,
            Data = timelineEvent.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        });
    }

    public void AddRoleReply(ProjectionRoleReply roleReply)
    {
        ArgumentNullException.ThrowIfNull(roleReply);

        RoleReplies.Add(new WorkflowExecutionRoleReply
        {
            Timestamp = roleReply.Timestamp,
            RoleId = roleReply.RoleId,
            SessionId = roleReply.SessionId,
            Content = roleReply.Content,
            ContentLength = roleReply.ContentLength,
        });
    }

    private IReadOnlyList<ProjectionGraphNode> BuildGraphNodes()
    {
        var updatedAt = UpdatedAt == default ? DateTimeOffset.UtcNow : UpdatedAt;
        var rootActorId = NormalizeToken(RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, CommandId);
        var nodes = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);

        nodes[rootActorId] = new ProjectionGraphNode
        {
            Scope = GraphScope,
            NodeId = rootActorId,
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflowName"] = WorkflowName ?? "",
            },
            UpdatedAt = updatedAt,
        };

        nodes[runNodeId] = new ProjectionGraphNode
        {
            Scope = GraphScope,
            NodeId = runNodeId,
            NodeType = WorkflowExecutionGraphConstants.RunNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = rootActorId,
                ["workflowName"] = WorkflowName ?? "",
                ["commandId"] = NormalizeToken(CommandId),
                ["input"] = Input ?? "",
            },
            UpdatedAt = updatedAt,
        };

        foreach (var step in Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, CommandId, step.StepId);
            nodes[stepNodeId] = new ProjectionGraphNode
            {
                Scope = GraphScope,
                NodeId = stepNodeId,
                NodeType = WorkflowExecutionGraphConstants.StepNodeType,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rootActorId"] = rootActorId,
                    ["commandId"] = NormalizeToken(CommandId),
                    ["stepId"] = NormalizeToken(step.StepId),
                    ["stepType"] = step.StepType ?? "",
                    ["targetRole"] = step.TargetRole ?? "",
                    ["workerId"] = step.WorkerId ?? "",
                    ["success"] = step.Success?.ToString() ?? "",
                },
                UpdatedAt = updatedAt,
            };
        }

        foreach (var topologyEdge in Topology)
        {
            var parentId = NormalizeToken(topologyEdge.Parent);
            var childId = NormalizeToken(topologyEdge.Child);
            if (parentId.Length > 0 && !nodes.ContainsKey(parentId))
            {
                nodes[parentId] = new ProjectionGraphNode
                {
                    Scope = GraphScope,
                    NodeId = parentId,
                    NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["workflowName"] = WorkflowName ?? "",
                    },
                    UpdatedAt = updatedAt,
                };
            }

            if (childId.Length > 0 && !nodes.ContainsKey(childId))
            {
                nodes[childId] = new ProjectionGraphNode
                {
                    Scope = GraphScope,
                    NodeId = childId,
                    NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["workflowName"] = WorkflowName ?? "",
                    },
                    UpdatedAt = updatedAt,
                };
            }
        }

        return nodes.Values.ToList();
    }

    private IReadOnlyList<ProjectionGraphEdge> BuildGraphEdges()
    {
        var updatedAt = UpdatedAt == default ? DateTimeOffset.UtcNow : UpdatedAt;
        var rootActorId = NormalizeToken(RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, CommandId);
        var edges = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);

        var ownsEdge = CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeOwns,
            rootActorId,
            runNodeId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            updatedAt);
        edges[ownsEdge.EdgeId] = ownsEdge;

        foreach (var step in Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, CommandId, step.StepId);
            var containsEdge = CreateEdge(
                WorkflowExecutionGraphConstants.EdgeTypeContainsStep,
                runNodeId,
                stepNodeId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stepId"] = NormalizeToken(step.StepId),
                    ["stepType"] = step.StepType ?? "",
                },
                updatedAt);
            edges[containsEdge.EdgeId] = containsEdge;
        }

        foreach (var topologyEdge in Topology)
        {
            var parentId = NormalizeToken(topologyEdge.Parent);
            var childId = NormalizeToken(topologyEdge.Child);
            if (parentId.Length == 0 || childId.Length == 0)
                continue;

            var childOfEdge = CreateEdge(
                WorkflowExecutionGraphConstants.EdgeTypeChildOf,
                parentId,
                childId,
                new Dictionary<string, string>(StringComparer.Ordinal),
                updatedAt);
            edges[childOfEdge.EdgeId] = childOfEdge;
        }

        return edges.Values.ToList();
    }

    private ProjectionGraphEdge CreateEdge(
        string relationType,
        string fromNodeId,
        string toNodeId,
        IReadOnlyDictionary<string, string> properties,
        DateTimeOffset updatedAt)
    {
        var normalizedFromNodeId = NormalizeToken(fromNodeId);
        var normalizedToNodeId = NormalizeToken(toNodeId);
        var normalizedEdgeType = NormalizeToken(relationType);
        var edgeId = BuildEdgeId(normalizedEdgeType, normalizedFromNodeId, normalizedToNodeId);
        return new ProjectionGraphEdge
        {
            Scope = GraphScope,
            EdgeId = edgeId,
            EdgeType = normalizedEdgeType,
            FromNodeId = normalizedFromNodeId,
            ToNodeId = normalizedToNodeId,
            Properties = new Dictionary<string, string>(properties, StringComparer.Ordinal),
            UpdatedAt = updatedAt,
        };
    }

    private static string BuildRunNodeId(string rootActorId, string commandId)
    {
        var normalizedRootActorId = NormalizeToken(rootActorId);
        var normalizedCommandId = NormalizeToken(commandId);
        return $"run:{normalizedRootActorId}:{normalizedCommandId}";
    }

    private static string BuildStepNodeId(string rootActorId, string commandId, string stepId)
    {
        var normalizedRootActorId = NormalizeToken(rootActorId);
        var normalizedCommandId = NormalizeToken(commandId);
        var normalizedStepId = NormalizeToken(stepId);
        return $"step:{normalizedRootActorId}:{normalizedCommandId}:{normalizedStepId}";
    }

    private static string BuildEdgeId(string relationType, string fromNodeId, string toNodeId)
    {
        var payload = $"{relationType}|{fromNodeId}|{toNodeId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{relationType}:{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private static string NormalizeToken(string? token)
    {
        var normalized = token?.Trim() ?? "";
        return normalized.Length == 0 ? UnknownToken : normalized;
    }
}

public sealed class WorkflowExecutionSummary
{
    public int TotalSteps { get; set; }
    public int RequestedSteps { get; set; }
    public int CompletedSteps { get; set; }
    public int RoleReplyCount { get; set; }
    public Dictionary<string, int> StepTypeCounts { get; set; } = [];
}

public sealed class WorkflowExecutionStepTrace
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string TargetRole { get; set; } = "";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string WorkerId { get; set; } = "";
    public string OutputPreview { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, string> RequestParameters { get; set; } = [];
    public Dictionary<string, string> CompletionAnnotations { get; set; } = [];
    public string NextStepId { get; set; } = "";
    public string BranchKey { get; set; } = "";
    public string AssignedVariable { get; set; } = "";
    public string AssignedValue { get; set; } = "";
    public string SuspensionType { get; set; } = "";
    public string SuspensionPrompt { get; set; } = "";
    public int? SuspensionTimeoutSeconds { get; set; }
    public string RequestedVariableName { get; set; } = "";
    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue
        ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
        : null;
}

public sealed class WorkflowExecutionRoleReply
{
    public DateTimeOffset Timestamp { get; set; }
    public string RoleId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
}

public sealed class WorkflowExecutionTimelineEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string EventType { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = [];
}

public sealed record WorkflowExecutionTopologyEdge(string Parent, string Child);
