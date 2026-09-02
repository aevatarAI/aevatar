using System.Text.Json.Serialization;
using Aevatar.Capabilities;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class StudioMemberAutomationEndpoints
{
    private const string BasePath =
        "/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations";
    private static readonly EventId CreateAcceptedEventId =
        new(
            StudioMemberAutomationAuditContract.CreateAcceptedEventId,
            StudioMemberAutomationAuditContract.CreateAcceptedEventName);

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost($"{BasePath}/preflight", HandlePreflightAsync).WithTags("StudioTeamAutomations");
        app.MapGet(BasePath, HandleListAsync).WithTags("StudioTeamAutomations");
        app.MapPost(BasePath, HandleCreateAsync).WithTags("StudioTeamAutomations");
        app.MapGet($"{BasePath}/{{scheduleId}}", HandleGetAsync).WithTags("StudioTeamAutomations");
        app.MapPut($"{BasePath}/{{scheduleId}}", HandleUpdateAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/reauthorize", HandleReauthorizeAsync)
            .WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/pause", HandlePauseAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/resume", HandleResumeAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/run-now", HandleRunNowAsync).WithTags("StudioTeamAutomations");
        app.MapDelete($"{BasePath}/{{scheduleId}}", HandleDeleteAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/retry-revocation", HandleRetryRevocationAsync)
            .WithTags("StudioTeamAutomations");
    }

    internal static async Task<IResult> HandlePreflightAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        StudioMemberAutomationPreflightRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var authorization = await schedules.PreflightForWriteAsync(
                BuildScheduleRequest(
                    scopeId,
                    teamId,
                    memberId,
                    body,
                    authority.AuthenticatedOwner,
                    authority.ProvisioningBearerToken),
                ct);
            if (authorization.Success)
                return Results.Ok(authorization);

            loggerFactory.CreateLogger(StudioMemberAutomationAuditContract.Category).LogWarning(
                "Team automation preflight authorization failed. scope={ScopeId} team={TeamId} member={MemberId} " +
                "failureCode={FailureCode}",
                scopeId,
                teamId,
                memberId,
                authorization.FailureCode);
            return MapPreflightFailure(authorization.FailureCode);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        StudioMemberAutomationMutationRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var request = BuildScheduleRequest(
                scopeId,
                teamId,
                memberId,
                body,
                authority.AuthenticatedOwner,
                authority.ProvisioningBearerToken) with
            {
                OperationId = body.OperationId,
                IdempotencyKey = body.IdempotencyKey,
                CredentialProvisioningKind = body.CredentialProvisioningKind,
                ConfirmedPolicyVersion = body.ConfirmedPolicyVersion,
            };
            var result = await schedules.CreateAsync(request, body.ConfirmedPermissionDigest, ct);
            if (result.Success && result.NewOperationCommitted)
            {
                loggerFactory.CreateLogger(StudioMemberAutomationAuditContract.Category).LogInformation(
                    CreateAcceptedEventId,
                    "Accepted Studio member automation create for scope {ScopeId}, team {TeamId}, member {MemberId}, " +
                    "schedule {ScheduleId}, operation {OperationId}, and verified binding {BindingId}.",
                    scopeId,
                    teamId,
                    memberId,
                    result.ScheduleId,
                    result.OperationId,
                    authority.AuthenticatedOwner.VerifiedBindingId);
            }

            return Results.Accepted(value: new StudioMemberAutomationMutationReceipt(
                result.Success,
                result.Status,
                result.ScheduleId,
                result.OperationId,
                result.CommandId));
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleReauthorizeAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationMutationRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var request = BuildScheduleRequest(
                scopeId,
                teamId,
                memberId,
                body,
                authority.AuthenticatedOwner,
                authority.ProvisioningBearerToken) with
            {
                ScheduleId = scheduleId,
                OperationId = body.OperationId,
                IdempotencyKey = body.IdempotencyKey,
                CredentialProvisioningKind = body.CredentialProvisioningKind,
                ConfirmedPolicyVersion = body.ConfirmedPolicyVersion,
            };
            var result = await schedules.ReauthorizeAsync(request, body.ConfirmedPermissionDigest, ct);
            return Results.Accepted(value: new StudioMemberAutomationMutationReceipt(
                result.Success,
                result.Status,
                result.ScheduleId,
                result.OperationId,
                result.CommandId));
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        int? take,
        string? cursor,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            return Results.Ok(await schedules.ListAsync(
                scopeId,
                teamId,
                memberId,
                take ?? 50,
                cursor,
                includeTotalCount: true,
                ct));
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var result = await schedules.GetAsync(scopeId, teamId, memberId, scheduleId, ct);
            return result == null
                ? AutomationNotFound()
                : Results.Ok(result);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleUpdateAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationUpdateRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var receipt = await schedules.UpdateAsync(new StudioMemberAutomationUpdateCommand(
                scopeId,
                teamId,
                memberId,
                scheduleId,
                body.ScheduleCron,
                body.ScheduleTimezone ?? "UTC",
                body.Enabled,
                body.OperationId,
                body.IdempotencyKey,
                authority.AuthenticatedOwner)
            {
                DisplayName = body.DisplayName,
                Prompt = body.Prompt,
                ProvisioningBearerToken = authority.ProvisioningBearerToken,
            }, ct);
            return Results.Accepted(value: receipt);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static Task<IResult> HandlePauseAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationActionRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        CancellationToken ct) =>
        HandleActionAsync(http, scopeId, teamId, memberId, scheduleId, body, schedules.PauseAsync, ct);

    internal static Task<IResult> HandleResumeAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationActionRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        CancellationToken ct) =>
        HandleActionAsync(http, scopeId, teamId, memberId, scheduleId, body, schedules.ResumeAsync, ct);

    internal static Task<IResult> HandleRunNowAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationActionRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        CancellationToken ct) =>
        HandleActionAsync(http, scopeId, teamId, memberId, scheduleId, body, schedules.RunNowAsync, ct);

    internal static async Task<IResult> HandleDeleteAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        [FromBody] StudioMemberAutomationActionRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var receipt = await schedules.DeleteAsync(
                new StudioMemberAutomationActionCommand(
                    scopeId,
                    teamId,
                    memberId,
                    scheduleId,
                    body.OperationId,
                    body.IdempotencyKey)
                {
                    AuthenticatedOwner = authority.AuthenticatedOwner,
                    ProvisioningBearerToken = authority.ProvisioningBearerToken,
                },
                ct);
            return Results.Accepted(value: receipt);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    internal static async Task<IResult> HandleRetryRevocationAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var receipt = await schedules.RetryRevocationAsync(
                new StudioMemberAutomationRetryRevocationCommand(
                    scopeId,
                    teamId,
                    memberId,
                    scheduleId)
                {
                    AuthenticatedOwner = authority.AuthenticatedOwner,
                    ProvisioningBearerToken = authority.ProvisioningBearerToken,
                },
                ct);
            return Results.Accepted(value: receipt);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    private static async Task<IResult> HandleActionAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        StudioMemberAutomationActionRequest body,
        Func<StudioMemberAutomationActionCommand, CancellationToken, Task<StudioMemberAutomationMutationReceipt>> action,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var command = new StudioMemberAutomationActionCommand(
                scopeId,
                teamId,
                memberId,
                scheduleId,
                body.OperationId,
                body.IdempotencyKey);
            var receipt = await action(command, ct);
            return Results.Accepted(value: receipt);
        }
        catch (Exception ex) when (TryMapError(ex, scopeId, teamId, memberId, out var error))
        {
            return error;
        }
    }

    private static StudioMemberWorkflowScheduleRequest BuildScheduleRequest(
        string scopeId,
        string teamId,
        string memberId,
        StudioMemberAutomationPreflightRequest body,
        AuthenticatedAuthorizationOwnerContext owner,
        string? bearerToken) =>
        new(
            scopeId,
            memberId,
            body.ScheduleCron,
            body.ScheduleTimezone ?? "UTC",
            owner)
        {
            TeamId = teamId,
            Prompt = body.Prompt,
            DisplayName = body.DisplayName,
            Enabled = body.Enabled,
            ProvisioningBearerToken = bearerToken,
        };

    private static bool TryMapError(
        Exception exception,
        string scopeId,
        string teamId,
        string memberId,
        out IResult result)
    {
        result = exception switch
        {
            StudioMemberAutomationAuthorizationBindingRequiredException => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_AUTHORIZATION_BINDING_REQUIRED",
                    message = "Reconnect NyxID to authorize this automation.",
                },
                statusCode: StatusCodes.Status409Conflict),
            UnauthorizedAccessException => Results.Json(
                new { code = "TEAM_AUTOMATION_UNAUTHORIZED", message = exception.Message },
                statusCode: StatusCodes.Status401Unauthorized),
            StudioMemberAutomationNotFoundException => AutomationNotFound(),
            StudioMemberNotFoundException => AutomationNotFound(),
            ScheduledDispatchNotFoundException => AutomationNotFound(),
            StudioMemberAutomationProjectionPendingException pending => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
                    message = "The refreshed authorization catalog is still being projected. Retry this request.",
                    retryable = true,
                    requiredStateVersion = pending.RequiredStateVersion,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationCatalogRefreshSupersededException => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_SUPERSEDED",
                    message = "A newer authorization catalog refresh superseded this request. Retry this request.",
                    retryable = true,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationCatalogRefreshUnavailableException => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_AUTHORIZATION_REFRESH_UNAVAILABLE",
                    message = "The authorization catalog could not be refreshed. Retry this request.",
                    retryable = true,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationPlanConflictException conflict => Results.Json(
                new StudioMemberAutomationConflictResponse(
                    ToPlanConflictCode(conflict.Code),
                    ToPlanConflictMessage(conflict.Code),
                    BuildPreflightLocator(scopeId, teamId, memberId),
                    ScheduledAuthorizationPlanMismatchReasons.ToWireValue(
                        conflict.AuthorizationPlanMismatchReason)),
                statusCode: StatusCodes.Status409Conflict),
            ScheduledDispatchConflictException => Results.Conflict(
                new { code = "TEAM_AUTOMATION_CONFLICT", message = exception.Message }),
            InvalidOperationException => Results.BadRequest(
                new { code = "INVALID_TEAM_AUTOMATION_REQUEST", message = exception.Message }),
            ArgumentException => Results.BadRequest(
                new { code = "INVALID_TEAM_AUTOMATION_REQUEST", message = exception.Message }),
            _ => null!,
        };
        return result != null;
    }

    private static IResult NotFound(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status404NotFound);

    private static IResult AutomationNotFound() =>
        NotFound("TEAM_AUTOMATION_NOT_FOUND", "Team automation resource was not found.");

    private static IResult MapPreflightFailure(
        ScheduledInvocationAuthorizationFailureCode failureCode)
    {
        var (statusCode, code, message, retryable) = failureCode switch
        {
            ScheduledInvocationAuthorizationFailureCode.TargetInvalid => (
                StatusCodes.Status400BadRequest,
                "TEAM_AUTOMATION_AUTHORIZATION_TARGET_INVALID",
                "The automation authorization target is invalid.",
                false),
            ScheduledInvocationAuthorizationFailureCode.OwnerInvalid => (
                StatusCodes.Status400BadRequest,
                "TEAM_AUTOMATION_AUTHORIZATION_OWNER_INVALID",
                "The authenticated authorization owner is invalid.",
                false),
            ScheduledInvocationAuthorizationFailureCode.OwnerMismatch => (
                StatusCodes.Status403Forbidden,
                "TEAM_AUTOMATION_AUTHORIZATION_OWNER_MISMATCH",
                "The authorization owner does not match this automation.",
                false),
            ScheduledInvocationAuthorizationFailureCode.ServiceNotFound => (
                StatusCodes.Status403Forbidden,
                "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_NOT_FOUND",
                "One or more required services are not available to this automation.",
                false),
            ScheduledInvocationAuthorizationFailureCode.ServiceAmbiguous => (
                StatusCodes.Status403Forbidden,
                "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_AMBIGUOUS",
                "A required service could not be identified unambiguously.",
                false),
            ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied => (
                StatusCodes.Status403Forbidden,
                "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED",
                "This automation is not authorized to use one or more required services.",
                false),
            ScheduledInvocationAuthorizationFailureCode.NodeGrantMissing => (
                StatusCodes.Status403Forbidden,
                "TEAM_AUTOMATION_AUTHORIZATION_NODE_GRANT_MISSING",
                "This automation is missing a required service permission.",
                false),
            ScheduledInvocationAuthorizationFailureCode.AuthorizationPlanChanged => (
                StatusCodes.Status409Conflict,
                "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED",
                "The authorization plan changed. Run preflight again.",
                false),
            ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound => (
                StatusCodes.Status503ServiceUnavailable,
                "TEAM_AUTOMATION_AUTHORIZATION_SNAPSHOT_NOT_FOUND",
                "Authorization data is temporarily unavailable. Retry this request.",
                true),
            ScheduledInvocationAuthorizationFailureCode.SnapshotStale => (
                StatusCodes.Status503ServiceUnavailable,
                "TEAM_AUTOMATION_AUTHORIZATION_SNAPSHOT_STALE",
                "Authorization data is temporarily stale. Retry this request.",
                true),
            ScheduledInvocationAuthorizationFailureCode.DurableAuthorizationUnavailable => (
                StatusCodes.Status503ServiceUnavailable,
                "TEAM_AUTOMATION_AUTHORIZATION_DURABLE_AUTHORIZATION_UNAVAILABLE",
                "Authorization is temporarily unavailable. Retry this request.",
                true),
            ScheduledInvocationAuthorizationFailureCode.CatalogProjectionPending => (
                StatusCodes.Status503ServiceUnavailable,
                "TEAM_AUTOMATION_AUTHORIZATION_PROJECTION_PENDING",
                "The authorization catalog is still being projected. Retry this request.",
                true),
            ScheduledInvocationAuthorizationFailureCode.UnknownEnum => (
                StatusCodes.Status400BadRequest,
                "TEAM_AUTOMATION_AUTHORIZATION_UNKNOWN_ENUM",
                "The automation authorization request contains an unsupported value.",
                false),
            _ => (
                StatusCodes.Status400BadRequest,
                "TEAM_AUTOMATION_AUTHORIZATION_FAILED",
                "Authorization could not continue.",
                false),
        };

        return Results.Json(new { code, message, retryable }, statusCode: statusCode);
    }

    private static string ToPlanConflictCode(string code) => code switch
    {
        "authorization_plan_changed" => "TEAM_AUTOMATION_AUTHORIZATION_PLAN_CHANGED",
        "reauthorization_required" => "TEAM_AUTOMATION_REAUTHORIZATION_REQUIRED",
        _ => "TEAM_AUTOMATION_CONFLICT",
    };

    private static string ToPlanConflictMessage(string code) => code switch
    {
        "authorization_plan_changed" =>
            "The authorization plan changed. Run preflight again before retrying.",
        "reauthorization_required" =>
            "The automation requires a fresh authorization review before it can be updated.",
        _ => "The Team automation request conflicts with its current state.",
    };

    private static string BuildPreflightLocator(string scopeId, string teamId, string memberId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId.Trim())}/teams/{Uri.EscapeDataString(teamId.Trim())}" +
        $"/members/{Uri.EscapeDataString(memberId.Trim())}/automations/preflight";

    private sealed record StudioMemberAutomationConflictResponse(
        string code,
        string message,
        string preflightLocator,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? authorizationPlanMismatchReason);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record StudioMemberAutomationPreflightRequest(
    string ScheduleCron,
    string? ScheduleTimezone,
    string? Prompt,
    string? DisplayName,
    bool Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StudioMemberAutomationMutationRequest(
    string ScheduleCron,
    string? ScheduleTimezone,
    string? Prompt,
    string? DisplayName,
    bool Enabled,
    string ConfirmedPermissionDigest,
    string ConfirmedPolicyVersion,
    string CredentialProvisioningKind,
    string OperationId,
    string IdempotencyKey)
    : StudioMemberAutomationPreflightRequest(
        ScheduleCron,
        ScheduleTimezone,
        Prompt,
        DisplayName,
        Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StudioMemberAutomationUpdateRequest(
    string ScheduleCron,
    string? ScheduleTimezone,
    string? Prompt,
    string? DisplayName,
    bool Enabled,
    string OperationId,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StudioMemberAutomationActionRequest(
    string OperationId,
    string IdempotencyKey);
