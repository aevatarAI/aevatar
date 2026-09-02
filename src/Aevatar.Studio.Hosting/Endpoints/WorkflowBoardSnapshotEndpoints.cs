using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class WorkflowBoardSnapshotEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(
                "/api/scopes/{scopeId}/workflow-board/snapshot",
                HandleSnapshotAsync)
            .WithTags("WorkflowBoard");
    }

    internal static async Task<IResult> HandleSnapshotAsync(
        HttpContext http,
        string scopeId,
        WorkflowBoardSnapshotHttpRequest? request,
        [FromServices] IWorkflowBoardSnapshotQueryPort snapshotQueryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (!TryMapRequest(scopeId, request, out var appRequest, out var errorMessage))
        {
            return BadRequest(errorMessage);
        }

        try
        {
            var snapshot = await snapshotQueryPort.GetSnapshotAsync(appRequest, ct);
            return Results.Ok(MapSnapshot(snapshot));
        }
        catch (WorkflowBoardSnapshotRequestException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (WorkflowBoardReadModelUnavailableException)
        {
            return Results.Json(
                new
                {
                    code = "WORKFLOW_BOARD_SNAPSHOT_UNAVAILABLE",
                    message = "Workflow board snapshot is temporarily unavailable.",
                },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static bool TryMapRequest(
        string scopeId,
        WorkflowBoardSnapshotHttpRequest? request,
        out WorkflowBoardSnapshotRequest appRequest,
        out string errorMessage)
    {
        appRequest = null!;
        errorMessage = string.Empty;

        if (request == null)
        {
            errorMessage = "request body is required.";
            return false;
        }

        try
        {
            if (request.ExtensionData is { Count: > 0 })
                throw new WorkflowBoardSnapshotRequestException("request contains unsupported fields.");

            appRequest = new WorkflowBoardSnapshotRequest(
                scopeId,
                request.TeamId,
                request.MemberId,
                request.Take);
            Validate(appRequest);
            return true;
        }
        catch (WorkflowBoardSnapshotRequestException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void Validate(WorkflowBoardSnapshotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            throw new WorkflowBoardSnapshotRequestException("scopeId is required.");

        if (request.TeamId != null && string.IsNullOrWhiteSpace(request.TeamId))
            throw new WorkflowBoardSnapshotRequestException("teamId must not be blank.");

        if (request.MemberId != null && string.IsNullOrWhiteSpace(request.MemberId))
            throw new WorkflowBoardSnapshotRequestException("memberId must not be blank.");

        if (request.MemberId != null && request.TeamId == null)
            throw new WorkflowBoardSnapshotRequestException("memberId requires teamId.");

        if (request.Take <= 0)
            throw new WorkflowBoardSnapshotRequestException("take must be greater than zero.");

        if (request.Take > WorkflowBoardSnapshotRequestLimits.MaxMemberRows)
            throw new WorkflowBoardSnapshotRequestException(
                $"take must be at most {WorkflowBoardSnapshotRequestLimits.MaxMemberRows}.");
    }

    private static WorkflowBoardSnapshotHttpResponse MapSnapshot(WorkflowBoardSnapshot snapshot) =>
        new(
            snapshot.ScopeId,
            snapshot.GeneratedAt,
            snapshot.Watermark,
            MapCounts(snapshot.Counts),
            snapshot.Teams.Select(MapTeam).ToArray(),
            snapshot.LastNodeUpdatedAt);

    private static WorkflowBoardSnapshotCountsHttpResponse MapCounts(WorkflowBoardSnapshotCounts counts) =>
        new(
            counts.Running,
            counts.Waiting,
            counts.Failed,
            counts.Retrying,
            counts.Completed);

    private static WorkflowBoardTeamSnapshotHttpResponse MapTeam(WorkflowBoardTeamSnapshot team) =>
        new(
            team.TeamId,
            team.TeamName,
            team.TotalMemberCount,
            team.Members.Select(MapMember).ToArray());

    private static WorkflowBoardMemberSnapshotHttpResponse MapMember(WorkflowBoardMemberSnapshot member) =>
        new(
            member.MemberId,
            member.DisplayName,
            MapExecutionAvailability(member.ExecutionAvailability),
            MapExecutionStatus(member.ExecutionStatus),
            member.Progress == null ? null : MapProgress(member.Progress),
            member.CompletedNodes.Select(MapCompletedNode).ToArray(),
            member.PendingNodes.Select(MapPendingNode).ToArray(),
            member.FailedNodes.Select(MapFailedNode).ToArray(),
            member.WorkflowId,
            member.WorkflowName,
            member.PublishedServiceId,
            member.ActorId,
            member.RoleSummary,
            member.CurrentExecutionId,
            member.CurrentNode == null ? null : MapCurrentNode(member.CurrentNode),
            member.LastNodeUpdatedAt);

    private static WorkflowBoardMemberProgressHttpResponse MapProgress(WorkflowBoardMemberProgress progress) =>
        new(progress.CompletedSteps, progress.TotalSteps);

    private static WorkflowBoardCurrentNodeHttpResponse MapCurrentNode(WorkflowBoardCurrentNode node) =>
        new(
            node.NodeId,
            node.Name,
            MapCurrentNodeStatus(node.Status),
            node.StartedAt,
            node.UpdatedAt,
            node.DurationMs);

    private static WorkflowBoardCompletedNodeHttpResponse MapCompletedNode(WorkflowBoardCompletedNode node) =>
        new(node.NodeId, node.Name, node.CompletedAt, node.DurationMs);

    private static WorkflowBoardPendingNodeHttpResponse MapPendingNode(WorkflowBoardPendingNode node) =>
        new(node.NodeId, node.Name, MapPendingNodeStatus(node.Status), node.Reason);

    private static WorkflowBoardFailedNodeHttpResponse MapFailedNode(WorkflowBoardFailedNode node) =>
        new(node.NodeId, node.Name, node.FailedAt);

    private static string MapExecutionAvailability(WorkflowBoardExecutionAvailability availability) =>
        availability switch
        {
            WorkflowBoardExecutionAvailability.Available => "available",
            WorkflowBoardExecutionAvailability.Unavailable => "unavailable",
            WorkflowBoardExecutionAvailability.PendingBackendContract => "pending_backend_contract",
            _ => "unknown",
        };

    private static string MapExecutionStatus(WorkflowBoardMemberExecutionStatus status) =>
        status switch
        {
            WorkflowBoardMemberExecutionStatus.Running => "running",
            WorkflowBoardMemberExecutionStatus.Waiting => "waiting",
            WorkflowBoardMemberExecutionStatus.Failed => "failed",
            WorkflowBoardMemberExecutionStatus.Retrying => "retrying",
            WorkflowBoardMemberExecutionStatus.Completed => "completed",
            WorkflowBoardMemberExecutionStatus.Stopped => "stopped",
            _ => "unknown",
        };

    private static string MapCurrentNodeStatus(WorkflowBoardCurrentNodeStatus status) =>
        status switch
        {
            WorkflowBoardCurrentNodeStatus.Running => "running",
            WorkflowBoardCurrentNodeStatus.Waiting => "waiting",
            WorkflowBoardCurrentNodeStatus.Pending => "pending",
            WorkflowBoardCurrentNodeStatus.Failed => "failed",
            WorkflowBoardCurrentNodeStatus.Completed => "completed",
            _ => "unknown",
        };

    private static string MapPendingNodeStatus(WorkflowBoardPendingNodeStatus status) =>
        status switch
        {
            WorkflowBoardPendingNodeStatus.Waiting => "waiting",
            WorkflowBoardPendingNodeStatus.Pending => "pending",
            WorkflowBoardPendingNodeStatus.Queued => "queued",
            _ => "unknown",
        };

    private static IResult BadRequest(string message) =>
        Results.BadRequest(new
        {
            code = "INVALID_WORKFLOW_BOARD_SNAPSHOT_REQUEST",
            message,
        });
}

public sealed record WorkflowBoardSnapshotHttpRequest(
    string? TeamId = null,
    string? MemberId = null,
    int? Take = null)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record WorkflowBoardSnapshotHttpResponse(
    string ScopeId,
    DateTimeOffset GeneratedAt,
    string Watermark,
    WorkflowBoardSnapshotCountsHttpResponse Counts,
    IReadOnlyList<WorkflowBoardTeamSnapshotHttpResponse> Teams,
    DateTimeOffset? LastNodeUpdatedAt = null);

public sealed record WorkflowBoardSnapshotCountsHttpResponse(
    int Running,
    int Waiting,
    int Failed,
    int Retrying,
    int Completed);

public sealed record WorkflowBoardTeamSnapshotHttpResponse(
    string TeamId,
    string TeamName,
    int? TotalMemberCount,
    IReadOnlyList<WorkflowBoardMemberSnapshotHttpResponse> Members);

public sealed record WorkflowBoardMemberSnapshotHttpResponse(
    string MemberId,
    string DisplayName,
    string ExecutionAvailability,
    string ExecutionStatus,
    WorkflowBoardMemberProgressHttpResponse? Progress,
    IReadOnlyList<WorkflowBoardCompletedNodeHttpResponse> CompletedNodes,
    IReadOnlyList<WorkflowBoardPendingNodeHttpResponse> PendingNodes,
    IReadOnlyList<WorkflowBoardFailedNodeHttpResponse> FailedNodes,
    string? WorkflowId = null,
    string? WorkflowName = null,
    string? PublishedServiceId = null,
    string? ActorId = null,
    string? RoleSummary = null,
    string? CurrentExecutionId = null,
    WorkflowBoardCurrentNodeHttpResponse? CurrentNode = null,
    DateTimeOffset? LastNodeUpdatedAt = null);

public sealed record WorkflowBoardMemberProgressHttpResponse(
    int CompletedSteps,
    int TotalSteps);

public sealed record WorkflowBoardCurrentNodeHttpResponse(
    string NodeId,
    string Name,
    string Status,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? UpdatedAt = null,
    long? DurationMs = null);

public sealed record WorkflowBoardCompletedNodeHttpResponse(
    string NodeId,
    string Name,
    DateTimeOffset? CompletedAt = null,
    long? DurationMs = null);

public sealed record WorkflowBoardPendingNodeHttpResponse(
    string NodeId,
    string Name,
    string Status,
    string? Reason = null);

public sealed record WorkflowBoardFailedNodeHttpResponse(
    string NodeId,
    string Name,
    DateTimeOffset? FailedAt = null);
