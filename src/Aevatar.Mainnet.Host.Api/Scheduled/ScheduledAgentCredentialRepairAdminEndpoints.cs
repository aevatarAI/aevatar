using Aevatar.Authentication.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Mainnet.Host.Api.Scheduled;

internal static class ScheduledAgentCredentialRepairAdminEndpoints
{
    public static IEndpointRouteBuilder MapScheduledAgentCredentialRepairAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/scheduled-agent-credentials/repair", HandleAsync)
            .WithTags("ScheduledAgentCredentialAdmin");
        return app;
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext http,
        [FromBody] RepairRequest? request,
        [FromServices] IPlatformAdminAuthorizer? authorizer,
        [FromServices] IUserAgentCatalogCredentialRepairPort repairPort,
        CancellationToken ct)
    {
        if (authorizer is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var authorization = http.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(bearer))
            return Results.Forbid();

        PlatformCaller caller;
        try
        {
            caller = await authorizer.ResolveCallerAsync(bearer, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Results.Forbid();
        }
        if (!caller.IsElevated || request is null || string.IsNullOrWhiteSpace(caller.UserId))
            return Results.Forbid();

        var reference = request.secret_reference;
        if (string.IsNullOrWhiteSpace(request.agent_id) ||
            string.IsNullOrWhiteSpace(request.api_key_id) ||
            reference is null ||
            string.IsNullOrWhiteSpace(request.repair_reason))
            return Results.BadRequest(new { error = "invalid_repair_request" });

        var result = await repairPort.RepairMissingSecretReferenceAsync(
            request.agent_id.Trim(),
            request.api_key_id.Trim(),
            reference,
            request.api_key_id.Trim(),
            request.repair_reason.Trim(),
            caller.UserId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct);
        return result.Repaired
            ? Results.Ok(new { status = "repaired" })
            : Results.Conflict(new
            {
                error = "credential_repair_rejected",
                reason = result.RejectionReason.ToString(),
            });
    }

    internal sealed record RepairRequest(
        string agent_id,
        string api_key_id,
        SecretReference secret_reference,
        string repair_reason);
}
