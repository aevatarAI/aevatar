using System.Text.Json.Serialization;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
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
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

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

        group.MapGet("/workflow-actors/{actorId}/current-state", GetWorkflowActorCurrentState)
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-runs/{workflowRunId}/timeline-export", ListWorkflowRunTimelineExport)
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflow-runs/{workflowRunId}/graph-export/edges", ListWorkflowRunGraphExportEdges)
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflow-runs/{workflowRunId}/graph-export/enriched", GetWorkflowRunGraphExportEnriched)
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workflow-runs/{workflowRunId}/graph-export/subgraph", GetWorkflowRunGraphExportSubgraph)
            // security-allowlist: workflow standalone host is dev-only; production hosts must add .RequireAuthorization() -- see cluster-022
            .Produces(StatusCodes.Status200OK);

    }

    internal static async Task<IResult> ListAgents(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        try
        {
            var agents = await queryService.ListAgentsAsync(ct);
            return Results.Ok(agents);
        }
        catch (ProjectionIndexSchemaDriftException exception)
        {
            return ProjectionIndexDiagnostics.ToServiceUnavailableResult(
                "workflow_execution_current_state_projection_index_drift",
                "Workflow execution current-state projection index schema drift detected.",
                exception);
        }
    }

    internal static async Task<IResult> ListPrimitives(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var capabilities = await queryService.GetCapabilitiesAsync(ct);
        var catalog = await queryService.ListWorkflowCatalogAsync(ct);
        var exampleWorkflowsByPrimitive = catalog
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

    internal static async Task<IResult> ListWorkflowCatalog(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default) =>
        Results.Ok(await queryService.ListWorkflowCatalogAsync(ct));

    internal static async Task<IResult> GetCapabilities(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default) =>
        Results.Ok(await queryService.GetCapabilitiesAsync(ct));

    internal static async Task<IResult> GetWorkflowDetail(
        string workflowName,
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var detail = await queryService.GetWorkflowDetailAsync(workflowName, ct);
        return detail == null ? Results.NotFound() : Results.Ok(detail);
    }

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: HTTP query exposed /actors/{actorId} as actor snapshot inspection.
    //   New principle: HTTP query exposes /workflow-actors/{actorId}/current-state as a readmodel resource.
    internal static async Task<IResult> GetWorkflowActorCurrentState(
        string actorId,
        HttpContext http,
        IWorkflowExecutionScopeQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var currentState = await queryService.GetWorkflowActorCurrentStateAsync(scopeId, actorId, ct);
        return currentState == null ? Results.NotFound() : Results.Ok(MapCurrentState(currentState));
    }

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    internal static async Task<IResult> ListWorkflowRunTimelineExport(
        string workflowRunId,
        HttpContext http,
        IWorkflowExecutionScopeQueryApplicationService queryService,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var timeline = await queryService.ListWorkflowRunTimelineExportAsync(scopeId, workflowRunId, take, ct);
        if (timeline == null)
            return Results.NotFound();

        return Results.Ok(timeline.Select(MapTimelineItem));
    }

    internal static async Task<IResult> ListWorkflowRunGraphExportEdges(
        string workflowRunId,
        HttpContext http,
        IWorkflowExecutionScopeQueryApplicationService queryService,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var edges = await queryService.ListWorkflowRunGraphExportEdgesAsync(
            scopeId,
            workflowRunId,
            take,
            graphOptions,
            ct);
        if (edges == null)
            return Results.NotFound();

        return Results.Ok(edges.Select(MapGraphEdge));
    }

    internal static async Task<IResult> GetWorkflowRunGraphExportEnriched(
        string workflowRunId,
        HttpContext http,
        IWorkflowExecutionScopeQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var subgraph = await queryService.GetWorkflowRunGraphExportSubgraphAsync(
            scopeId,
            workflowRunId,
            depth,
            take,
            graphOptions,
            ct);
        if (subgraph == null)
            return Results.NotFound();

        return Results.Ok(MapGraphSubgraph(subgraph));
    }

    internal static async Task<IResult> GetWorkflowRunGraphExportSubgraph(
        string workflowRunId,
        HttpContext http,
        IWorkflowExecutionScopeQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        if (!Aevatar.Capabilities.AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var subgraph = await queryService.GetWorkflowRunGraphExportSubgraphAsync(
            scopeId,
            workflowRunId,
            depth,
            take,
            graphOptions,
            ct);
        if (subgraph == null)
            return Results.NotFound();

        return Results.Ok(MapGraphSubgraph(subgraph));
    }

    private static WorkflowRunGraphExportQueryOptions BuildGraphQueryOptions(
        string? direction,
        string[]? edgeTypes)
    {
        return new WorkflowRunGraphExportQueryOptions
        {
            Direction = ParseDirection(direction),
            EdgeTypes = NormalizeEdgeTypes(edgeTypes),
        };
    }

    private static WorkflowRunGraphExportDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return WorkflowRunGraphExportDirection.Both;

        return Enum.TryParse<WorkflowRunGraphExportDirection>(direction.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : WorkflowRunGraphExportDirection.Both;
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

    private static WorkflowActorCurrentStateHttpResponse MapCurrentState(WorkflowActorSnapshot snapshot) =>
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
            snapshot.SagaStatus,
            MapDeadLetter(snapshot),
            snapshot.TotalSteps,
            snapshot.RequestedSteps,
            snapshot.CompletedSteps,
            snapshot.RoleReplyCount,
            snapshot.ConnectorApprovals.Select(MapConnectorApproval).ToList());

    private static WorkflowExternalActionApprovalHttpResponse MapConnectorApproval(
        WorkflowExternalActionApprovalSnapshot snapshot)
    {
        var plan = snapshot.Plan ?? new WorkflowExternalActionPlan();
        var provenance = plan.Provenance ?? new WorkflowExternalActionProvenance();
        return new WorkflowExternalActionApprovalHttpResponse(
            plan.ActionId,
            plan.Summary,
            plan.ServiceRef,
            plan.NodeId,
            plan.ConnectorName,
            plan.ConnectorType,
            plan.Operation,
            plan.HttpVerb,
            plan.Resource,
            plan.PermissionScope,
            provenance.ScopeId,
            provenance.TeamId,
            provenance.MemberId,
            provenance.WorkflowId,
            provenance.PublishedServiceId,
            provenance.WorkflowActorId,
            provenance.RunId,
            provenance.StepId,
            provenance.ExecutionId,
            provenance.RequesterPlatform,
            provenance.RequesterTenant,
            provenance.RequesterExternalUserId,
            provenance.PrincipalSubject,
            provenance.RequesterCapabilityScope,
            plan.CreatedAt?.ToDateTimeOffset(),
            plan.ExpiresAt?.ToDateTimeOffset(),
            snapshot.RemoteExpiresAt?.ToDateTimeOffset(),
            snapshot.LifecycleStatus,
            snapshot.ApprovalStatus,
            snapshot.ApprovalReasonCode,
            snapshot.ApprovalResolvedAt?.ToDateTimeOffset(),
            snapshot.ExecutionStatus,
            snapshot.ExecutionReasonCode,
            snapshot.ExecutionStartedAt?.ToDateTimeOffset(),
            snapshot.ExecutionCompletedAt?.ToDateTimeOffset());
    }

    private static WorkflowCompensationDeadLetterHttpResponse? MapDeadLetter(WorkflowActorSnapshot snapshot)
    {
        if (snapshot.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter)
            return null;

        return new WorkflowCompensationDeadLetterHttpResponse(
            snapshot.DeadLetterFailedCompensationStepId,
            snapshot.DeadLetterRemainingUncompensated,
            snapshot.DeadLetterError);
    }

    private static WorkflowRunTimelineExportItemHttpResponse MapTimelineItem(WorkflowRunTimelineExportItem item) =>
        new(
            item.Timestamp,
            item.Stage,
            item.Message,
            item.AgentId,
            item.StepId,
            item.StepType,
            item.EventType,
            item.Data.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowRunGraphExportNodeHttpResponse MapGraphNode(WorkflowRunGraphExportNode node) =>
        new(
            node.NodeId,
            node.NodeType,
            node.UpdatedAt,
            node.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowRunGraphExportEdgeHttpResponse MapGraphEdge(WorkflowRunGraphExportEdge edge) =>
        new(
            edge.EdgeId,
            edge.FromNodeId,
            edge.ToNodeId,
            edge.EdgeType,
            edge.UpdatedAt,
            edge.Properties.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static WorkflowRunGraphExportSubgraphHttpResponse MapGraphSubgraph(WorkflowRunGraphExportSubgraph subgraph) =>
        new(
            subgraph.RootNodeId,
            subgraph.SourceStateVersion,
            subgraph.RouteFingerprint == null
                ? null
                : new WorkflowRunGraphExportRouteFingerprintHttpResponse(
                    subgraph.RouteFingerprint.ContractId,
                    subgraph.RouteFingerprint.ContractVersion,
                    subgraph.RouteFingerprint.PhysicalNamespace,
                    subgraph.RouteFingerprint.RouteEpoch),
            subgraph.SourceCoordinate == null
                ? null
                : new WorkflowRunGraphExportSourceCoordinateHttpResponse(
                    subgraph.SourceCoordinate.ActorId,
                    subgraph.SourceCoordinate.StateVersion,
                    subgraph.SourceCoordinate.EventId),
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

// Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
//   Old pattern: HTTP response was named WorkflowActorSnapshotHttpResponse and serialized actorId.
//   New principle: HTTP response is workflow actor current-state shaped and serializes actorId.
public sealed record WorkflowActorCurrentStateHttpResponse(
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
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowSagaStatus SagaStatus,
    WorkflowCompensationDeadLetterHttpResponse? DeadLetter,
    int TotalSteps,
    int RequestedSteps,
    int CompletedSteps,
    int RoleReplyCount,
    IReadOnlyList<WorkflowExternalActionApprovalHttpResponse> ConnectorApprovals);

public sealed record WorkflowExternalActionApprovalHttpResponse(
    string ActionId,
    string Summary,
    string ServiceRef,
    string NodeId,
    string ConnectorName,
    string ConnectorType,
    string Operation,
    string HttpVerb,
    string Resource,
    string PermissionScope,
    string ScopeId,
    string TeamId,
    string MemberId,
    string WorkflowId,
    string PublishedServiceId,
    string WorkflowActorId,
    string RunId,
    string StepId,
    string ExecutionId,
    string RequesterPlatform,
    string RequesterTenant,
    string RequesterExternalUserId,
    string PrincipalSubject,
    string RequesterCapabilityScope,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RemoteExpiresAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowExternalActionLifecycleStatus LifecycleStatus,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowExternalActionApprovalStatus ApprovalStatus,
    string ApprovalReasonCode,
    DateTimeOffset? ApprovalResolvedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowExternalActionExecutionStatus ExecutionStatus,
    string ExecutionReasonCode,
    DateTimeOffset? ExecutionStartedAt,
    DateTimeOffset? ExecutionCompletedAt);

public sealed record WorkflowCompensationDeadLetterHttpResponse(
    string FailedCompensationStepId,
    int RemainingUncompensated,
    string Error);

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: HTTP timeline responses exposed actor timeline readmodel names.
//   New principle: HTTP timeline responses expose workflow-run artifact export semantics.
public sealed record WorkflowRunTimelineExportItemHttpResponse(
    DateTimeOffset Timestamp,
    string Stage,
    string Message,
    string AgentId,
    string StepId,
    string StepType,
    string EventType,
    Dictionary<string, string> Data);

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: HTTP graph responses exposed actor graph readmodel names.
//   New principle: HTTP graph responses expose workflow-run graph export artifact semantics.
public sealed record WorkflowRunGraphExportNodeHttpResponse(
    string NodeId,
    string NodeType,
    DateTimeOffset UpdatedAt,
    Dictionary<string, string> Properties);

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: HTTP graph responses exposed actor graph readmodel names.
//   New principle: HTTP graph responses expose workflow-run graph export artifact semantics.
public sealed record WorkflowRunGraphExportEdgeHttpResponse(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    string EdgeType,
    DateTimeOffset UpdatedAt,
    Dictionary<string, string> Properties);

// Refactor (iter29/cluster-029-workflow-history-artifact):
//   Old pattern: HTTP graph responses exposed actor graph readmodel names.
//   New principle: HTTP graph responses expose workflow-run graph export artifact semantics.
public sealed record WorkflowRunGraphExportSubgraphHttpResponse(
    string RootNodeId,
    long SourceStateVersion,
    WorkflowRunGraphExportRouteFingerprintHttpResponse? RouteFingerprint,
    WorkflowRunGraphExportSourceCoordinateHttpResponse? SourceCoordinate,
    List<WorkflowRunGraphExportNodeHttpResponse> Nodes,
    List<WorkflowRunGraphExportEdgeHttpResponse> Edges);

public sealed record WorkflowRunGraphExportRouteFingerprintHttpResponse(
    string ContractId,
    long ContractVersion,
    string PhysicalNamespace,
    long RouteEpoch);

public sealed record WorkflowRunGraphExportSourceCoordinateHttpResponse(
    string ActorId,
    long StateVersion,
    string EventId);

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
