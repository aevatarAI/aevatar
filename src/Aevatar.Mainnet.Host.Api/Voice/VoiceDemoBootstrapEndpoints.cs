using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgents.NyxidChat.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Voice demo bootstrap endpoint owned actor creation, route mutation, and readiness polling in Host/API.
//   New principle: Host/API resolves the caller and delegates a typed command to the NyxID chat module, then returns an honest 202 Accepted receipt.
internal static class VoiceDemoBootstrapEndpoints
{
    public static IEndpointRouteBuilder MapVoiceDemoBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/demo/voice/bootstrap", HandleBootstrapAsync)
            .WithTags("VoiceDemo");

        return app;
    }

    private static async Task<IResult> HandleBootstrapAsync(
        HttpContext http,
        [FromServices] VoiceDemoAgentCommandPort commandPort,
        CancellationToken ct)
    {
        // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
        //   Old pattern: The request path blocked until catalog, route, and voice-session reads looked ready.
        //   New principle: The endpoint only adapts HTTP auth claims into a bootstrap command; read-side readiness is queried or observed separately.
        if (!TryResolveScopeId(http.User, out var scopeId))
        {
            return Results.Json(
                new { error = "scope_missing", detail = "Authenticated NyxID scope_id claim is required." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var receipt = await commandPort.AcceptBootstrapAsync(new VoiceDemoBootstrapCommand(scopeId), ct);
        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = receipt.ActorId,
            route_policy_actor_id = receipt.RoutePolicyActorId,
            voice_module_name = receipt.VoiceModuleName,
            policy_rule_id = receipt.PolicyRuleId,
            correlation_id = receipt.CorrelationId,
            agent_command_id = receipt.AgentCommandId,
            route_policy_command_id = receipt.RoutePolicyCommandId,
            nyxid_proxy = "https://nyx.chrono-ai.fun/api/v1/proxy/s/llm-openai",
            readiness = "query readmodels or subscribe to events; this POST only confirms dispatch acceptance",
        });
    }

    private static bool TryResolveScopeId(ClaimsPrincipal user, out string scopeId)
    {
        scopeId = FirstNonEmpty(
            user.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value,
            user.FindFirst("uid")?.Value,
            user.FindFirst("sub")?.Value,
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty;

        return !string.IsNullOrWhiteSpace(scopeId);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = value?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }
}
