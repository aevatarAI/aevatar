using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
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
/// attempts to create a Workflow-kind scheduled-dispatch that produces the run —
/// the scheduled path is also the only one that projects the caller's re-minted
/// NyxID token onto the run so its LLM calls authenticate. The endpoint returns
/// 202 Accepted after the member/bind hand-off; runs appear in the Observatory
/// when the response schedule stage is <c>schedule_accepted</c>.
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
///   - accepted (member created, bind accepted, schedule accepted/not requested/blocked) → 202 + links
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
            var executionMode = request.RunImmediately || !string.IsNullOrWhiteSpace(request.Cron)
                ? ExternalCapabilityExecutionMode.Durable
                : ExternalCapabilityExecutionMode.Interactive;
            var admittedRequest = request with
            {
                CapabilityAdmission = StudioWorkflowCapabilityAdmissionHttpContext.Create(
                    http,
                    executionMode),
            };
            var response = await provisioningService.ProvisionAsync(
                scopeId, callerCredential, admittedRequest, ct);

            // The bind and any run are asynchronous, so provisioning ACKs with
            // 202 Accepted once the member + bind hand-off is accepted. The
            // response schedule stage tells the caller whether a schedule was
            // accepted, not requested, or blocked after bind.
            return Results.Accepted(
                BuildScheduleLocation(response.ScheduleId),
                response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", ex.Message);
        }
    }

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
