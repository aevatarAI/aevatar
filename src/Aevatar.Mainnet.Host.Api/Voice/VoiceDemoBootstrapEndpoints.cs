using System.Security.Claims;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf.WellKnownTypes;
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
    private const string InvokeGAgentToolName = "aevatar_invoke_gagent";

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
        [FromServices] IChatRoutePolicyQueryPort routePolicyQueryPort,
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
            AgentType = NyxIdChatServiceDefaults.GAgentTypeName,
            TemplateName = "voice-demo",
            OwnerScope = ownerScope.Clone(),
        }, ct);

        var routePolicyReceipt = await EnsureVoiceRoutePolicyAsync(
            scopeId,
            actorId,
            ownerScope,
            routePolicyCommandPort,
            routePolicyQueryPort,
            ct);

        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = actorId,
            route_policy_actor_id = routePolicyReceipt.ActorId,
            voice_module_name = VoiceModuleName,
            policy_rule_id = RouteRuleId,
            agent_command_id = voiceDemoReceipt.CommandId,
            agent_correlation_id = voiceDemoReceipt.CorrelationId,
            route_policy_command_id = routePolicyReceipt.CommandId,
            route_policy_correlation_id = routePolicyReceipt.CorrelationId,
            nyxid_proxy = "https://nyx.chrono-ai.fun/api/v1/proxy/s/llm-openai",
            readiness = "query readmodels or subscribe to events; this POST only confirms dispatch acceptance",
        });
    }

    private static async Task<ChatRoutePolicyCommandAcceptedReceipt> EnsureVoiceRoutePolicyAsync(
        string scopeId,
        string actorId,
        OwnerScope ownerScope,
        IChatRoutePolicyCommandPort routePolicyCommandPort,
        IChatRoutePolicyQueryPort routePolicyQueryPort,
        CancellationToken ct)
    {
        var existing = await routePolicyQueryPort.LookupForCallerAsync(ownerScope, ct);
        var command = new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new OwnerScope
            {
                NyxUserId = scopeId,
                Platform = OwnerScope.NyxIdPlatform,
            },
            DefaultTarget = existing?.DefaultTarget.Clone() ?? ForwardToDemoActor(actorId),
        };

        if (existing is not null)
        {
            command.Rules.AddRange(existing.Rules
                .Where(static rule => !string.Equals(rule.RuleId, RouteRuleId, StringComparison.Ordinal))
                .Select(static rule => rule.Clone()));
        }

        command.Rules.Add(new ChatRouteRule
        {
            RuleId = RouteRuleId,
            Priority = 1000,
            Match = new ChatRouteMatch
            {
                SourceKind = ChatSourceKind.Voice,
            },
            Action = ForwardToDemoActor(actorId),
            Description = "route browser voice demo to the current user's mainnet agent",
        });

        return await routePolicyCommandPort.UpsertAsync(scopeId, command, ct);
    }

    private static ChatRouteAction ForwardToDemoActor(string actorId) =>
        new()
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.VoiceRealtime },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = InvokeGAgentToolName,
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["actor_id"] = Value.ForString(actorId),
                            ["voice_module_name"] = Value.ForString(VoiceModuleName),
                        },
                    },
                },
            },
        };

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
