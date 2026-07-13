using System.Text.Json.Serialization;
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

        var reference = request.SecretReference;
        if (string.IsNullOrWhiteSpace(request.AgentId) ||
            string.IsNullOrWhiteSpace(request.ApiKeyId) ||
            reference is null ||
            string.IsNullOrWhiteSpace(reference.Ref) ||
            string.IsNullOrWhiteSpace(reference.Purpose) ||
            string.IsNullOrWhiteSpace(reference.OwnerScopeKey) ||
            reference.Version <= 0 ||
            string.IsNullOrWhiteSpace(reference.Fingerprint) ||
            string.IsNullOrWhiteSpace(request.RepairReason))
            return Results.BadRequest(new { error = "invalid_repair_request" });

        var result = await repairPort.RepairMissingSecretReferenceAsync(
            request.AgentId.Trim(),
            request.ApiKeyId.Trim(),
            reference,
            request.ApiKeyId.Trim(),
            request.RepairReason.Trim(),
            caller.UserId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct);
        if (result.Outcome.OutcomeCase == UserAgentCatalogCredentialRepairOutcome.OutcomeOneofCase.Repaired)
        {
            return Results.Ok(new
            {
                status = "repaired",
                request_id = result.RequestId,
                command_id = result.Admission.CommandId,
            });
        }

        return Results.Conflict(new
        {
            status = "rejected",
            request_id = result.RequestId,
            command_id = result.Admission.CommandId,
            reason = result.Outcome.Rejected?.Reason.ToString() ?? "unspecified",
        });
    }

    internal sealed record RepairRequest(
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("api_key_id")] string ApiKeyId,
        [property: JsonPropertyName("secret_reference")] SecretReference SecretReference,
        [property: JsonPropertyName("repair_reason")] string RepairReason);
}
