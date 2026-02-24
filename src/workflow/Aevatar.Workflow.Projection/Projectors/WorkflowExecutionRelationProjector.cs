using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionRelationProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private const string UnknownToken = "unknown";
    private readonly IProjectionRelationStore _relationStore;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _readModelStore;

    public WorkflowExecutionRelationProjector(
        IProjectionRelationStore relationStore,
        IProjectionReadModelStore<WorkflowExecutionReport, string> readModelStore)
    {
        _relationStore = relationStore;
        _readModelStore = readModelStore;
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

    public ValueTask ProjectAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        _ = context;
        _ = envelope;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        var report = await _readModelStore.GetAsync(context.RootActorId, ct);
        var completedAt = report?.EndedAt ?? DateTimeOffset.UtcNow;
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
            var parentNodeId = NormalizeToken(edge.Parent);
            var childNodeId = NormalizeToken(edge.Child);
            if (parentNodeId.Length == 0 || childNodeId.Length == 0)
                continue;

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

        if (report == null || report.Steps.Count == 0)
            return;

        foreach (var step in report.Steps)
        {
            var stepId = NormalizeToken(step.StepId);
            if (stepId.Length == 0)
                continue;

            var stepNodeId = BuildStepNodeId(context.RootActorId, stepId);
            var stepUpdatedAt = step.CompletedAt ?? step.RequestedAt ?? completedAt;
            await _relationStore.UpsertNodeAsync(
                BuildStepNode(
                    stepNodeId,
                    context.RootActorId,
                    step,
                    stepUpdatedAt),
                ct);
            await _relationStore.UpsertEdgeAsync(
                BuildEdge(
                    runNodeId,
                    stepNodeId,
                    WorkflowExecutionRelationConstants.RelationContainsStep,
                    stepUpdatedAt),
                ct);
        }
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
        WorkflowExecutionStepTrace step,
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
                ["stepId"] = NormalizeToken(step.StepId),
                ["stepType"] = step.StepType ?? "",
                ["targetRole"] = step.TargetRole ?? "",
                ["workerId"] = step.WorkerId ?? "",
                ["success"] = step.Success?.ToString() ?? "",
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

    private static string BuildStepNodeId(string rootActorId, string stepId)
    {
        var normalizedRootActorId = NormalizeToken(rootActorId);
        var normalizedStepId = NormalizeToken(stepId);
        return $"step:{normalizedRootActorId}:{normalizedStepId}";
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
}
