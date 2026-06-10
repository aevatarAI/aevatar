using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Voice demo bootstrap endpoint owned actor creation, route mutation, and readiness polling in Host/API.
//   New principle: Host/API resolves the caller, delegates mutations through Application command ports, then returns an honest 202 Accepted receipt.
internal static class VoiceDemoBootstrapEndpoints
{
    private const string VoiceModuleName = "voice_presence_openai";
    private const string RouteRuleId = "voice-demo";

    public static IEndpointRouteBuilder MapVoiceDemoBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/demo/voice/bootstrap", HandleBootstrapAsync)
            .WithTags("VoiceDemo");

        return app;
    }

    private static async Task<IResult> HandleBootstrapAsync(
        HttpContext http,
        [FromServices] IVoiceDemoAgentCommandPort voiceDemoAgentCommandPort,
        [FromServices] IUserAgentCatalogCommandPort catalogCommandPort,
        [FromServices] IChatRoutePolicyCommandPort routePolicyCommandPort,
        [FromServices] IChatRouteFallbackProvider fallbackProvider,
        CancellationToken ct)
    {
        // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
        //   Old pattern: The request path blocked until catalog, route, and voice-session reads looked ready.
        //   New principle: This POST only admits commands; read-side readiness is queried or observed separately.
        if (!TryResolveScopeId(http.User, out var scopeId))
        {
            return Results.Json(
                new { error = "scope_missing", detail = "Authenticated NyxID scope_id claim is required." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var ownerScope = OwnerScope.ForNyxIdNative(scopeId);

        var voiceDemoReceipt = await voiceDemoAgentCommandPort.EnsureAsync(scopeId, VoiceModuleName, ct);
        var actorId = voiceDemoReceipt.ActorId;

        await catalogCommandPort.UpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = actorId,
            AgentType = NyxIdChatServiceDefaults.GAgentKind,
            TemplateName = "voice-demo",
            OwnerScope = ownerScope.Clone(),
        }, ct);

        var routePolicyReceipt = await EnsureVoiceRoutePolicyAsync(
            scopeId,
            ownerScope,
            actorId,
            routePolicyCommandPort,
            fallbackProvider,
            ct);

        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = actorId,
            route_policy_actor_id = routePolicyReceipt?.ActorId,
            voice_module_name = VoiceModuleName,
            policy_rule_id = RouteRuleId,
            agent_command_id = voiceDemoReceipt.CommandId,
            agent_correlation_id = voiceDemoReceipt.CorrelationId,
            route_policy_command_id = routePolicyReceipt?.CommandId,
            route_policy_correlation_id = routePolicyReceipt?.CorrelationId,
            readiness = "query readmodels or subscribe to events; this POST only confirms dispatch acceptance",
        });
    }

    private static async Task<ChatRoutePolicyCommandAcceptedReceipt?> EnsureVoiceRoutePolicyAsync(
        string scopeId,
        OwnerScope ownerScope,
        string actorId,
        IChatRoutePolicyCommandPort routePolicyCommandPort,
        IChatRouteFallbackProvider fallbackProvider,
        CancellationToken ct)
    {
        var command = new UpsertChatRouteRuleRequested
        {
            OwnerScope = ownerScope.Clone(),
            DefaultTargetIfUninitialized = fallbackProvider.GetFallbackDecision().Action?.Clone(),
            Rule = new ChatRouteRule
            {
                RuleId = RouteRuleId,
                Priority = 900,
                Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice },
                Action = ChatRouteActionTargets.ForwardToVoiceAttachTarget(
                    actorId,
                    VoiceModuleName),
                Description = "Voice demo typed attach target.",
            },
        };

        return await routePolicyCommandPort.UpsertRuleAsync(scopeId, command, ct);
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
