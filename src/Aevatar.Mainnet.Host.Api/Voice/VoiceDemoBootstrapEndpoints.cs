using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.Mainnet.Host.Api.Voice;

internal static class VoiceDemoBootstrapEndpoints
{
    private const string VoiceModuleName = "voice_presence_openai";
    private const string RouteRuleId = "voice-demo";
    private const string ChatRoutePolicyActorIdPrefix = "chat-route-policy:";
    private const string PublisherActorId = "voice-demo-bootstrap";
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
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        [FromServices] IUserAgentCatalogCommandPort catalogCommandPort,
        [FromServices] IUserAgentCatalogQueryPort catalogQueryPort,
        [FromServices] IChatRoutePolicyQueryPort routePolicyQueryPort,
        [FromServices] ChatRoutePolicyProjectionPort routePolicyProjectionPort,
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
        var actorId = BuildDemoActorId(scopeId);

        await EnsureDemoAgentAsync(actorId, actorRuntime, actorDispatchPort, ct);
        await catalogCommandPort.UpsertAsync(new UserAgentCatalogUpsertCommand
        {
            AgentId = actorId,
            AgentType = NyxIdChatServiceDefaults.GAgentTypeName,
            TemplateName = "voice-demo",
            OwnerScope = scheduledScope.Clone(),
        }, ct);

        var routePolicyActorId = $"{ChatRoutePolicyActorIdPrefix}{scopeId}";
        await EnsureVoiceRoutePolicyAsync(
            routePolicyActorId,
            scopeId,
            actorId,
            routingScope,
            actorRuntime,
            actorDispatchPort,
            routePolicyQueryPort,
            routePolicyProjectionPort,
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

    private static async Task EnsureDemoAgentAsync(
        string actorId,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        CancellationToken ct)
    {
        var actor = await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = "voice-demo",
            RoleName = "Voice Demo Agent",
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = "You are the Aevatar voice demo agent. Reply conversationally and keep spoken answers concise.",
            MaxHistoryMessages = 16,
            StreamBufferCapacity = 64,
            EventModules = VoiceModuleName,
        };

        await DispatchAsync(actor.Id, initialize, actorDispatchPort, ct);
    }

    private static async Task EnsureVoiceRoutePolicyAsync(
        string routePolicyActorId,
        string scopeId,
        string actorId,
        RoutingOwnerScope routingScope,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        IChatRoutePolicyQueryPort routePolicyQueryPort,
        ChatRoutePolicyProjectionPort routePolicyProjectionPort,
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

        var actor = await actorRuntime.CreateAsync<ChatRoutePolicyGAgent>(routePolicyActorId, ct);
        await routePolicyProjectionPort.EnsureProjectionForActorAsync(actor.Id, ct);
        await DispatchAsync(actor.Id, command, actorDispatchPort, ct);
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

    private static async Task DispatchAsync(
        string actorId,
        IMessage command,
        IActorDispatchPort actorDispatchPort,
        CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };

        await actorDispatchPort.DispatchAsync(actorId, envelope, ct);
    }

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

    private static string BuildDemoActorId(string scopeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{NyxIdChatServiceDefaults.ActorIdPrefix}-voice-demo-{hash}";
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
