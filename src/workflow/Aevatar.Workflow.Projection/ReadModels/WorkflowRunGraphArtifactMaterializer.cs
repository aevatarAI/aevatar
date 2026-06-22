using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowRunGraphArtifactMaterializer
{
    private const string UnknownToken = "unknown";

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public ProjectionGraphMaterialization Materialize(WorkflowRunInsightReportDocument readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        return new ProjectionGraphMaterialization
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            Nodes = BuildGraphNodes(readModel),
            Edges = BuildGraphEdges(readModel),
        };
    }

    private static IReadOnlyList<ProjectionGraphNode> BuildGraphNodes(WorkflowRunInsightReportDocument readModel)
    {
        var updatedAt = readModel.UpdatedAt == default ? DateTimeOffset.UtcNow : readModel.UpdatedAt;
        var rootActorId = NormalizeToken(readModel.RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, readModel.CommandId);
        var nodes = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);

        nodes[rootActorId] = CreateActorNode(rootActorId, readModel.WorkflowName, updatedAt);
        nodes[runNodeId] = new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = runNodeId,
            NodeType = WorkflowExecutionGraphConstants.RunNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = rootActorId,
                ["workflowName"] = readModel.WorkflowName ?? string.Empty,
                ["commandId"] = NormalizeToken(readModel.CommandId),
                ["input"] = readModel.Input ?? string.Empty,
            },
            UpdatedAt = updatedAt,
        };

        foreach (var step in readModel.Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, readModel.CommandId, step.StepId);
            nodes[stepNodeId] = new ProjectionGraphNode
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                NodeId = stepNodeId,
                NodeType = WorkflowExecutionGraphConstants.StepNodeType,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rootActorId"] = rootActorId,
                    ["commandId"] = NormalizeToken(readModel.CommandId),
                    ["stepId"] = NormalizeToken(step.StepId),
                    ["stepType"] = step.StepType ?? string.Empty,
                    ["targetRole"] = step.TargetRole ?? string.Empty,
                    ["workerId"] = step.WorkerId ?? string.Empty,
                    ["success"] = step.Success?.ToString() ?? string.Empty,
                },
                UpdatedAt = updatedAt,
            };
        }

        foreach (var topologyEdge in readModel.Topology)
        {
            var parentId = NormalizeToken(topologyEdge.Parent);
            var childId = NormalizeToken(topologyEdge.Child);
            if (!nodes.ContainsKey(parentId))
                nodes[parentId] = CreateActorNode(parentId, readModel.WorkflowName, updatedAt);

            if (!nodes.ContainsKey(childId))
                nodes[childId] = CreateActorNode(childId, readModel.WorkflowName, updatedAt);
        }

        return nodes.Values.ToList();
    }

    private static IReadOnlyList<ProjectionGraphEdge> BuildGraphEdges(WorkflowRunInsightReportDocument readModel)
    {
        var updatedAt = readModel.UpdatedAt == default ? DateTimeOffset.UtcNow : readModel.UpdatedAt;
        var rootActorId = NormalizeToken(readModel.RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, readModel.CommandId);
        var edges = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);

        var ownsEdge = CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeOwns,
            rootActorId,
            runNodeId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            updatedAt);
        edges[ownsEdge.EdgeId] = ownsEdge;

        var stepIds = new HashSet<string>(
            readModel.Steps.Select(step => NormalizeToken(step.StepId)),
            StringComparer.Ordinal);

        foreach (var step in readModel.Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, readModel.CommandId, step.StepId);
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

            // step -> next-step execution-flow edge (only when the next step is a known step node), so the
            // graph carries the real run order; the branch taken rides along as branchKey.
            if (!string.IsNullOrWhiteSpace(step.NextStepId) && stepIds.Contains(NormalizeToken(step.NextStepId)))
            {
                var nextProperties = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(step.BranchKey))
                    nextProperties["branchKey"] = step.BranchKey;
                var nextEdge = CreateEdge(
                    WorkflowExecutionGraphConstants.EdgeTypeNext,
                    stepNodeId,
                    BuildStepNodeId(rootActorId, readModel.CommandId, step.NextStepId),
                    nextProperties,
                    updatedAt);
                edges[nextEdge.EdgeId] = nextEdge;
            }
        }

        foreach (var topologyEdge in readModel.Topology)
        {
            var parentId = NormalizeToken(topologyEdge.Parent);
            var childId = NormalizeToken(topologyEdge.Child);
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

    private static ProjectionGraphNode CreateActorNode(
        string actorId,
        string? workflowName,
        DateTimeOffset updatedAt)
    {
        return new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = actorId,
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflowName"] = workflowName ?? string.Empty,
            },
            UpdatedAt = updatedAt,
        };
    }

    private static ProjectionGraphEdge CreateEdge(
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
            Scope = WorkflowExecutionGraphConstants.Scope,
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
}
