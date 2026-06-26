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

        if (request.TeamSelections == null || request.TeamSelections.Count == 0)
        {
            errorMessage = "teamSelections is required.";
            return false;
        }

        if (request.TeamSelections.Count > WorkflowBoardSnapshotRequestLimits.MaxSelectedTeams)
        {
            errorMessage = $"At most {WorkflowBoardSnapshotRequestLimits.MaxSelectedTeams} teams can be selected.";
            return false;
        }

        if (request.PreviousWatermark != null)
        {
            if (string.IsNullOrWhiteSpace(request.PreviousWatermark))
            {
                errorMessage = "previousWatermark must not be blank when present.";
                return false;
            }

            if (request.PreviousWatermark.Length > WorkflowBoardSnapshotRequestLimits.MaxPreviousWatermarkLength)
            {
                errorMessage =
                    $"previousWatermark must be at most {WorkflowBoardSnapshotRequestLimits.MaxPreviousWatermarkLength} characters.";
                return false;
            }
        }

        var selections = new List<WorkflowBoardTeamSelection>();
        var selectedMemberCount = 0;
        var seenRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in request.TeamSelections)
        {
            if (string.IsNullOrWhiteSpace(selection.TeamId))
            {
                errorMessage = "teamId is required.";
                return false;
            }

            if (selection.MemberIds == null || selection.MemberIds.Count == 0)
            {
                errorMessage = "memberIds is required.";
                return false;
            }

            if (selection.MemberIds.Any(static memberId => string.IsNullOrWhiteSpace(memberId)))
            {
                errorMessage = "memberIds must not contain blank values.";
                return false;
            }

            var teamId = selection.TeamId.Trim();
            var memberIds = new List<string>();
            foreach (var rawMemberId in selection.MemberIds)
            {
                var memberId = rawMemberId.Trim();
                if (!seenRows.Add($"{teamId}\u001f{memberId}"))
                    continue;

                memberIds.Add(memberId);
                selectedMemberCount++;
            }

            if (memberIds.Count > 0)
                selections.Add(new WorkflowBoardTeamSelection(teamId, memberIds));
        }

        if (selectedMemberCount == 0)
        {
            errorMessage = "memberIds is required.";
            return false;
        }

        if (selectedMemberCount > WorkflowBoardSnapshotRequestLimits.MaxSelectedMembers)
        {
            errorMessage =
                $"At most {WorkflowBoardSnapshotRequestLimits.MaxSelectedMembers} members can be selected.";
            return false;
        }

        appRequest = new WorkflowBoardSnapshotRequest(scopeId, selections, request.PreviousWatermark);
        return true;
    }

    private static WorkflowBoardSnapshotHttpResponse MapSnapshot(WorkflowBoardSnapshot snapshot) =>
        new(
            snapshot.ScopeId,
            snapshot.GeneratedAt,
            snapshot.Watermark,
            MapTotals(snapshot.Totals),
            snapshot.Teams.Select(MapTeam).ToArray(),
            snapshot.InvalidSelections.Select(MapInvalidSelection).ToArray(),
            snapshot.LastNodeUpdatedAt);

    private static WorkflowBoardTotalsHttpResponse MapTotals(WorkflowBoardTotals totals) =>
        new(
            totals.CompletedSteps,
            totals.RunningNodes,
            totals.WaitingOrPendingNodes,
            totals.FailedNodes);

    private static WorkflowBoardTeamSnapshotHttpResponse MapTeam(WorkflowBoardTeamSnapshot team) =>
        new(
            team.TeamId,
            team.TeamName,
            team.TotalMemberCount,
            team.SelectedMemberCount,
            team.Members.Select(MapMember).ToArray());

    private static WorkflowBoardMemberSnapshotHttpResponse MapMember(WorkflowBoardMemberSnapshot member) =>
        new(
            member.MemberId,
            member.DisplayName,
            MapExecutionAvailability(member.ExecutionAvailability),
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

    private static WorkflowBoardInvalidSelectionHttpResponse MapInvalidSelection(
        WorkflowBoardInvalidSelection invalidSelection) =>
        new(
            invalidSelection.TeamId,
            invalidSelection.MemberId,
            MapInvalidSelectionReason(invalidSelection.Reason),
            invalidSelection.Message);

    private static string MapExecutionAvailability(WorkflowBoardExecutionAvailability availability) =>
        availability switch
        {
            WorkflowBoardExecutionAvailability.Available => "available",
            WorkflowBoardExecutionAvailability.Unavailable => "unavailable",
            WorkflowBoardExecutionAvailability.PendingBackendContract => "pending_backend_contract",
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

    private static string MapInvalidSelectionReason(WorkflowBoardInvalidSelectionReason reason) =>
        reason switch
        {
            WorkflowBoardInvalidSelectionReason.TeamNotFound => "team_not_found",
            WorkflowBoardInvalidSelectionReason.MemberNotFound => "member_not_found",
            WorkflowBoardInvalidSelectionReason.MemberNotInTeam => "member_not_in_team",
            WorkflowBoardInvalidSelectionReason.Unauthorized => "unauthorized",
            WorkflowBoardInvalidSelectionReason.Archived => "archived",
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
    IReadOnlyList<WorkflowBoardTeamSelectionHttpRequest>? TeamSelections = null,
    string? PreviousWatermark = null);

public sealed record WorkflowBoardTeamSelectionHttpRequest(
    string? TeamId = null,
    IReadOnlyList<string>? MemberIds = null);

public sealed record WorkflowBoardSnapshotHttpResponse(
    string ScopeId,
    DateTimeOffset GeneratedAt,
    string Watermark,
    WorkflowBoardTotalsHttpResponse Totals,
    IReadOnlyList<WorkflowBoardTeamSnapshotHttpResponse> Teams,
    IReadOnlyList<WorkflowBoardInvalidSelectionHttpResponse> InvalidSelections,
    DateTimeOffset? LastNodeUpdatedAt = null);

public sealed record WorkflowBoardTotalsHttpResponse(
    int? CompletedSteps,
    int? RunningNodes,
    int? WaitingOrPendingNodes,
    int? FailedNodes);

public sealed record WorkflowBoardTeamSnapshotHttpResponse(
    string TeamId,
    string TeamName,
    int? TotalMemberCount,
    int SelectedMemberCount,
    IReadOnlyList<WorkflowBoardMemberSnapshotHttpResponse> Members);

public sealed record WorkflowBoardMemberSnapshotHttpResponse(
    string MemberId,
    string DisplayName,
    string ExecutionAvailability,
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

public sealed record WorkflowBoardInvalidSelectionHttpResponse(
    string TeamId,
    string? MemberId,
    string Reason,
    string Message);
