using System.Security.Claims;
using System.Text.Json.Serialization;
using Aevatar.Capabilities;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
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
        app.MapPost($"{BasePath}/{{scheduleId}}/retry-revocation", HandleRetryRevocationAsync)
            .WithTags("StudioTeamAutomations");
        app.MapDelete($"{BasePath}/{{scheduleId}}", HandleDeleteAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/pause", HandlePauseAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/resume", HandleResumeAsync).WithTags("StudioTeamAutomations");
        app.MapPost($"{BasePath}/{{scheduleId}}/run-now", HandleRunNowAsync).WithTags("StudioTeamAutomations");
    }

    internal static async Task<IResult> HandlePreflightAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string memberId,
        StudioMemberAutomationPreflightRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
            return Results.Ok(await schedules.PreflightAsync(
                BuildScheduleRequest(scopeId, teamId, memberId, body, owner.Context, ResolveBearerToken(http)),
                ct));
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
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
            var bearer = ResolveBearerToken(http);
            var request = BuildScheduleRequest(scopeId, teamId, memberId, body, owner.Context, bearer) with
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
                    owner.Context.VerifiedBindingId);
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
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
            var request = BuildScheduleRequest(
                scopeId,
                teamId,
                memberId,
                body,
                owner.Context,
                ResolveBearerToken(http)) with
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
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
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
                owner.Context)
            {
                DisplayName = body.DisplayName,
                Prompt = body.Prompt,
                ProvisioningBearerToken = ResolveBearerToken(http),
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
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
            var receipt = await schedules.DeleteAsync(
                new StudioMemberAutomationActionCommand(
                    scopeId,
                    teamId,
                    memberId,
                    scheduleId,
                    body.OperationId,
                    body.IdempotencyKey)
                {
                    AuthenticatedOwner = owner.Context,
                    ProvisioningBearerToken = ResolveBearerToken(http),
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
        StudioMemberAutomationActionRequest body,
        [FromServices] IStudioMemberWorkflowSchedulePort schedules,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        try
        {
            var owner = await ResolveOwnerAsync(http, bindingQuery, ct);
            var receipt = await schedules.RetryRevocationAsync(
                new StudioMemberAutomationActionCommand(
                    scopeId,
                    teamId,
                    memberId,
                    scheduleId,
                    body.OperationId,
                    body.IdempotencyKey)
                {
                    AuthenticatedOwner = owner.Context,
                    ProvisioningBearerToken = ResolveBearerToken(http),
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

    private static async Task<ResolvedOwner> ResolveOwnerAsync(
        HttpContext http,
        IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        var subject = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("nyxid_subject_missing");

        var normalizedSubject = subject.Trim();
        var externalSubject = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = normalizedSubject,
        };
        var binding = await bindingQuery.ResolveAsync(externalSubject, ct);
        if (binding == null || string.IsNullOrWhiteSpace(binding.Value))
            throw new InvalidOperationException("nyxid_binding_missing");
        return new ResolvedOwner(new AuthenticatedAuthorizationOwnerContext(
            new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = normalizedSubject,
            },
            OwnerScope.NyxIdPlatform,
            string.Empty,
            normalizedSubject,
            binding.Value));
    }

    private static string ResolveBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (header == null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("provisioning_bearer_missing");
        var token = header[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(','))
            throw new UnauthorizedAccessException("provisioning_bearer_invalid");
        return token;
    }

    private static bool TryMapError(
        Exception exception,
        string scopeId,
        string teamId,
        string memberId,
        out IResult result)
    {
        result = exception switch
        {
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
                new
                {
                    code = ToPlanConflictCode(conflict.Code),
                    message = ToPlanConflictMessage(conflict.Code),
                    preflightLocator = BuildPreflightLocator(scopeId, teamId, memberId),
                },
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

    private sealed record ResolvedOwner(AuthenticatedAuthorizationOwnerContext Context);
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
