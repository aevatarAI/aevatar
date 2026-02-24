using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionRelationProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private const string UnknownToken = "unknown";
    private readonly IProjectionRelationStore _relationStore;

    public WorkflowExecutionRelationProjector(IProjectionRelationStore relationStore)
    {
        _relationStore = relationStore;
    }

    public async ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        var runNodeId = BuildRunNodeId(context.RootActorId, context.CommandId);
        var now = context.StartedAt;
        await _relationStore.UpsertNodeAsync(
            BuildActorNode(context.RootActorId, context.WorkflowName, now),
            ct);
        await _relationStore.UpsertNodeAsync(
            BuildRunNode(runNodeId, context.RootActorId, context.WorkflowName, context.CommandId, context.Input, now),
            ct);
        await _relationStore.UpsertEdgeAsync(
            BuildEdge(
                context.RootActorId,
                runNodeId,
                WorkflowExecutionRelationConstants.RelationOwns,
                now),
            ct);
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        var runNodeId = BuildRunNodeId(context.RootActorId, context.CommandId);
        var now = ResolveEventTimestamp(envelope);

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            await UpsertStepRelationAsync(
                context,
                runNodeId,
                evt.StepId,
                evt.StepType,
                evt.TargetRole,
                workerId: "",
                success: null,
                now,
                ct);
            return;
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            await UpsertStepRelationAsync(
                context,
                runNodeId,
                evt.StepId,
                stepType: "",
                targetRole: "",
                evt.WorkerId,
                evt.Success,
                now,
                ct);
        }
    }

    public async ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var runNodeId = BuildRunNodeId(context.RootActorId, context.CommandId);

        await _relationStore.UpsertNodeAsync(
            BuildActorNode(context.RootActorId, context.WorkflowName, completedAt),
            ct);
        await _relationStore.UpsertNodeAsync(
            BuildRunNode(
                runNodeId,
                context.RootActorId,
                context.WorkflowName,
                context.CommandId,
                context.Input,
                completedAt),
            ct);
        await _relationStore.UpsertEdgeAsync(
            BuildEdge(
                context.RootActorId,
                runNodeId,
                WorkflowExecutionRelationConstants.RelationOwns,
                completedAt),
            ct);

        foreach (var edge in topology)
        {
            var rawParentNodeId = edge.Parent?.Trim() ?? "";
            var rawChildNodeId = edge.Child?.Trim() ?? "";
            if (rawParentNodeId.Length == 0 || rawChildNodeId.Length == 0)
                continue;
            var parentNodeId = NormalizeToken(rawParentNodeId);
            var childNodeId = NormalizeToken(rawChildNodeId);

            await _relationStore.UpsertNodeAsync(
                BuildActorNode(parentNodeId, context.WorkflowName, completedAt),
                ct);
            await _relationStore.UpsertNodeAsync(
                BuildActorNode(childNodeId, context.WorkflowName, completedAt),
                ct);
            await _relationStore.UpsertEdgeAsync(
                BuildEdge(
                    parentNodeId,
                    childNodeId,
                    WorkflowExecutionRelationConstants.RelationChildOf,
                    completedAt),
                ct);
        }
    }

    private async Task UpsertStepRelationAsync(
        WorkflowExecutionProjectionContext context,
        string runNodeId,
        string stepId,
        string stepType,
        string targetRole,
        string workerId,
        bool? success,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var rawStepId = stepId?.Trim() ?? "";
        if (rawStepId.Length == 0)
            return;

        var normalizedStepId = NormalizeToken(rawStepId);
        var stepNodeId = BuildStepNodeId(context.RootActorId, context.CommandId, normalizedStepId);
        var stepTypeValue = stepType?.Trim() ?? "";
        var targetRoleValue = targetRole?.Trim() ?? "";
        var workerIdValue = workerId?.Trim() ?? "";
        var successValue = success;
        if (stepTypeValue.Length == 0 ||
            targetRoleValue.Length == 0 ||
            workerIdValue.Length == 0 ||
            !successValue.HasValue)
        {
            var existingNode = await TryGetNodeAsync(stepNodeId, ct);
            if (existingNode != null)
            {
                if (stepTypeValue.Length == 0 &&
                    existingNode.Properties.TryGetValue("stepType", out var existingStepType))
                {
                    stepTypeValue = existingStepType;
                }

                if (targetRoleValue.Length == 0 &&
                    existingNode.Properties.TryGetValue("targetRole", out var existingTargetRole))
                {
                    targetRoleValue = existingTargetRole;
                }

                if (workerIdValue.Length == 0 &&
                    existingNode.Properties.TryGetValue("workerId", out var existingWorkerId))
                {
                    workerIdValue = existingWorkerId;
                }

                if (!successValue.HasValue &&
                    existingNode.Properties.TryGetValue("success", out var existingSuccess) &&
                    bool.TryParse(existingSuccess, out var parsedSuccess))
                {
                    successValue = parsedSuccess;
                }
            }
        }

        await _relationStore.UpsertNodeAsync(
            BuildStepNode(
                stepNodeId,
                context.RootActorId,
                context.CommandId,
                normalizedStepId,
                stepTypeValue,
                targetRoleValue,
                workerIdValue,
                successValue,
                updatedAt),
            ct);
        await _relationStore.UpsertEdgeAsync(
            BuildEdge(
                runNodeId,
                stepNodeId,
                WorkflowExecutionRelationConstants.RelationContainsStep,
                updatedAt),
            ct);
    }

    private async Task<ProjectionRelationNode?> TryGetNodeAsync(
        string nodeId,
        CancellationToken ct)
    {
        var subgraph = await _relationStore.GetSubgraphAsync(
            new ProjectionRelationQuery
            {
                Scope = WorkflowExecutionRelationConstants.Scope,
                RootNodeId = nodeId,
                Direction = ProjectionRelationDirection.Both,
                Depth = 1,
                Take = 1,
            },
            ct);
        return subgraph.Nodes.FirstOrDefault(x =>
            string.Equals(x.NodeId, nodeId, StringComparison.Ordinal) &&
            x.Properties.Count > 0);
    }

    private static ProjectionRelationNode BuildActorNode(
        string actorId,
        string workflowName,
        DateTimeOffset updatedAt)
    {
        return new ProjectionRelationNode
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            NodeId = NormalizeToken(actorId),
            NodeType = WorkflowExecutionRelationConstants.ActorNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflowName"] = workflowName ?? "",
            },
            UpdatedAt = updatedAt,
        };
    }

    private static ProjectionRelationNode BuildRunNode(
        string runNodeId,
        string rootActorId,
        string workflowName,
        string commandId,
        string input,
        DateTimeOffset updatedAt)
    {
        return new ProjectionRelationNode
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            NodeId = runNodeId,
            NodeType = WorkflowExecutionRelationConstants.RunNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = NormalizeToken(rootActorId),
                ["workflowName"] = workflowName ?? "",
                ["commandId"] = NormalizeToken(commandId),
                ["input"] = input ?? "",
            },
            UpdatedAt = updatedAt,
        };
    }

    private static ProjectionRelationNode BuildStepNode(
        string stepNodeId,
        string rootActorId,
        string commandId,
        string stepId,
        string stepType,
        string targetRole,
        string workerId,
        bool? success,
        DateTimeOffset updatedAt)
    {
        return new ProjectionRelationNode
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            NodeId = stepNodeId,
            NodeType = WorkflowExecutionRelationConstants.StepNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = NormalizeToken(rootActorId),
                ["commandId"] = NormalizeToken(commandId),
                ["stepId"] = NormalizeToken(stepId),
                ["stepType"] = stepType ?? "",
                ["targetRole"] = targetRole ?? "",
                ["workerId"] = workerId ?? "",
                ["success"] = success?.ToString() ?? "",
            },
            UpdatedAt = updatedAt,
        };
    }

    private static ProjectionRelationEdge BuildEdge(
        string fromNodeId,
        string toNodeId,
        string relationType,
        DateTimeOffset updatedAt)
    {
        var normalizedFromNodeId = NormalizeToken(fromNodeId);
        var normalizedToNodeId = NormalizeToken(toNodeId);
        var normalizedRelationType = NormalizeToken(relationType);
        return new ProjectionRelationEdge
        {
            Scope = WorkflowExecutionRelationConstants.Scope,
            EdgeId = BuildEdgeId(normalizedRelationType, normalizedFromNodeId, normalizedToNodeId),
            FromNodeId = normalizedFromNodeId,
            ToNodeId = normalizedToNodeId,
            RelationType = normalizedRelationType,
            UpdatedAt = updatedAt,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal),
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

    private static string NormalizeToken(string token)
    {
        var normalized = token?.Trim() ?? "";
        return normalized.Length == 0 ? UnknownToken : normalized;
    }

    private static DateTimeOffset ResolveEventTimestamp(EventEnvelope envelope)
    {
        var ts = envelope.Timestamp;
        if (ts == null)
            return DateTimeOffset.UtcNow;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }
}
