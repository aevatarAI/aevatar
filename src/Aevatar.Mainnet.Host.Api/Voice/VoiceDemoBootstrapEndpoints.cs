using System.Security.Claims;
using Aevatar.AI.Abstractions;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
internal static class VoiceDemoBootstrapEndpoints
{
    private const string VoiceModuleName = "voice_presence_openai";
    private const string RouteRuleId = "voice-demo";
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(150);

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
        [FromServices] IUserAgentCatalogQueryPort catalogQueryPort,
        [FromServices] IChatRoutePolicyCommandPort routePolicyCommandPort,
        [FromServices] IChatRoutePolicyQueryPort routePolicyQueryPort,
        [FromServices] ChatRouteResolver routeResolver,
        [FromServices] IVoicePresenceSessionResolver voiceSessionResolver,
        CancellationToken ct)
    {
        if (!TryResolveScopeId(http.User, out var scopeId))
        {
            return Results.Json(
                new { error = "scope_missing", detail = "Authenticated NyxID scope_id claim is required." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var routingScope = RoutingOwnerScope.ForNyxIdNative(scopeId);
        var scheduledScope = ScheduledOwnerScope.ForNyxIdNative(scopeId);

        var voiceDemoReceipt = await voiceDemoAgentCommandPort.EnsureAsync(scopeId, VoiceModuleName, ct);
        var actorId = voiceDemoReceipt.ActorId;
        await catalogCommandPort.UpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = actorId,
            AgentType = NyxIdChatServiceDefaults.GAgentTypeName,
            TemplateName = "voice-demo",
            OwnerScope = scheduledScope.Clone(),
        }, ct);

        await EnsureVoiceRoutePolicyAsync(
            scopeId,
            actorId,
            routingScope,
            routePolicyCommandPort,
            routePolicyQueryPort,
            ct);

        var catalogObserved = await WaitUntilAsync(
            async () => await catalogQueryPort.GetForCallerAsync(actorId, scheduledScope, ct) is not null,
            ct);

        var routeObserved = await WaitUntilAsync(
            async () => RouteResolvesToDemoActor(
                await routePolicyQueryPort.LookupForCallerAsync(routingScope, ct),
                routingScope,
                routeResolver,
                actorId),
            ct);

        var voiceReady = await WaitUntilAsync(
            async () =>
            {
                var session = await voiceSessionResolver.ResolveAsync(
                    new VoicePresenceSessionRequest(actorId, VoiceModuleName),
                    ct);
                return session?.IsInitialized == true;
            },
            ct);

        if (!catalogObserved || !routeObserved || !voiceReady)
        {
            return Results.Json(
                new
                {
                    error = "voice_demo_not_ready",
                    actor_id = actorId,
                    voice_module_name = VoiceModuleName,
                    catalog_observed = catalogObserved,
                    route_observed = routeObserved,
                    voice_session_ready = voiceReady,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new
        {
            actor_id = actorId,
            voice_module_name = VoiceModuleName,
            policy_rule_id = RouteRuleId,
            nyxid_proxy = "https://nyx.chrono-ai.fun/api/v1/proxy/s/llm-openai",
        });
    }

    private static async Task EnsureVoiceRoutePolicyAsync(
        string scopeId,
        string actorId,
        RoutingOwnerScope routingScope,
        IChatRoutePolicyCommandPort routePolicyCommandPort,
        IChatRoutePolicyQueryPort routePolicyQueryPort,
        CancellationToken ct)
    {
        var existing = await routePolicyQueryPort.LookupForCallerAsync(routingScope, ct);
        var command = new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new ChatRouteCallerScope
            {
                NyxUserId = scopeId,
                Platform = RoutingOwnerScope.NyxIdPlatform,
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

        await routePolicyCommandPort.UpsertAsync(scopeId, command, ct);
    }

    private static bool RouteResolvesToDemoActor(
        ChatRoutePolicySnapshot? snapshot,
        RoutingOwnerScope routingScope,
        ChatRouteResolver resolver,
        string actorId)
    {
        if (snapshot is null)
            return false;

        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Voice,
            CallerScope = new ChatRouteCallerScope
            {
                NyxUserId = routingScope.NyxUserId,
                Platform = routingScope.Platform,
                RegistrationScopeId = routingScope.RegistrationScopeId,
                SenderId = routingScope.SenderId,
            },
            Voice = new VoiceInput
            {
                Codec = VoiceCodec.Pcm16,
                SampleRateHz = 24000,
                Mode = VoiceConversationMode.FullDuplex,
                VadMode = VadMode.Server,
                VoiceModuleName = VoiceModuleName,
            },
        });

        return decision.Action.ActionCase == ChatRouteAction.ActionOneofCase.ForwardToGagent &&
               string.Equals(decision.Action.ForwardToGagent.ActorId, actorId, StringComparison.Ordinal) &&
               string.Equals(decision.Action.ForwardToGagent.VoiceModuleName, VoiceModuleName, StringComparison.Ordinal);
    }

    private static ChatRouteAction ForwardToDemoActor(string actorId) =>
        new()
        {
            ForwardToGagent = new ForwardToGAgent
            {
                ActorId = actorId,
                VoiceModuleName = VoiceModuleName,
            },
        };

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> predicate,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ObservationTimeout;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await predicate())
                return true;

            await Task.Delay(ObservationPollInterval, ct);
        }

        return false;
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
