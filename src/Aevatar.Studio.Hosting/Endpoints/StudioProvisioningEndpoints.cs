using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

/// <summary>
/// One-call workflow provisioning HTTP surface (C1) mounted at
/// <c>POST /api/scopes/{scopeId}/provision-workflow</c>. Composes member
/// create + bind + invoke behind a single proxied call so a Claude Code session
/// reaching aevatar through the nyxid downstream provisions a runnable,
/// scope-owned workflow without orchestrating the multi-step member flow itself.
///
/// The endpoint depends only on <see cref="IStudioWorkflowProvisioningService"/>;
/// it never reaches for the platform invocation or member ports directly. It
/// mirrors <see cref="StudioMemberEndpoints"/>: the same scope-access guard
/// short-circuits before the service is touched, and domain validation failures
/// map to a stable 400 code.
///
/// Response status:
///   - bound in time → 200 with the run id + links
///   - bind accepted but still pending at the timeout → 202 (no run id)
///   - validation / terminal bind failure → 400
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

        try
        {
            var response = await provisioningService.ProvisionAsync(scopeId, request, ct);

            // The bind is asynchronous: a member that did not bind within the
            // timeout is honestly reported as 202 Accepted (it exists and will
            // bind), distinct from the 200 that carries a started run.
            return string.Equals(
                    response.BindingStatus,
                    ProvisionWorkflowBindingStatusNames.Pending,
                    StringComparison.Ordinal)
                ? Results.Accepted(
                    $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(response.MemberId)}",
                    response)
                : Results.Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_PROVISION_WORKFLOW_REQUEST", ex.Message);
        }
    }

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });
}
