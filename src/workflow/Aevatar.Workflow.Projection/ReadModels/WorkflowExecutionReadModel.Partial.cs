using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

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

public sealed partial class WorkflowExecutionReport
    : IProjectionReadModel,
      IProjectionReadModelCloneable<WorkflowExecutionReport>,
      IHasProjectionTimeline,
      IHasProjectionRoleReplies,
      IGraphReadModel
{
    private const string UnknownToken = "unknown";

    public DateTimeOffset CreatedAt
    {
        get => ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ToTimestamp(value);
    }

    public WorkflowExecutionProjectionScope ProjectionScope
    {
        get => (WorkflowExecutionProjectionScope)ProjectionScopeValue;
        set => ProjectionScopeValue = (int)value;
    }

    public WorkflowExecutionTopologySource TopologySource
    {
        get => (WorkflowExecutionTopologySource)TopologySourceValue;
        set => TopologySourceValue = (int)value;
    }

    public WorkflowExecutionCompletionStatus CompletionStatus
    {
        get => (WorkflowExecutionCompletionStatus)CompletionStatusValue;
        set => CompletionStatusValue = (int)value;
    }

    public DateTimeOffset StartedAt
    {
        get => ToDateTimeOffset(StartedAtUtcValue);
        set => StartedAtUtcValue = ToTimestamp(value);
    }

    public DateTimeOffset EndedAt
    {
        get => ToDateTimeOffset(EndedAtUtcValue);
        set => EndedAtUtcValue = ToTimestamp(value);
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }

    public IList<WorkflowExecutionTopologyEdge> Topology
    {
        get => TopologyEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(TopologyEntries, value);
    }

    public IList<WorkflowExecutionStepTrace> Steps
    {
        get => StepEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(StepEntries, value);
    }

    public IList<WorkflowExecutionRoleReply> RoleReplies
    {
        get => RoleReplyEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(RoleReplyEntries, value);
    }

    public IList<WorkflowExecutionTimelineEvent> Timeline
    {
        get => TimelineEntries;
        set => WorkflowExecutionReadModelCollections.ReplaceCollection(TimelineEntries, value);
    }

    public WorkflowExecutionSummary Summary
    {
        get => SummaryValue ??= new WorkflowExecutionSummary();
        set => SummaryValue = value ?? new WorkflowExecutionSummary();
    }

    public string GraphScope => WorkflowExecutionGraphConstants.Scope;

    public IReadOnlyList<ProjectionGraphNode> GraphNodes => BuildGraphNodes();

    public IReadOnlyList<ProjectionGraphEdge> GraphEdges => BuildGraphEdges();

    public WorkflowExecutionReport DeepClone() => Clone();

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
                ["workflowName"] = WorkflowName ?? string.Empty,
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
                ["workflowName"] = WorkflowName ?? string.Empty,
                ["commandId"] = NormalizeToken(CommandId),
                ["input"] = Input ?? string.Empty,
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
                    ["stepType"] = step.StepType ?? string.Empty,
                    ["targetRole"] = step.TargetRole ?? string.Empty,
                    ["workerId"] = step.WorkerId ?? string.Empty,
                    ["success"] = step.Success?.ToString() ?? string.Empty,
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
                        ["workflowName"] = WorkflowName ?? string.Empty,
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
                        ["workflowName"] = WorkflowName ?? string.Empty,
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
                    ["stepType"] = step.StepType ?? string.Empty,
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
        var normalized = token?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? UnknownToken : normalized;
    }

    private static Timestamp ToTimestamp(DateTimeOffset value) =>
        Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

}

public sealed partial class WorkflowExecutionSummary
{
    public IDictionary<string, int> StepTypeCounts
    {
        get => StepTypeCountsMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(StepTypeCountsMap, value);
    }
}

public sealed partial class WorkflowExecutionStepTrace
{
    public DateTimeOffset? RequestedAt
    {
        get => RequestedAtUtcValue == null ? null : RequestedAtUtcValue.ToDateTimeOffset();
        set => RequestedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public DateTimeOffset? CompletedAt
    {
        get => CompletedAtUtcValue == null ? null : CompletedAtUtcValue.ToDateTimeOffset();
        set => CompletedAtUtcValue = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public bool? Success
    {
        get => SuccessWrapper;
        set => SuccessWrapper = value;
    }

    public IDictionary<string, string> RequestParameters
    {
        get => RequestParametersMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(RequestParametersMap, value);
    }

    public IDictionary<string, string> CompletionAnnotations
    {
        get => CompletionAnnotationsMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(CompletionAnnotationsMap, value);
    }

    public int? SuspensionTimeoutSeconds
    {
        get => SuspensionTimeoutSecondsValue == 0 ? null : SuspensionTimeoutSecondsValue;
        set => SuspensionTimeoutSecondsValue = value ?? 0;
    }

    public double? DurationMs => RequestedAt.HasValue && CompletedAt.HasValue
        ? Math.Max(0, (CompletedAt.Value - RequestedAt.Value).TotalMilliseconds)
        : null;
}

public sealed partial class WorkflowExecutionRoleReply
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed partial class WorkflowExecutionTimelineEvent
{
    public DateTimeOffset Timestamp
    {
        get => TimestampUtcValue == null ? default : TimestampUtcValue.ToDateTimeOffset();
        set => TimestampUtcValue = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public IDictionary<string, string> Data
    {
        get => DataMap;
        set => WorkflowExecutionReadModelCollections.ReplaceMap(DataMap, value);
    }
}

public sealed partial class WorkflowExecutionTopologyEdge
{
    public WorkflowExecutionTopologyEdge(string parent, string child)
    {
        Parent = parent ?? string.Empty;
        Child = child ?? string.Empty;
    }
}

internal static class WorkflowExecutionReadModelCollections
{
    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
            return;

        target.Add(source);
    }

    public static void ReplaceMap<TValue>(MapField<string, TValue> target, IDictionary<string, TValue>? source)
    {
        target.Clear();
        if (source == null)
            return;

        target.Add(source);
    }
}
