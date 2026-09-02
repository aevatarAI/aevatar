using Aevatar.Capabilities;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

/// <summary>
/// One-call workflow provisioning HTTP surface (C1) mounted at
/// <c>POST /api/scopes/{scopeId}/provision-workflow</c>. Composes member
/// create + bind + scheduled-dispatch behind a single proxied call so a Claude
/// Code session reaching aevatar through the nyxid downstream provisions a
/// runnable, scope-owned workflow without orchestrating the multi-step member
/// flow itself.
///
/// The flow is NON-BLOCKING: binding a workflow member is a multi-minute async
/// pipeline, so the handler never polls the bind to completion (that would
/// exhaust the gateway timeout). It creates the member, accepts the bind, and
/// creates a Workflow-kind scheduled-dispatch that produces the run — the
/// scheduled path is also the only one that projects the caller's re-minted NyxID
/// token onto the run so its LLM calls authenticate. The endpoint therefore
/// always returns 202 Accepted; runs appear in the Observatory as the schedule
/// fires.
///
/// The endpoint depends only on <see cref="IStudioWorkflowProvisioningService"/>;
/// it never reaches for the platform invocation or schedule ports directly. It
/// mirrors <see cref="StudioMemberEndpoints"/>: the same scope-access guard
/// short-circuits before the service is touched, and domain validation failures
/// map to a stable 400 code.
///
/// The caller's NyxID subject reference (<see cref="ProvisionWorkflowRequest.Caller"/>)
/// is supplied in the request body, mirroring the workflow-schedule path. A
/// forwarded bearer token cannot be converted into the subject reference the
/// scheduled dispatch needs to re-mint a token on every fire, so the subject ref
/// is an explicit body field rather than derived from an ambient claim.
///
/// Response status:
///   - accepted (member created, bind accepted, schedule created) → 202 + links
///   - validation / missing caller subject ref → 400
///   - cross-scope / unauthenticated → 403 / 401 (via the guard)
///
/// IMPORTANT: the <see cref="IStudioWorkflowProvisioningService"/> parameter must
/// carry <see cref="FromServicesAttribute"/> for the same reason documented on
/// <see cref="StudioMemberEndpoints"/> — Minimal API's RequestDelegateFactory
/// probes parameter types for a custom binder, and the attribute resolves the
/// dependency from DI instead.
/// </summary>
internal static class StudioProvisioningEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/scopes/{scopeId}/provision-workflow", HandleProvisionWorkflowAsync)
            .WithTags("StudioProvisioning");
    }

    internal static async Task<IResult> HandleProvisionWorkflowAsync(
        HttpContext http,
        string scopeId,
        ProvisionWorkflowRequest request,
        [FromServices] IStudioWorkflowProvisioningService provisioningService,
        [FromServices] IExternalIdentityBindingQueryPort bindingQuery,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (request == null)
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", "request body is required.");

        if (string.IsNullOrWhiteSpace(request.TeamId))
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", "teamId is required.");

        if (!TryResolveCallerCredential(request, out var callerCredential, out var credentialError))
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", credentialError);

        try
        {
            var shouldSchedule = request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron);
            var executionMode = shouldSchedule
                ? ExternalCapabilityExecutionMode.Durable
                : ExternalCapabilityExecutionMode.Interactive;
            var scheduleAuthority = shouldSchedule
                ? await StudioMemberAutomationHttpAuthorityResolver.ResolveAsync(
                    http,
                    bindingQuery,
                    ResolveAuthDisabledScheduleOwnerFallback(http, callerCredential),
                    ct)
                : null;
            var admittedRequest = request with
            {
                ExplicitRequestConfirmations = null,
                CapabilityAdmission = StudioWorkflowCapabilityAdmissionHttpContext.Create(
                    http,
                    executionMode,
                    request.ExplicitRequestConfirmations),
                AuthenticatedOwner = scheduleAuthority?.AuthenticatedOwner,
                ProvisioningBearerToken = scheduleAuthority?.ProvisioningBearerToken,
            };
            var response = await provisioningService.ProvisionAsync(
                scopeId, callerCredential, admittedRequest, ct);

            // The bind and the run are both asynchronous, so provisioning always
            // ACKs with 202 Accepted: the member + bind + schedule were accepted
            // and the run is produced by the schedule. The Location points at the
            // schedule so the caller can poll/manage it.
            return Results.Accepted(
                BuildScheduleLocation(response.ScheduleId),
                response);
        }
        catch (NyxIdExplicitRequestConfirmationInputException ex)
        {
            return BadRequest(NyxIdExplicitRequestConfirmationInputException.ErrorCode, ex.Message);
        }
        catch (WorkflowCallerCredentialSelectionException)
        {
            return BadRequest(
                WorkflowCallerCredentialSelectionException.ErrorCode,
                WorkflowCallerCredentialSelectionException.SafeMessage);
        }
        catch (StudioMemberAutomationAuthorizationBindingRequiredException)
        {
            return Results.Json(
                new
                {
                    code = "PROVISION_WORKFLOW_AUTHORIZATION_BINDING_REQUIRED",
                    message = "Reconnect NyxID to authorize this workflow schedule.",
                },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(
                new { code = "PROVISION_WORKFLOW_UNAUTHORIZED", message = ex.Message },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (TryMapProvisioningError(ex, out var result))
        {
            return result;
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", ex.Message);
        }
    }

    private static bool TryMapProvisioningError(Exception exception, out IResult result)
    {
        result = exception switch
        {
            StudioMemberAutomationProjectionPendingException pending => Results.Json(
                new
                {
                    code = "PROVISION_WORKFLOW_AUTHORIZATION_PROJECTION_PENDING",
                    message = "The refreshed authorization catalog is still being projected. Retry this request.",
                    retryable = true,
                    requiredStateVersion = pending.RequiredStateVersion,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationCatalogRefreshSupersededException => Results.Json(
                new
                {
                    code = "PROVISION_WORKFLOW_AUTHORIZATION_REFRESH_SUPERSEDED",
                    message = "A newer authorization catalog refresh superseded this request. Retry this request.",
                    retryable = true,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationCatalogRefreshUnavailableException => Results.Json(
                new
                {
                    code = "PROVISION_WORKFLOW_AUTHORIZATION_REFRESH_UNAVAILABLE",
                    message = "The authorization catalog could not be refreshed. Retry this request.",
                    retryable = true,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            StudioMemberAutomationPlanConflictException conflict => Results.Json(
                new
                {
                    code = ToPlanConflictCode(conflict.Code),
                    message = ToPlanConflictMessage(conflict.Code),
                },
                statusCode: StatusCodes.Status409Conflict),
            _ => null!,
        };
        return result != null;
    }

    private static string ToPlanConflictCode(string code) => code switch
    {
        "authorization_plan_changed" => "PROVISION_WORKFLOW_AUTHORIZATION_PLAN_CHANGED",
        "reauthorization_required" => "PROVISION_WORKFLOW_REAUTHORIZATION_REQUIRED",
        _ => "PROVISION_WORKFLOW_AUTHORIZATION_CONFLICT",
    };

    private static string ToPlanConflictMessage(string code) => code switch
    {
        "authorization_plan_changed" => "The authorization plan changed before the schedule write. Retry this request.",
        "reauthorization_required" => "Reconnect NyxID to authorize this workflow schedule.",
        _ => "The workflow schedule authorization plan conflicted with the current state.",
    };

    private static string? ResolveAuthDisabledScheduleOwnerFallback(
        HttpContext http,
        ProvisionWorkflowCallerCredential callerCredential) =>
        AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices)
            ? null
            : callerCredential.ExternalUserId;

    /// <summary>
    /// Resolves the caller NyxID subject reference from the request body. The
    /// scope (capability) defaults to the nyxid proxy scope when omitted so the
    /// common CC-via-proxy case needs only the subject identity.
    /// </summary>
    private static bool TryResolveCallerCredential(
        ProvisionWorkflowRequest request,
        out ProvisionWorkflowCallerCredential callerCredential,
        out string error)
    {
        callerCredential = null!;
        error = string.Empty;

        var caller = request.Caller;
        if (caller == null
            || string.IsNullOrWhiteSpace(caller.Platform)
            || string.IsNullOrWhiteSpace(caller.ExternalUserId))
        {
            error = "caller.platform and caller.externalUserId are required (the NyxID subject the scheduled run re-mints a token for).";
            return false;
        }

        var scope = string.IsNullOrWhiteSpace(caller.Scope)
            ? ProvisionWorkflowCallerCredential.DefaultScope
            : caller.Scope.Trim();

        callerCredential = new ProvisionWorkflowCallerCredential(
            Platform: caller.Platform.Trim(),
            ExternalUserId: caller.ExternalUserId.Trim(),
            Scope: scope,
            Tenant: string.IsNullOrWhiteSpace(caller.Tenant) ? null : caller.Tenant.Trim());
        return true;
    }

    private static string BuildScheduleLocation(string? scheduleId) =>
        string.IsNullOrWhiteSpace(scheduleId)
            ? "/api/schedules"
            : $"/api/schedules/{Uri.EscapeDataString(scheduleId)}";

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });
}
