using System.Text.Json.Serialization;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.ProjectionRecovery;

internal static class ProjectionVersionRegressionRepairAdminEndpoints
{
    private const string WorkspaceRoute =
        "/api/admin/scheduled-agent-key/projection-repair/workspace";
    private const string CatalogRoute =
        "/api/admin/scheduled-agent-key/projection-repair/nyxid-catalog";

    public static IEndpointRouteBuilder MapProjectionVersionRegressionRepairAdminEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost(WorkspaceRoute, HandleWorkspaceRouteAsync)
            .WithTags("ScheduledAgentKeyProjectionRepairAdmin");
        app.MapPost(CatalogRoute, HandleCatalogRouteAsync)
            .WithTags("ScheduledAgentKeyProjectionRepairAdmin");
        return app;
    }

    internal static async Task<IResult> HandleWorkspaceAsync(
        HttpContext http,
        WorkspaceRepairRequest? request,
        IPlatformAdminAuthorizer? authorizer,
        IStudioWorkspaceVersionRegressionRepairService? service,
        CancellationToken ct)
    {
        var authorization = await AuthorizeAsync(http, authorizer, ct);
        if (authorization.Error is not null)
            return authorization.Error;
        if (service is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (request is null || string.IsNullOrWhiteSpace(request.ScopeId))
            return InvalidRequest();

        if (!request.Apply)
        {
            return await ExecuteDownstreamAsync(
                async () =>
                {
                    var inspection = await service.InspectAsync(request.ScopeId, ct);
                    return Results.Json(ToInspectionResponse(inspection));
                },
                ct);
        }

        if (!IsValidApplyManifest(
                request.ExpectedActorId,
                request.ExpectedSourceStateVersion,
                request.ExpectedDocumentStateVersion,
                request.ExpectedDocumentLastEventId,
                request.RepairRequestId,
                request.RepairReason))
        {
            return InvalidRequest();
        }

        return await ExecuteDownstreamAsync(
            async () =>
            {
                var result = await service.RepairAsync(
                    new StudioWorkspaceVersionRegressionRepairRequest(
                        request.ScopeId,
                        request.ExpectedActorId,
                        request.ExpectedSourceStateVersion,
                        request.ExpectedDocumentStateVersion,
                        request.ExpectedDocumentLastEventId,
                        request.RepairRequestId,
                        request.RepairReason,
                        authorization.Caller!.UserId),
                    ct);
                var response = ToWorkspaceRepairResponse(result);
                return result.Status switch
                {
                    StudioWorkspaceVersionRegressionRepairStatus.Accepted =>
                        Results.Json(response, statusCode: StatusCodes.Status202Accepted),
                    StudioWorkspaceVersionRegressionRepairStatus.Conflict =>
                        Results.Json(response, statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Json(
                        response,
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            },
            ct);
    }

    internal static async Task<IResult> HandleCatalogAsync(
        HttpContext http,
        CatalogRepairRequest? request,
        IPlatformAdminAuthorizer? authorizer,
        INyxIdAuthorizationCatalogVersionRegressionRepairService? service,
        CancellationToken ct)
    {
        var authorization = await AuthorizeAsync(http, authorizer, ct);
        if (authorization.Error is not null)
            return authorization.Error;
        if (service is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (request is null)
            return InvalidRequest();

        var caller = authorization.Caller!;
        if (!request.Apply)
        {
            return await ExecuteDownstreamAsync(
                async () =>
                {
                    var inspection = await service.InspectPersonalAsync(caller.UserId, ct);
                    return Results.Json(ToInspectionResponse(inspection));
                },
                ct);
        }

        if (!IsValidApplyManifest(
                request.ExpectedActorId,
                request.ExpectedSourceStateVersion,
                request.ExpectedDocumentStateVersion,
                request.ExpectedDocumentLastEventId,
                request.RepairRequestId,
                request.RepairReason))
        {
            return InvalidRequest();
        }

        return await ExecuteDownstreamAsync(
            async () =>
            {
                var result = await service.RepairPersonalAsync(
                    new NyxIdAuthorizationCatalogVersionRegressionRepairRequest(
                        VerifiedOwnerSubject: caller.UserId,
                        request.ExpectedActorId,
                        BearerToken: authorization.BearerToken,
                        request.ExpectedSourceStateVersion,
                        request.ExpectedDocumentStateVersion,
                        request.ExpectedDocumentLastEventId,
                        request.RepairRequestId,
                        request.RepairReason,
                        RequestedBySubjectId: caller.UserId),
                    ct);
                var response = ToCatalogRepairResponse(result);
                return result.Status switch
                {
                    NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready =>
                        Results.Json(response),
                    NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending =>
                        Results.Json(response, statusCode: StatusCodes.Status202Accepted),
                    NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict =>
                        Results.Json(response, statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Json(
                        response,
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                };
            },
            ct);
    }

    private static Task<IResult> HandleWorkspaceRouteAsync(
        HttpContext http,
        [FromBody] WorkspaceRepairRequest? request,
        CancellationToken ct) =>
        HandleWorkspaceAsync(
            http,
            request,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            http.RequestServices.GetService<IStudioWorkspaceVersionRegressionRepairService>(),
            ct);

    private static Task<IResult> HandleCatalogRouteAsync(
        HttpContext http,
        [FromBody] CatalogRepairRequest? request,
        CancellationToken ct) =>
        HandleCatalogAsync(
            http,
            request,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            http.RequestServices.GetService<INyxIdAuthorizationCatalogVersionRegressionRepairService>(),
            ct);

    private static async Task<AuthorizationResult> AuthorizeAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? authorizer,
        CancellationToken ct)
    {
        if (authorizer is null)
        {
            return new AuthorizationResult(
                string.Empty,
                null,
                Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        var authorization = http.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(bearer))
            return new AuthorizationResult(string.Empty, null, Results.Forbid());

        PlatformCaller caller;
        try
        {
            caller = await authorizer.ResolveCallerAsync(bearer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuthorizationResult(string.Empty, null, Results.Forbid());
        }

        if (!caller.IsElevated || string.IsNullOrWhiteSpace(caller.UserId))
            return new AuthorizationResult(string.Empty, null, Results.Forbid());

        return new AuthorizationResult(bearer, caller, null);
    }

    private static async Task<IResult> ExecuteDownstreamAsync(
        Func<Task<IResult>> execute,
        CancellationToken ct)
    {
        try
        {
            return await execute();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static bool IsValidApplyManifest(
        string expectedActorId,
        long expectedSourceStateVersion,
        long expectedDocumentStateVersion,
        string expectedDocumentLastEventId,
        string repairRequestId,
        string repairReason) =>
        !string.IsNullOrWhiteSpace(expectedActorId) &&
        expectedSourceStateVersion > 0 &&
        expectedDocumentStateVersion > expectedSourceStateVersion &&
        !string.IsNullOrWhiteSpace(expectedDocumentLastEventId) &&
        !string.IsNullOrWhiteSpace(repairRequestId) &&
        !string.IsNullOrWhiteSpace(repairReason);

    private static IResult InvalidRequest() =>
        Results.BadRequest(new ErrorResponse("invalid_repair_request"));

    private static InspectionResponse ToInspectionResponse(
        StudioWorkspaceVersionRegressionInspection inspection) =>
        new(
            "inspection",
            inspection.ActorId,
            inspection.SourceStateVersion,
            inspection.DocumentStateVersion,
            inspection.DocumentLastEventId,
            inspection.DocumentActorId,
            inspection.Repairable);

    private static InspectionResponse ToInspectionResponse(
        NyxIdAuthorizationCatalogVersionRegressionInspection inspection) =>
        new(
            "inspection",
            inspection.ActorId,
            inspection.SourceStateVersion,
            inspection.DocumentStateVersion,
            inspection.DocumentLastEventId,
            inspection.DocumentActorId,
            inspection.Repairable);

    private static WorkspaceRepairResponse ToWorkspaceRepairResponse(
        StudioWorkspaceVersionRegressionRepairResult result) =>
        new(
            WorkspaceStatus(result.Status),
            result.Inspection.ActorId,
            result.Inspection.SourceStateVersion,
            result.Inspection.DocumentStateVersion,
            result.Inspection.DocumentLastEventId,
            result.RepairRequestId,
            result.CommandId,
            WorkspaceDeleteStatus(result.DeleteDisposition));

    private static CatalogRepairResponse ToCatalogRepairResponse(
        NyxIdAuthorizationCatalogVersionRegressionRepairResult result) =>
        new(
            CatalogStatus(result.Status),
            result.Inspection.ActorId,
            result.Inspection.SourceStateVersion,
            result.Inspection.DocumentStateVersion,
            result.Inspection.DocumentLastEventId,
            result.RepairRequestId,
            CatalogDeleteStatus(result.DeleteDisposition),
            RefreshStatus(result.Refresh?.Status),
            result.Refresh?.StateVersion,
            VisibilityStatus(result.Visibility?.Status),
            result.Visibility?.RequiredStateVersion,
            result.Visibility?.VisibleStateVersion);

    private static string WorkspaceStatus(
        StudioWorkspaceVersionRegressionRepairStatus status) =>
        status switch
        {
            StudioWorkspaceVersionRegressionRepairStatus.Accepted => "accepted",
            StudioWorkspaceVersionRegressionRepairStatus.Conflict => "conflict",
            _ => "unavailable",
        };

    private static string WorkspaceDeleteStatus(
        StudioWorkspaceReplicaDeleteDisposition? status) =>
        status switch
        {
            StudioWorkspaceReplicaDeleteDisposition.Deleted => "deleted",
            StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent => "already_absent",
            StudioWorkspaceReplicaDeleteDisposition.SourceChanged => "source_changed",
            StudioWorkspaceReplicaDeleteDisposition.DocumentChanged => "document_changed",
            StudioWorkspaceReplicaDeleteDisposition.RevisionConflict => "revision_conflict",
            _ => string.Empty,
        };

    private static string CatalogStatus(
        NyxIdAuthorizationCatalogVersionRegressionRepairStatus status) =>
        status switch
        {
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict => "conflict",
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Failed => "failed",
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready => "ready",
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending =>
                "projection_pending",
            _ => "unavailable",
        };

    private static string CatalogDeleteStatus(
        NyxIdAuthorizationCatalogReplicaDeleteDisposition? status) =>
        status switch
        {
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted => "deleted",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent => "already_absent",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged => "source_changed",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.DocumentChanged => "document_changed",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.RevisionConflict => "revision_conflict",
            _ => string.Empty,
        };

    private static string RefreshStatus(NyxIdAuthorizationCatalogRefreshStatus? status) =>
        status switch
        {
            NyxIdAuthorizationCatalogRefreshStatus.Observed => "observed",
            NyxIdAuthorizationCatalogRefreshStatus.AccessDenied => "access_denied",
            NyxIdAuthorizationCatalogRefreshStatus.Failed => "failed",
            NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut => "observation_timed_out",
            NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported => "owner_not_supported",
            NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable => "catalog_unstable",
            NyxIdAuthorizationCatalogRefreshStatus.Superseded => "superseded",
            _ => "unspecified",
        };

    private static string VisibilityStatus(NyxIdAuthorizationCatalogVisibilityStatus? status) =>
        status switch
        {
            NyxIdAuthorizationCatalogVisibilityStatus.Ready => "ready",
            NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending => "projection_pending",
            NyxIdAuthorizationCatalogVisibilityStatus.OwnerMismatch => "owner_mismatch",
            NyxIdAuthorizationCatalogVisibilityStatus.Invalidated => "invalidated",
            NyxIdAuthorizationCatalogVisibilityStatus.Stale => "stale",
            NyxIdAuthorizationCatalogVisibilityStatus.Invalid => "invalid",
            NyxIdAuthorizationCatalogVisibilityStatus.Unavailable => "unavailable",
            _ => "unspecified",
        };

    internal sealed record WorkspaceRepairRequest(
        [property: JsonPropertyName("scope_id")] string ScopeId,
        [property: JsonPropertyName("apply")] bool Apply,
        [property: JsonPropertyName("expected_actor_id")] string ExpectedActorId,
        [property: JsonPropertyName("expected_source_state_version")] long ExpectedSourceStateVersion,
        [property: JsonPropertyName("expected_document_state_version")] long ExpectedDocumentStateVersion,
        [property: JsonPropertyName("expected_document_last_event_id")] string ExpectedDocumentLastEventId,
        [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
        [property: JsonPropertyName("repair_reason")] string RepairReason);

    internal sealed record CatalogRepairRequest(
        [property: JsonPropertyName("apply")] bool Apply,
        [property: JsonPropertyName("expected_actor_id")] string ExpectedActorId,
        [property: JsonPropertyName("expected_source_state_version")] long ExpectedSourceStateVersion,
        [property: JsonPropertyName("expected_document_state_version")] long ExpectedDocumentStateVersion,
        [property: JsonPropertyName("expected_document_last_event_id")] string ExpectedDocumentLastEventId,
        [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
        [property: JsonPropertyName("repair_reason")] string RepairReason);

    private sealed class AuthorizationResult
    {
        public AuthorizationResult(
            string bearerToken,
            PlatformCaller? caller,
            IResult? error)
        {
            BearerToken = bearerToken;
            Caller = caller;
            Error = error;
        }

        public string BearerToken { get; }

        public PlatformCaller? Caller { get; }

        public IResult? Error { get; }
    }

    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string Error);

    private sealed record InspectionResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("actor_id")] string ActorId,
        [property: JsonPropertyName("source_state_version")] long SourceStateVersion,
        [property: JsonPropertyName("document_state_version")] long? DocumentStateVersion,
        [property: JsonPropertyName("document_last_event_id")] string DocumentLastEventId,
        [property: JsonPropertyName("document_actor_id")] string DocumentActorId,
        [property: JsonPropertyName("repairable")] bool Repairable);

    private sealed record WorkspaceRepairResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("actor_id")] string ActorId,
        [property: JsonPropertyName("source_state_version")] long SourceStateVersion,
        [property: JsonPropertyName("document_state_version")] long? DocumentStateVersion,
        [property: JsonPropertyName("document_last_event_id")] string DocumentLastEventId,
        [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
        [property: JsonPropertyName("command_id")] string CommandId,
        [property: JsonPropertyName("delete_status")] string DeleteStatus);

    private sealed record CatalogRepairResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("actor_id")] string ActorId,
        [property: JsonPropertyName("source_state_version")] long SourceStateVersion,
        [property: JsonPropertyName("document_state_version")] long? DocumentStateVersion,
        [property: JsonPropertyName("document_last_event_id")] string DocumentLastEventId,
        [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
        [property: JsonPropertyName("delete_status")] string DeleteStatus,
        [property: JsonPropertyName("refresh_status")] string RefreshStatus,
        [property: JsonPropertyName("refresh_state_version")] long? RefreshStateVersion,
        [property: JsonPropertyName("visibility_status")] string VisibilityStatus,
        [property: JsonPropertyName("required_state_version")] long? RequiredStateVersion,
        [property: JsonPropertyName("visible_state_version")] long? VisibleStateVersion);
}
