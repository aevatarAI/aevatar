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

        group.MapGet("/primitives", ListPrimitives)
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

        group.MapGet("/actors/{actorId}/graph-enriched", GetActorGraphEnriched)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/actors/{actorId}/graph-subgraph", GetActorGraphSubgraph)
            .Produces(StatusCodes.Status200OK);

    }

    internal static async Task<IResult> ListAgents(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var agents = await queryService.ListAgentsAsync(ct);
        return Results.Ok(agents);
    }

    internal static IResult ListPrimitives(IWorkflowExecutionQueryApplicationService queryService)
    {
        var capabilities = queryService.GetCapabilities();
        var exampleWorkflowsByPrimitive = queryService
            .ListWorkflowCatalog()
            .Where(static item => item.IsPrimitiveExample)
            .SelectMany(item => item.Primitives.Select(primitive => new { Primitive = primitive, Workflow = item.Name }))
            .GroupBy(static item => item.Primitive, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(item => item.Workflow)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var primitives = capabilities.Primitives
            .OrderBy(static primitive => primitive.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static primitive => primitive.Name, StringComparer.OrdinalIgnoreCase)
            .Select(primitive => MapPrimitive(primitive, exampleWorkflowsByPrimitive))
            .ToList();
        return Results.Ok(primitives);
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

    internal static async Task<IResult> GetActorGraphEnriched(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var snapshot = await queryService.GetActorSnapshotAsync(actorId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var subgraph = await queryService.GetActorGraphSubgraphAsync(actorId, depth, take, graphOptions, ct);
        return Results.Ok(new WorkflowActorGraphEnrichedHttpResponse(MapSnapshot(snapshot), MapGraphSubgraph(subgraph)));
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

    private static WorkflowPrimitiveDescriptorHttpResponse MapPrimitive(
        WorkflowPrimitiveCapability primitive,
        IReadOnlyDictionary<string, List<string>> exampleWorkflowsByPrimitive)
    {
        return new WorkflowPrimitiveDescriptorHttpResponse(
            primitive.Name,
            primitive.Aliases
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            primitive.Category,
            primitive.Description,
            primitive.Parameters.Select(MapPrimitiveParameter).ToList(),
            exampleWorkflowsByPrimitive.TryGetValue(primitive.Name, out var exampleWorkflows)
                ? exampleWorkflows
                : []);
    }

    private static WorkflowPrimitiveParameterDescriptorHttpResponse MapPrimitiveParameter(
        WorkflowPrimitiveParameterCapability parameter) =>
        new(
            parameter.Name,
            parameter.Type,
            parameter.Required,
            parameter.Description,
            parameter.Default,
            parameter.Enum
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList());
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

public sealed record WorkflowActorGraphEnrichedHttpResponse(
    WorkflowActorSnapshotHttpResponse Snapshot,
    WorkflowActorGraphSubgraphHttpResponse Subgraph);

public sealed record WorkflowPrimitiveParameterDescriptorHttpResponse(
    string Name,
    string Type,
    bool Required,
    string Description,
    string Default,
    List<string> EnumValues);

public sealed record WorkflowPrimitiveDescriptorHttpResponse(
    string Name,
    List<string> Aliases,
    string Category,
    string Description,
    List<WorkflowPrimitiveParameterDescriptorHttpResponse> Parameters,
    List<string> ExampleWorkflows);
