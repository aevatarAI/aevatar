using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Scheduled;

internal static class SkillRunnerExternalTriggerEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSkillRunnerExternalTriggerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(
                "/api/skill-runners/{agentId}/external-trigger-sources/{sourceId}/deliveries",
                HandleDeliveryAsync)
            .WithTags("SkillRunnerExternalTriggers")
            .WithEndpointAudit(
                "skill-runner.external-trigger.delivery",
                AuditSensitivityLevel.Confidential,
                "skill_runner",
                EndpointAuditTargetResolvers.FromRouteValue("skill_runner", "agentId"),
                EndpointAuditSanitizers.WithRouteValues("agentId", "sourceId"),
                captureUnauthenticated: true)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> HandleDeliveryAsync(
        HttpContext http,
        string agentId,
        string sourceId,
        [FromServices] ISkillRunnerCommandPort commandPort,
        [FromServices] INyxIdCurrentUserResolver currentUserResolver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(commandPort);
        ArgumentNullException.ThrowIfNull(currentUserResolver);

        // This endpoint is AllowAnonymous because NyxID issues opaque API keys, not JWTs,
        // so the host's JwtBearer fallback policy would 401 valid callers. Authenticate by
        // resolving the caller against NyxID: a missing/invalid/expired bearer resolves to
        // no user and is rejected. Header presence alone is NOT proof of identity.
        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return Results.Unauthorized();

        var callerUserId = await currentUserResolver.ResolveCurrentUserIdAsync(bearerToken, ct);
        if (string.IsNullOrWhiteSpace(callerUserId))
            return Results.Unauthorized();

        var deliveryId = ResolveDeliveryId(http);
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            return Results.BadRequest(new
            {
                error = "missing_event_id",
                detail = "A stable X-Aevatar-Event-Id or X-Event-Id header is required.",
            });
        }

        SkillRunnerExternalTriggerDeliveryRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<SkillRunnerExternalTriggerDeliveryRequest>(JsonOptions, ct)
                      ?? new SkillRunnerExternalTriggerDeliveryRequest();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        var command = new AdmitSkillRunnerExternalTriggerCommand
        {
            Identity = new SkillRunnerExternalTriggerIdentity
            {
                SourceId = sourceId?.Trim() ?? string.Empty,
                DeliveryId = deliveryId.Trim(),
                AdmissionId = NormalizeOptional(request.AdmissionId) ?? Guid.NewGuid().ToString("N"),
                Kind = ResolveKind(request.Kind),
                ReceivedAt = request.ReceivedAtUtc is { } receivedAt
                    ? Timestamp.FromDateTimeOffset(receivedAt)
                    : Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                PayloadSummary = Truncate(NormalizeOptional(request.PayloadSummary) ?? string.Empty, 256),
                PayloadRef = NormalizeOptional(request.PayloadRef) ?? string.Empty,
            },
        };

        try
        {
            var receipt = await commandPort.AdmitExternalTriggerAsync(agentId, command, ct);
            return Results.Accepted(value: new
            {
                status = "accepted",
                actor_id = receipt.ActorId,
                command_id = receipt.CommandId,
                correlation_id = receipt.CorrelationId,
                admission_id = receipt.AdmissionId,
                source_id = receipt.SourceId,
                delivery_id = receipt.DeliveryId,
            });
        }
        catch (SkillRunnerExternalTriggerAdmissionException ex)
            when (ex.Error == SkillRunnerExternalTriggerAdmissionError.RunnerNotFound)
        {
            return Results.NotFound(new
            {
                error = "runner_not_found",
                agent_id = ex.AgentId,
            });
        }
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    private static string? ResolveDeliveryId(HttpContext http) =>
        NormalizeOptional(http.Request.Headers["X-Aevatar-Event-Id"].ToString()) ??
        NormalizeOptional(http.Request.Headers["X-Event-Id"].ToString());

    private static ExternalTriggerSourceKind ResolveKind(string? value) =>
        NormalizeOptional(value)?.ToLowerInvariant() switch
        {
            "channel_inbound" or "channel-inbound" => ExternalTriggerSourceKind.ChannelInbound,
            "webhook" => ExternalTriggerSourceKind.Webhook,
            _ => ExternalTriggerSourceKind.Webhook,
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record SkillRunnerExternalTriggerDeliveryRequest
    {
        public string? AdmissionId { get; init; }

        public string? Kind { get; init; }

        public DateTimeOffset? ReceivedAtUtc { get; init; }

        public string? PayloadSummary { get; init; }

        public string? PayloadRef { get; init; }
    }
}
