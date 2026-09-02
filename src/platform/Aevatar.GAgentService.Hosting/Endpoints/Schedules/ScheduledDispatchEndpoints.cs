using System.Security.Claims;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.Capabilities;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints.Schedules;

public static class ScheduledDispatchEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/schedules", Create)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPut("/schedules/{scheduleId}", Update)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/schedules/{scheduleId}:enable", Enable)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/schedules/{scheduleId}:disable", Disable)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapDelete("/schedules/{scheduleId}", Delete)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces<StudioMemberAutomationMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status500InternalServerError);
        group.MapGet("/schedules", List)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK);
        group.MapGet("/schedules/{scheduleId}", Get)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/schedules/preview", Preview)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/schedules/{scheduleId}:run-now", RunNow)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> Create(
        HttpContext http,
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        TeamMemberAutomationOwner? owner;
        try
        {
            context = ResolveMutationContext(http);
            owner = input.Owner?.ToTeamMemberAutomationOwner();
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            if (owner != null)
                context = context with { TeamAutomationOwner = owner };
            configuration = (await input.ToConfigurationAsync(
                input.ScheduleId,
                catalogReader,
                revisionCatalogReader,
                context.AuthenticatedNyxIdOwnerSubject,
                defaultMissingWorkflowScheduleAuth: true,
                ct)) with
            {
                TeamAutomationOwner = owner,
            };
            var targetScopeId = configuration.Target.ServiceInvocation?.Identity.TenantId;
            if (TryCreateOwnerScopeAccessDeniedResult(http, targetScopeId, out denied))
                return denied;
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.CreateAsync(configuration, context, ct);
            return Results.Accepted(BuildScheduleLocation(receipt.ScheduleId, owner), receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        HttpContext http,
        string scheduleId,
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        TeamMemberAutomationOwner? owner;
        try
        {
            context = ResolveMutationContext(http);
            owner = input.Owner?.ToTeamMemberAutomationOwner();
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            if (owner != null)
                context = context with { TeamAutomationOwner = owner };
            configuration = (await input.ToConfigurationAsync(
                scheduleId,
                catalogReader,
                revisionCatalogReader,
                context.AuthenticatedNyxIdOwnerSubject,
                defaultMissingWorkflowScheduleAuth: true,
                ct)) with
            {
                TeamAutomationOwner = owner,
            };
            var targetScopeId = configuration.Target.ServiceInvocation?.Identity.TenantId;
            if (TryCreateOwnerScopeAccessDeniedResult(http, targetScopeId, out denied))
                return denied;
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, configuration, context, ct);
            return Results.Accepted(BuildScheduleLocation(receipt.ScheduleId, owner), receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Enable(
        HttpContext http,
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var owner = input?.Owner?.ToTeamMemberAutomationOwner();
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            var receipt = owner == null
                ? await schedules.EnableAsync(scheduleId, input?.Reason ?? string.Empty, ct)
                : await schedules.EnableTeamAutomationAsync(scheduleId, owner, input?.Reason ?? string.Empty, ct);
            return Results.Accepted(BuildScheduleLocation(receipt.ScheduleId, owner), receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Disable(
        HttpContext http,
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var owner = input?.Owner?.ToTeamMemberAutomationOwner();
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            var receipt = owner == null
                ? await schedules.DisableAsync(scheduleId, input?.Reason ?? string.Empty, ct)
                : await schedules.DisableTeamAutomationAsync(scheduleId, owner, input?.Reason ?? string.Empty, ct);
            return Results.Accepted(BuildScheduleLocation(receipt.ScheduleId, owner), receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Delete(
        HttpContext http,
        string scheduleId,
        [FromQuery] string? reason,
        [FromBody] ScheduledDispatchDeleteHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        TeamMemberAutomationOwner? owner;
        try
        {
            owner = input?.Owner?.ToTeamMemberAutomationOwner();
        }
        catch (ArgumentException)
        {
            return InvalidTeamAutomationRequest(
                "Team automation owner is invalid.");
        }

        if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
            return denied;

        var operationId = NormalizeOptional(input?.OperationId);
        var idempotencyKey = NormalizeOptional(input?.IdempotencyKey);
        if ((operationId == null) != (idempotencyKey == null))
        {
            return InvalidTeamAutomationRequest(
                "operationId and idempotencyKey must be supplied together.");
        }

        var deleteReason = reason ?? input?.Reason ?? string.Empty;
        if (operationId == null)
        {
            try
            {
                var receipt = owner == null
                    ? await schedules.DeleteAsync(
                        scheduleId,
                        deleteReason,
                        ct)
                    : await schedules.DeleteTeamAutomationAsync(
                        scheduleId,
                        owner,
                        deleteReason,
                        ct);
                return Results.Accepted(
                    BuildScheduleLocation(receipt.ScheduleId, owner),
                    receipt);
            }
            catch (Exception ex) when (
                owner != null &&
                TryMapTeamAutomationDeleteError(ex, out var ownerError))
            {
                return ownerError;
            }
            catch (Exception ex) when (
                owner == null &&
                TryMapScheduleMutationError(ex, out var genericError))
            {
                return genericError;
            }
        }

        if (owner == null)
        {
            return InvalidTeamAutomationRequest(
                "owner is required when operationId and idempotencyKey are supplied.");
        }

        var lifecycleSchedules =
            http.RequestServices.GetService<IStudioMemberWorkflowSchedulePort>();
        var bindingQuery =
            http.RequestServices.GetService<IExternalIdentityBindingQueryPort>();
        if (lifecycleSchedules == null || bindingQuery == null)
            return TeamAutomationLifecycleUnavailable();

        try
        {
            var authority =
                await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ct);
            var receipt = await lifecycleSchedules.DeleteAsync(
                new StudioMemberAutomationActionCommand(
                    owner.ScopeId,
                    owner.TeamId,
                    owner.MemberId,
                    scheduleId,
                    operationId,
                    idempotencyKey!)
                {
                    Reason = deleteReason,
                    AuthenticatedOwner = authority.AuthenticatedOwner,
                    ProvisioningBearerToken =
                        authority.ProvisioningBearerToken,
                },
                ct);
            return Results.Accepted(
                BuildScheduleLocation(receipt.ScheduleId, owner),
                receipt);
        }
        catch (Exception ex) when (
            TryMapTeamAutomationDeleteError(ex, out var lifecycleError))
        {
            return lifecycleError;
        }
    }

    internal static async Task<IResult> List(
        HttpContext http,
        [FromServices] IScheduledDispatchApplicationService schedules,
        string? ownerKind = null,
        string? ownerScopeId = null,
        string? ownerTeamId = null,
        string? ownerMemberId = null,
        string? scopeId = null,
        string? teamId = null,
        string? memberId = null,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        if (HasLegacyOwnerQuery(scopeId, teamId, memberId))
            return Results.BadRequest(new { error = "Use ownerKind, ownerScopeId, ownerTeamId, and ownerMemberId for schedule owner queries." });

        ScheduledDispatchListQuery query;
        try
        {
            query = ResolveListQueryFromOwnerQuery(
                ownerKind,
                ownerScopeId,
                ownerTeamId,
                ownerMemberId,
                take,
                cursor,
                includeTotalCount);
            var queryScopeId = query.TeamAutomationOwner?.ScopeId ?? query.TeamAutomationScopeId;
            if (TryCreateOwnerScopeAccessDeniedResult(http, queryScopeId, out var denied))
                return denied;
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Ok(await schedules.ListAsync(query, ct));
    }

    internal static async Task<IResult> Get(
        HttpContext http,
        string scheduleId,
        [FromServices] IScheduledDispatchApplicationService schedules,
        string? ownerKind = null,
        string? ownerScopeId = null,
        string? ownerTeamId = null,
        string? ownerMemberId = null,
        string? scopeId = null,
        string? teamId = null,
        string? memberId = null,
        CancellationToken ct = default)
    {
        if (HasLegacyOwnerQuery(scopeId, teamId, memberId))
            return Results.BadRequest(new { error = "Use ownerKind, ownerScopeId, ownerTeamId, and ownerMemberId for schedule owner queries." });

        try
        {
            var owner = ResolveOwnerFromQuery(ownerKind, ownerScopeId, ownerTeamId, ownerMemberId);
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            var schedule = owner == null
                ? await schedules.GetAsync(scheduleId, ct)
                : await schedules.GetTeamAutomationAsync(scheduleId, owner, ct);
            return schedule == null ? Results.NotFound() : Results.Ok(schedule);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> Preview(
        ScheduledDispatchPreviewHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            return Results.Ok(await schedules.PreviewAsync(
                input.CronExpression,
                input.Timezone,
                input.Count <= 0 ? 5 : input.Count,
                input.FromUtc,
                ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> RunNow(
        HttpContext http,
        string scheduleId,
        ScheduledDispatchRunNowHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var owner = input?.Owner?.ToTeamMemberAutomationOwner();
            if (TryCreateOwnerScopeAccessDeniedResult(http, owner, out var denied))
                return denied;
            var receipt = owner == null
                ? await schedules.RunNowAsync(scheduleId, ct)
                : await schedules.RunTeamAutomationNowAsync(scheduleId, owner, ct);
            return Results.Accepted(BuildScheduleLocation(receipt.ScheduleId, owner), receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    private static bool TryCreateOwnerScopeAccessDeniedResult(
        HttpContext http,
        TeamMemberAutomationOwner? owner,
        out IResult denied)
    {
        if (owner == null)
        {
            denied = Results.Empty;
            return false;
        }

        return AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, owner.ScopeId, out denied);
    }

    private static bool TryCreateOwnerScopeAccessDeniedResult(
        HttpContext http,
        string? scopeId,
        out IResult denied)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            denied = Results.Empty;
            return false;
        }

        return AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId.Trim(), out denied);
    }

    private static string BuildScheduleLocation(string scheduleId, TeamMemberAutomationOwner? owner)
    {
        var encodedScheduleId = Uri.EscapeDataString(scheduleId);
        if (owner == null)
            return $"/api/schedules/{encodedScheduleId}";

        return $"/api/schedules/{encodedScheduleId}" +
               $"?ownerKind={Uri.EscapeDataString(ScheduledDispatchOwnerKinds.StudioMemberAutomation)}" +
               $"&ownerScopeId={Uri.EscapeDataString(owner.ScopeId)}" +
               $"&ownerTeamId={Uri.EscapeDataString(owner.TeamId)}" +
               $"&ownerMemberId={Uri.EscapeDataString(owner.MemberId)}";
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ScheduledDispatchMutationContext ResolveMutationContext(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new ScheduledDispatchMutationContext(
            ReadFirstClaim(http.User, "scope_id", "workflow.scope_id"),
            ResolveAuthenticatedNyxIdOwnerSubject(http));
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef? ResolveAuthenticatedNyxIdOwnerSubject(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var ownerUserId = ReadFirstClaim(
            http.User,
            "uid",
            "sub",
            ClaimTypes.NameIdentifier,
            "user_id");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            return null;

        return new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            ownerUserId.Trim());
    }

    private static string? ReadFirstClaim(ClaimsPrincipal? user, params string[] claimTypes)
    {
        if (user == null)
            return null;

        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool HasLegacyOwnerQuery(string? scopeId, string? teamId, string? memberId) =>
        !string.IsNullOrWhiteSpace(scopeId) ||
        !string.IsNullOrWhiteSpace(teamId) ||
        !string.IsNullOrWhiteSpace(memberId);

    private static ScheduledDispatchListQuery ResolveListQueryFromOwnerQuery(
        string? ownerKind,
        string? ownerScopeId,
        string? ownerTeamId,
        string? ownerMemberId,
        int take,
        string? cursor,
        bool includeTotalCount)
    {
        if (string.IsNullOrWhiteSpace(ownerKind) &&
            string.IsNullOrWhiteSpace(ownerScopeId) &&
            string.IsNullOrWhiteSpace(ownerTeamId) &&
            string.IsNullOrWhiteSpace(ownerMemberId))
        {
            return new ScheduledDispatchListQuery(
                Take: take,
                Cursor: cursor,
                IncludeTotalCount: includeTotalCount);
        }

        if (!string.Equals(ownerKind, ScheduledDispatchOwnerKinds.StudioMemberAutomation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported scheduled dispatch owner kind '{ownerKind ?? string.Empty}'.",
                nameof(ownerKind));
        }

        var normalizedScopeId = NormalizeOptional(ownerScopeId)
            ?? throw new ArgumentException("Owner scopeId is required.", nameof(ownerScopeId));
        var normalizedTeamId = NormalizeOptional(ownerTeamId)
            ?? throw new ArgumentException("Owner teamId is required.", nameof(ownerTeamId));
        var normalizedMemberId = NormalizeOptional(ownerMemberId);
        if (normalizedMemberId is not null)
        {
            return new ScheduledDispatchListQuery(
                Take: take,
                Cursor: cursor,
                IncludeTotalCount: includeTotalCount,
                TeamAutomationOwner: new TeamMemberAutomationOwner(
                    normalizedScopeId,
                    normalizedMemberId,
                    normalizedTeamId));
        }

        return new ScheduledDispatchListQuery(
            Take: take,
            Cursor: cursor,
            IncludeTotalCount: includeTotalCount,
            TeamAutomationScopeId: normalizedScopeId,
            TeamAutomationTeamId: normalizedTeamId,
            TeamAutomationMemberId: null,
            ExcludeCompletedTeamAutomationDeletions: true);
    }

    private static TeamMemberAutomationOwner? ResolveOwnerFromQuery(
        string? ownerKind,
        string? ownerScopeId,
        string? ownerTeamId,
        string? ownerMemberId)
    {
        if (string.IsNullOrWhiteSpace(ownerKind) &&
            string.IsNullOrWhiteSpace(ownerScopeId) &&
            string.IsNullOrWhiteSpace(ownerTeamId) &&
            string.IsNullOrWhiteSpace(ownerMemberId))
        {
            return null;
        }

        return new ScheduledDispatchOwner(
                ownerKind ?? string.Empty,
                ownerScopeId ?? string.Empty,
                ownerTeamId ?? string.Empty,
                ownerMemberId ?? string.Empty)
            .ToTeamMemberAutomationOwner();
    }

    internal static bool TryMapScheduleConfigurationError(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case FormatException:
                result = Results.BadRequest(new
                {
                    code = "INVALID_SCHEDULED_DISPATCH_REQUEST",
                    message = "payloadBase64 must be valid base64.",
                    validation = new
                    {
                        field = "serviceInvocation.payloadBase64",
                        error = "INVALID_BASE64",
                    },
                });
                return true;
            case InvalidOperationException invalid:
                result = Results.BadRequest(new
                {
                    code = "INVALID_SCHEDULED_DISPATCH_REQUEST",
                    message = invalid.Message,
                });
                return true;
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }

    internal static bool TryMapScheduleMutationError(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            case ScheduledDispatchNotFoundException notFound:
                result = Results.NotFound(new { error = notFound.Message });
                return true;
            case ScheduledDispatchConflictException conflict:
                result = Results.Conflict(new { error = conflict.Message });
                return true;
            case InvalidOperationException invalidOperation when IsExpectedScheduleLifecycleError(invalidOperation.Message):
                result = Results.BadRequest(new { error = invalidOperation.Message });
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }

    private static IResult InvalidTeamAutomationRequest(string message) =>
        Results.BadRequest(new
        {
            code = "INVALID_TEAM_AUTOMATION_REQUEST",
            message,
        });

    private static IResult TeamAutomationLifecycleUnavailable() =>
        Results.Json(
            new
            {
                code = "TEAM_AUTOMATION_LIFECYCLE_UNAVAILABLE",
                message =
                    "Team automation lifecycle capability is unavailable.",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult TeamAutomationNotFound() =>
        Results.Json(
            new
            {
                code = "TEAM_AUTOMATION_NOT_FOUND",
                message = "Team automation resource was not found.",
            },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult TeamAutomationDeleteFailed() =>
        Results.Json(
            new
            {
                code = "TEAM_AUTOMATION_DELETE_FAILED",
                message =
                    "Team automation delete could not be completed.",
            },
            statusCode: StatusCodes.Status500InternalServerError);

    private static bool TryMapTeamAutomationDeleteError(
        Exception exception,
        out IResult result)
    {
        result = exception switch
        {
            UnauthorizedAccessException => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_UNAUTHORIZED",
                    message =
                        "Authenticated Team automation authority is required.",
                },
                statusCode: StatusCodes.Status401Unauthorized),
            StudioMemberAutomationNotFoundException =>
                TeamAutomationNotFound(),
            StudioMemberNotFoundException => TeamAutomationNotFound(),
            ScheduledDispatchNotFoundException => TeamAutomationNotFound(),
            ScheduledDispatchConflictException => Results.Json(
                new
                {
                    code = "TEAM_AUTOMATION_CONFLICT",
                    message =
                        "The Team automation delete conflicts with its active operation.",
                },
                statusCode: StatusCodes.Status409Conflict),
            InvalidOperationException invalidOperation =>
                MapTeamAutomationDeleteInvalidOperation(
                    invalidOperation.Message),
            ArgumentException => InvalidTeamAutomationRequest(
                "Team automation delete request is invalid."),
            _ => null!,
        };
        return result != null;
    }

    private static IResult MapTeamAutomationDeleteInvalidOperation(
        string? stableCode) =>
        stableCode switch
        {
            "team_member_is_not_workflow" or
            "team_automation_delete_requires_revocation_context" or
            "team_automation_owner_required" =>
                InvalidTeamAutomationRequest(
                    "Team automation delete request is invalid."),
            "team_automation_commit_observation_unavailable" or
            "team_automation_dispatch_rejected" or
            "team_automation_commit_observation_ended" =>
                TeamAutomationLifecycleUnavailable(),
            "team_automation_observation_status_invalid" or
            "team_automation_revocation_completion_not_committed" =>
                TeamAutomationDeleteFailed(),
            _ => TeamAutomationDeleteFailed(),
        };

    private static bool IsExpectedScheduleLifecycleError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.StartsWith("team_automation_", StringComparison.Ordinal) ||
         message.StartsWith("schedule_", StringComparison.Ordinal));

    internal static void RejectExternalCallerDurableCredential(Any? payload)
    {
        if (payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true ||
            chatRequest.CallerDurableCredential == null)
        {
            return;
        }

        throw new ArgumentException(
            "caller_durable_credential is trusted-only and cannot be supplied by schedule API payloads.",
            "caller_durable_credential");
    }

}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchConfigurationHttpRequest
{
    private const string DefaultWorkflowScheduleNyxIdScope = "proxy";

    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchScheduleKind ScheduleKind { get; init; } = ScheduledDispatchScheduleKind.Generic;
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ScheduledDispatchOwnerHttpRequest? Owner { get; init; }
    public ScheduledDispatchServiceInvocationTargetHttpRequest? ServiceInvocation { get; init; }

    public async Task<ScheduledDispatchConfiguration> ToConfigurationAsync(
        string? fallbackScheduleId,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        bool defaultMissingWorkflowScheduleAuth = false,
        CancellationToken ct = default)
    {
        var resolvedTarget = await ResolveTargetAsync(catalogReader, revisionCatalogReader, authenticatedOwnerSubject, ct);
        var scheduleKind = ResolveScheduleKind(resolvedTarget);
        var target = defaultMissingWorkflowScheduleAuth
            ? ApplyDefaultWorkflowScheduleAuth(resolvedTarget.Target, scheduleKind, authenticatedOwnerSubject)
            : resolvedTarget.Target;
        return new ScheduledDispatchConfiguration(
            ScheduleId: string.IsNullOrWhiteSpace(ScheduleId) ? fallbackScheduleId ?? string.Empty : ScheduleId,
            DisplayName: DisplayName ?? string.Empty,
            Target: target,
            CronExpression: CronExpression,
            Timezone: Timezone ?? string.Empty,
            Enabled: Enabled,
            Headers: Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: scheduleKind)
        {
            CredentialRequirementTargetKind = resolvedTarget.CredentialRequirementTargetKind,
        };
    }

    private async Task<ResolvedScheduledDispatchTarget> ResolveTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject,
        CancellationToken ct)
    {
        if (ServiceInvocation == null)
            throw new ArgumentException("A service invocation scheduled dispatch target is required.");

        return await ServiceInvocation.ToResolvedTargetAsync(
            catalogReader,
            revisionCatalogReader,
            authenticatedOwnerSubject,
            ct);
    }

    private ScheduledDispatchScheduleKind ResolveScheduleKind(ResolvedScheduledDispatchTarget resolvedTarget)
    {
        if (ScheduleKind == ScheduledDispatchScheduleKind.Workflow)
        {
            if (!resolvedTarget.IsWorkflowServiceTarget)
            {
                throw new ArgumentException(
                    "scheduleKind Workflow requires a workflow service invocation target.",
                    nameof(ScheduleKind));
            }

            return ScheduledDispatchScheduleKind.Workflow;
        }

        if (resolvedTarget.IsWorkflowServiceTarget && ScheduleKind == ScheduledDispatchScheduleKind.Generic)
        {
            return ScheduledDispatchScheduleKind.Workflow;
        }

        return ScheduleKind;
    }

    private static ScheduledDispatchTargetDescriptor ApplyDefaultWorkflowScheduleAuth(
        ScheduledDispatchTargetDescriptor target,
        ScheduledDispatchScheduleKind scheduleKind,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject)
    {
        var serviceInvocation = target.ServiceInvocation;
        if (scheduleKind != ScheduledDispatchScheduleKind.Workflow ||
            target.Kind != ScheduledDispatchTargetKind.ServiceInvocation ||
            serviceInvocation == null ||
            serviceInvocation.Auth != null)
        {
            return target;
        }

        if (authenticatedOwnerSubject == null)
        {
            throw new ArgumentException(
                "Authenticated NyxID owner subject is required for workflow schedule auth.",
                nameof(authenticatedOwnerSubject));
        }

        return target with
        {
            ServiceInvocation = serviceInvocation with
            {
                Auth = new ScheduledServiceInvocationAuth(
                    new ScheduledServiceInvocationNyxIdCredentialSource(
                        authenticatedOwnerSubject,
                        DefaultWorkflowScheduleNyxIdScope,
                        ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner)),
            },
        };
    }

    internal sealed record ResolvedScheduledDispatchTarget(
        ScheduledDispatchTargetDescriptor Target,
        bool IsWorkflowServiceTarget,
        ScheduledDispatchCredentialRequirementTargetKind CredentialRequirementTargetKind);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchServiceInvocationTargetHttpRequest
{
    public required ServiceIdentity Identity { get; init; }
    public required string EndpointId { get; init; }
    public required string PayloadTypeUrl { get; init; }
    public string? PayloadBase64 { get; init; }
    public string? PayloadJson { get; init; }
    public string? RevisionId { get; init; }
    public ServiceInvocationCaller? Caller { get; init; }
    public ScheduledServiceInvocationAuthHttpRequest? Auth { get; init; }

    public async Task<ScheduledDispatchTargetDescriptor> ToTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        CancellationToken ct = default) =>
        (await ToResolvedTargetAsync(catalogReader, revisionCatalogReader, authenticatedOwnerSubject, ct))
        .Target;

    internal async Task<ScheduledDispatchConfigurationHttpRequest.ResolvedScheduledDispatchTarget> ToResolvedTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        CancellationToken ct = default)
    {
        var (payload, revisionId) = await ResolvePayloadAsync(catalogReader, revisionCatalogReader, ct);
        var target = ToTarget(payload, revisionId, authenticatedOwnerSubject);
        var implementationRevision = await ResolveImplementationRevisionAsync(catalogReader, revisionCatalogReader, revisionId, ct);
        return new ScheduledDispatchConfigurationHttpRequest.ResolvedScheduledDispatchTarget(
            target,
            IsWorkflowRevision(implementationRevision),
            ResolveCredentialRequirementTargetKind(implementationRevision));
    }

    public ScheduledDispatchTargetDescriptor ToTarget(
        Any payload,
        string revisionId,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null)
    {
        ScheduledDispatchEndpoints.RejectExternalCallerDurableCredential(payload);
        return new(
            ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                Identity,
                EndpointId,
                payload,
                revisionId,
                Caller,
                Auth?.ToAuth(authenticatedOwnerSubject)));
    }

    private async Task<(Any Payload, string RevisionId)> ResolvePayloadAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        var typeUrl = PayloadTypeUrl ?? string.Empty;
        var requestedRevisionId = RevisionId?.Trim() ?? string.Empty;
        var hasJson = !string.IsNullOrWhiteSpace(PayloadJson);
        var hasBase64 = !string.IsNullOrWhiteSpace(PayloadBase64);
        if (hasJson && hasBase64)
            throw new InvalidOperationException(
                "payloadJson and payloadBase64 are mutually exclusive; specify only one.");

        if (hasJson)
        {
            if (string.IsNullOrWhiteSpace(typeUrl))
                throw new InvalidOperationException("payloadTypeUrl is required when payloadJson is provided.");

            var revisionId = requestedRevisionId;
            if (string.IsNullOrWhiteSpace(revisionId))
            {
                var catalog = await catalogReader.GetAsync(Identity, ct);
                revisionId = catalog?.ActiveServingRevisionId ?? string.Empty;
            }

            var packed = await ServiceJsonPayloads.PackJsonAsync(
                revisionCatalogReader,
                Identity,
                revisionId,
                typeUrl,
                PayloadJson!,
                ct);
            return (packed, revisionId);
        }

        return (ServiceJsonPayloads.PackBase64(typeUrl, PayloadBase64), requestedRevisionId);
    }

    private async Task<ServiceRevisionSnapshot?> ResolveImplementationRevisionAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        string resolvedRevisionId,
        CancellationToken ct)
    {
        var revisions = await revisionCatalogReader.GetAsync(Identity, ct).ConfigureAwait(false);
        if (revisions == null || revisions.Revisions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(resolvedRevisionId))
        {
            return revisions.Revisions.FirstOrDefault(revision =>
                string.Equals(revision.RevisionId, resolvedRevisionId, StringComparison.Ordinal) &&
                ServiceEndpointContractMath.RevisionContainsEndpoint(revision, EndpointId));
        }

        var service = await catalogReader.GetAsync(Identity, ct).ConfigureAwait(false);
        return service == null
            ? null
            : ServiceEndpointContractMath.ResolveCurrentContractRevision(service, revisions, EndpointId);
    }

    private static bool IsWorkflowRevision(ServiceRevisionSnapshot? revision) =>
        revision != null &&
        (string.Equals(
             revision.ImplementationKind,
             ServiceEndpointContractMath.ImplementationKindWorkflow,
             StringComparison.OrdinalIgnoreCase) ||
         revision.Implementation?.Workflow != null);

    private static ScheduledDispatchCredentialRequirementTargetKind ResolveCredentialRequirementTargetKind(
        ServiceRevisionSnapshot? revision)
    {
        if (revision == null)
            return ScheduledDispatchCredentialRequirementTargetKind.Unspecified;

        if (IsWorkflowRevision(revision))
            return ScheduledDispatchCredentialRequirementTargetKind.WorkflowService;

        if (string.Equals(
                revision.ImplementationKind,
                ServiceEndpointContractMath.ImplementationKindStatic,
                StringComparison.OrdinalIgnoreCase) ||
            revision.Implementation?.Static != null)
        {
            return ScheduledDispatchCredentialRequirementTargetKind.StaticService;
        }

        if (string.Equals(
                revision.ImplementationKind,
                ServiceEndpointContractMath.ImplementationKindScripting,
                StringComparison.OrdinalIgnoreCase) ||
            revision.Implementation?.Scripting != null)
        {
            return ScheduledDispatchCredentialRequirementTargetKind.ScriptingService;
        }

        return ScheduledDispatchCredentialRequirementTargetKind.Unspecified;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledServiceInvocationAuthHttpRequest
{
    public ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest? SenderNyxId { get; init; }
    public string? DurableSenderBearerToken { get; init; }
    public ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest? ScopeOwnerNyxId { get; init; }

    public ScheduledServiceInvocationAuth ToAuth(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null)
    {
        var hasSenderNyxId = SenderNyxId != null;
        var durableToken = DurableSenderBearerToken?.Trim() ?? string.Empty;
        var hasDurableSenderBearerToken = durableToken.Length > 0;
        var hasScopeOwnerNyxId = ScopeOwnerNyxId != null;
        if (hasDurableSenderBearerToken)
        {
            throw new ArgumentException(
                "durableSenderBearerToken is no longer accepted for schedule auth; use senderNyxId or scopeOwnerNyxId.",
                nameof(DurableSenderBearerToken));
        }

        if (Convert.ToInt32(hasSenderNyxId) +
            Convert.ToInt32(hasScopeOwnerNyxId) != 1)
        {
            throw new ArgumentException("Exactly one service invocation credential source is required.", nameof(SenderNyxId));
        }

        if (hasScopeOwnerNyxId)
            return new ScheduledServiceInvocationAuth(ScopeOwnerNyxId!.ToSource(authenticatedOwnerSubject));

        return new ScheduledServiceInvocationAuth(SenderNyxId!.ToSource());
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
{
    public required string Scope { get; init; }

    public ScheduledServiceInvocationNyxIdCredentialSource ToSource(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null) =>
        new(
            RequireAuthenticatedOwnerSubject(authenticatedOwnerSubject),
            NormalizeRequired(Scope, nameof(Scope)),
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);

    private static ScheduledServiceInvocationNyxIdSubjectRef RequireAuthenticatedOwnerSubject(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject) =>
        authenticatedOwnerSubject ?? throw new ArgumentException(
            "Authenticated NyxID owner subject is required for scope owner schedule auth.",
            nameof(authenticatedOwnerSubject));

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
{
    public required ScheduledServiceInvocationNyxIdSubjectRefHttpRequest Subject { get; init; }
    public required string Scope { get; init; }

    public ScheduledServiceInvocationNyxIdCredentialSource ToSource() =>
        new(NormalizeSubject(Subject), NormalizeRequired(Scope, nameof(Scope)));

    private static ScheduledServiceInvocationNyxIdSubjectRef NormalizeSubject(
        ScheduledServiceInvocationNyxIdSubjectRefHttpRequest? subject)
    {
        if (subject == null)
            throw new ArgumentException("Subject is required.", nameof(Subject));

        return subject.ToSubject();
    }

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
{
    public required string Platform { get; init; }
    public string? Tenant { get; init; }
    public required string ExternalUserId { get; init; }

    public ScheduledServiceInvocationNyxIdSubjectRef ToSubject() =>
        new(
            NormalizeRequired(Platform, nameof(Platform)).ToLowerInvariant(),
            NormalizeOptional(Tenant),
            NormalizeRequired(ExternalUserId, nameof(ExternalUserId)));

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed record ScheduledDispatchPreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchOwnerHttpRequest
{
    public required string Kind { get; init; }
    public required string ScopeId { get; init; }
    public required string TeamId { get; init; }
    public required string MemberId { get; init; }

    public TeamMemberAutomationOwner ToTeamMemberAutomationOwner() =>
        new ScheduledDispatchOwner(Kind, ScopeId, TeamId, MemberId)
            .ToTeamMemberAutomationOwner();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchDeleteHttpRequest
{
    public string? Reason { get; init; }
    public string? OperationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public ScheduledDispatchOwnerHttpRequest? Owner { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchStateChangeHttpRequest
{
    public string? Reason { get; init; }
    public ScheduledDispatchOwnerHttpRequest? Owner { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledDispatchRunNowHttpRequest
{
    public ScheduledDispatchOwnerHttpRequest? Owner { get; init; }
}
