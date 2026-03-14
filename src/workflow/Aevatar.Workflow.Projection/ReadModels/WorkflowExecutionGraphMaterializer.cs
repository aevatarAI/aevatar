using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowExecutionGraphMaterializer
    : IProjectionGraphMaterializer<WorkflowExecutionReport>
{
    private const string UnknownToken = "unknown";

    public ProjectionGraphMaterialization Materialize(WorkflowExecutionReport readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        return new ProjectionGraphMaterialization
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            Nodes = BuildGraphNodes(readModel),
            Edges = BuildGraphEdges(readModel),
        };
    }

    private static IReadOnlyList<ProjectionGraphNode> BuildGraphNodes(WorkflowExecutionReport report)
    {
        var updatedAt = report.UpdatedAt == default ? DateTimeOffset.UtcNow : report.UpdatedAt;
        var rootActorId = NormalizeToken(report.RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, report.CommandId);
        var nodes = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);

        nodes[rootActorId] = CreateActorNode(rootActorId, report.WorkflowName, updatedAt);
        nodes[runNodeId] = new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = runNodeId,
            NodeType = WorkflowExecutionGraphConstants.RunNodeType,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rootActorId"] = rootActorId,
                ["workflowName"] = report.WorkflowName ?? string.Empty,
                ["commandId"] = NormalizeToken(report.CommandId),
                ["input"] = report.Input ?? string.Empty,
            },
            UpdatedAt = updatedAt,
        };

        foreach (var step in report.Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, report.CommandId, step.StepId);
            nodes[stepNodeId] = new ProjectionGraphNode
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                NodeId = stepNodeId,
                NodeType = WorkflowExecutionGraphConstants.StepNodeType,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rootActorId"] = rootActorId,
                    ["commandId"] = NormalizeToken(report.CommandId),
                    ["stepId"] = NormalizeToken(step.StepId),
                    ["stepType"] = step.StepType ?? string.Empty,
                    ["targetRole"] = step.TargetRole ?? string.Empty,
                    ["workerId"] = step.WorkerId ?? string.Empty,
                    ["success"] = step.Success?.ToString() ?? string.Empty,
                },
                UpdatedAt = updatedAt,
            };
        }

        foreach (var topologyEdge in report.Topology)
        {
            var parentId = NormalizeToken(topologyEdge.Parent);
            var childId = NormalizeToken(topologyEdge.Child);
            if (!nodes.ContainsKey(parentId))
                nodes[parentId] = CreateActorNode(parentId, report.WorkflowName, updatedAt);

            if (!nodes.ContainsKey(childId))
                nodes[childId] = CreateActorNode(childId, report.WorkflowName, updatedAt);
        }

        return nodes.Values.ToList();
    }

    private static IReadOnlyList<ProjectionGraphEdge> BuildGraphEdges(WorkflowExecutionReport report)
    {
        var updatedAt = report.UpdatedAt == default ? DateTimeOffset.UtcNow : report.UpdatedAt;
        var rootActorId = NormalizeToken(report.RootActorId);
        var runNodeId = BuildRunNodeId(rootActorId, report.CommandId);
        var edges = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);

        var ownsEdge = CreateEdge(
            WorkflowExecutionGraphConstants.EdgeTypeOwns,
            rootActorId,
            runNodeId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            updatedAt);
        edges[ownsEdge.EdgeId] = ownsEdge;

        foreach (var step in report.Steps)
        {
            var stepNodeId = BuildStepNodeId(rootActorId, report.CommandId, step.StepId);
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

        foreach (var topologyEdge in report.Topology)
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
