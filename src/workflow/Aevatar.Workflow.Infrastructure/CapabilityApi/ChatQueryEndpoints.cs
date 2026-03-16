using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ChatQueryEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflow-catalog", ListWorkflowCatalog)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/capabilities", GetCapabilities)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows/{workflowName}", GetWorkflowDetail)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/actors/{actorId}", GetActorSnapshot)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/actors/{actorId}/timeline", ListActorTimeline)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-edges", ListActorGraphEdges)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-subgraph", GetActorGraphSubgraph)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-enriched", GetActorGraphEnrichedSnapshot)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ListAgents(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var agents = await queryService.ListAgentsAsync(ct);
        return Results.Ok(agents);
    }

    internal static IResult ListWorkflows(IWorkflowExecutionQueryApplicationService queryService) =>
        Results.Ok(queryService.ListWorkflows());

    internal static IResult ListWorkflowCatalog(IWorkflowExecutionQueryApplicationService queryService) =>
        Results.Ok(queryService.ListWorkflowCatalog());

    internal static IResult GetCapabilities(IWorkflowExecutionQueryApplicationService queryService) =>
        Results.Ok(queryService.GetCapabilities());

    internal static IResult GetWorkflowDetail(
        string workflowName,
        IWorkflowExecutionQueryApplicationService queryService)
    {
        var detail = queryService.GetWorkflowDetail(workflowName);
        return detail == null ? Results.NotFound() : Results.Ok(detail);
    }

    internal static async Task<IResult> GetActorSnapshot(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var snapshot = await queryService.GetActorSnapshotAsync(actorId, ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(MapSnapshot(snapshot));
    }

    internal static async Task<IResult> ListActorTimeline(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int take = 200,
        CancellationToken ct = default)
    {
        var timeline = await queryService.ListActorTimelineAsync(actorId, take, ct);
        return Results.Ok(timeline.Select(MapTimelineItem));
    }

    internal static async Task<IResult> ListActorGraphEdges(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var edges = await queryService.ListActorGraphEdgesAsync(actorId, take, graphOptions, ct);
        return Results.Ok(edges.Select(MapGraphEdge));
    }

    internal static async Task<IResult> GetActorGraphSubgraph(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var subgraph = await queryService.GetActorGraphSubgraphAsync(actorId, depth, take, graphOptions, ct);
        return Results.Ok(MapGraphSubgraph(subgraph));
    }

    internal static async Task<IResult> GetActorGraphEnrichedSnapshot(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var graphEnriched = await queryService.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, graphOptions, ct);
        return graphEnriched == null ? Results.NotFound() : Results.Ok(MapGraphEnrichedSnapshot(graphEnriched));
    }

    private static WorkflowActorGraphQueryOptions BuildGraphQueryOptions(
        string? direction,
        string[]? edgeTypes)
    {
        return new WorkflowActorGraphQueryOptions
        {
            Direction = ParseDirection(direction),
            EdgeTypes = NormalizeEdgeTypes(edgeTypes),
        };
    }

    private static WorkflowActorGraphDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return WorkflowActorGraphDirection.Both;

        return Enum.TryParse<WorkflowActorGraphDirection>(direction.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : WorkflowActorGraphDirection.Both;
    }

    private static IReadOnlyList<string> NormalizeEdgeTypes(IReadOnlyList<string>? edgeTypes)
    {
        if (edgeTypes == null || edgeTypes.Count == 0)
            return [];

        return edgeTypes
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static WorkflowActorSnapshotHttpResponse MapSnapshot(WorkflowActorSnapshot snapshot) =>
        new(
            snapshot.ActorId,
            snapshot.WorkflowName,
            snapshot.LastCommandId,
            snapshot.CompletionStatus,
            snapshot.StateVersion,
            snapshot.LastEventId,
            snapshot.LastUpdatedAt,
            snapshot.LastSuccess,
            snapshot.LastOutput,
            snapshot.LastError,
            snapshot.TotalSteps,
            snapshot.RequestedSteps,
            snapshot.CompletedSteps,
            snapshot.RoleReplyCount);

    private static WorkflowActorTimelineItemHttpResponse MapTimelineItem(WorkflowActorTimelineItem item) =>
        new(
            item.Timestamp,
            item.Stage,
            item.Message,
            item.AgentId,
            item.StepId,
            item.StepType,
            item.EventType,
            item.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowActorGraphNodeHttpResponse MapGraphNode(WorkflowActorGraphNode node) =>
        new(
            node.NodeId,
            node.NodeType,
            node.UpdatedAt,
            node.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowActorGraphEdgeHttpResponse MapGraphEdge(WorkflowActorGraphEdge edge) =>
        new(
            edge.EdgeId,
            edge.FromNodeId,
            edge.ToNodeId,
            edge.EdgeType,
            edge.UpdatedAt,
            edge.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowActorGraphSubgraphHttpResponse MapGraphSubgraph(WorkflowActorGraphSubgraph subgraph) =>
        new(
            subgraph.RootNodeId,
            subgraph.Nodes.Select(MapGraphNode).ToList(),
            subgraph.Edges.Select(MapGraphEdge).ToList());

    private static WorkflowActorGraphEnrichedSnapshotHttpResponse MapGraphEnrichedSnapshot(WorkflowActorGraphEnrichedSnapshot snapshot) =>
        new(
            MapSnapshot(snapshot.Snapshot ?? new WorkflowActorSnapshot()),
            MapGraphSubgraph(snapshot.Subgraph ?? new WorkflowActorGraphSubgraph()));
}

public sealed record WorkflowActorSnapshotHttpResponse(
    string ActorId,
    string WorkflowName,
    string LastCommandId,
    WorkflowRunCompletionStatus CompletionStatus,
    long StateVersion,
    string LastEventId,
    DateTimeOffset LastUpdatedAt,
    bool? LastSuccess,
    string LastOutput,
    string LastError,
    int TotalSteps,
    int RequestedSteps,
    int CompletedSteps,
    int RoleReplyCount);

public sealed record WorkflowActorTimelineItemHttpResponse(
    DateTimeOffset Timestamp,
    string Stage,
    string Message,
    string AgentId,
    string StepId,
    string StepType,
    string EventType,
    Dictionary<string, string> Data);

public sealed record WorkflowActorGraphNodeHttpResponse(
    string NodeId,
    string NodeType,
    DateTimeOffset UpdatedAt,
    Dictionary<string, string> Properties);

public sealed record WorkflowActorGraphEdgeHttpResponse(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    string EdgeType,
    DateTimeOffset UpdatedAt,
    Dictionary<string, string> Properties);

public sealed record WorkflowActorGraphSubgraphHttpResponse(
    string RootNodeId,
    List<WorkflowActorGraphNodeHttpResponse> Nodes,
    List<WorkflowActorGraphEdgeHttpResponse> Edges);

public sealed record WorkflowActorGraphEnrichedSnapshotHttpResponse(
    WorkflowActorSnapshotHttpResponse Snapshot,
    WorkflowActorGraphSubgraphHttpResponse Subgraph);
