using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.AI.Abstractions;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.Hosting.Tests;

// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
public sealed class VoiceDemoBootstrapEndpointsTests
{
    private const string Scope = "voice-scope-1";

    [Fact]
    public async Task Bootstrap_UpsertsVoiceRoutePolicyWithoutProjectionPriming()
    {
        var voiceDemoCommandPort = new RecordingVoiceDemoAgentCommandPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var existing = new ChatRoutePolicySnapshot(
            new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "existing-default" } },
            [
                new ChatRouteRule
                {
                    RuleId = "keep-chat",
                    Priority = 10,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.NyxResponses },
                    Action = new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "kept-model" } },
                    Description = "preserve non-voice-demo rule",
                },
                new ChatRouteRule
                {
                    RuleId = "voice-demo",
                    Priority = 900,
                    Match = new ChatRouteMatch { SourceKind = ChatSourceKind.Voice },
                    Action = new ChatRouteAction { ForwardToGagent = new ForwardToGAgent { ActorId = "old-agent" } },
                    Description = "replace stale voice demo rule",
                },
            ]);
        var routePolicyQueryPort = new UpdatingRoutePolicyQueryPort(existing);
        var routePolicyCommandPort = new RecordingChatRoutePolicyCommandPort(routePolicyQueryPort);
        await using var app = await CreateAppAsync(
            voiceDemoCommandPort,
            catalogCommandPort,
            new RecordingCatalogQueryPort(),
            routePolicyCommandPort,
            routePolicyQueryPort,
            new ReadyVoiceSessionResolver());
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        body.Should().ContainKey("actor_id");
        var demoActorId = body!["actor_id"].ToString()!;
        voiceDemoCommandPort.Commands.Should().ContainSingle()
            .Which.Should().Be((demoActorId, "voice_presence_openai"));
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(demoActorId);

        routePolicyCommandPort.Upserts.Should().ContainSingle();
        var (policyScope, command) = routePolicyCommandPort.Upserts[0];
        policyScope.Should().Be(Scope);
        command.OwnerScope.NyxUserId.Should().Be(Scope);
        command.OwnerScope.Platform.Should().Be(RoutingOwnerScope.NyxIdPlatform);
        command.DefaultTarget.ForwardToModel.ModelName.Should().Be("existing-default");
        command.Rules.Should().ContainSingle(rule => rule.RuleId == "keep-chat")
            .Which.Action.ForwardToModel.ModelName.Should().Be("kept-model");
        var voiceRule = command.Rules.Should().ContainSingle(rule => rule.RuleId == "voice-demo").Subject;
        voiceRule.Priority.Should().Be(1000);
        voiceRule.Match.SourceKind.Should().Be(ChatSourceKind.Voice);
        voiceRule.Action.ForwardToGagent.ActorId.Should().Be(demoActorId);
        voiceRule.Action.ForwardToGagent.VoiceModuleName.Should().Be("voice_presence_openai");
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingVoiceDemoAgentCommandPort voiceDemoCommandPort,
        RecordingCatalogCommandPort catalogCommandPort,
        RecordingCatalogQueryPort catalogQueryPort,
        RecordingChatRoutePolicyCommandPort routePolicyCommandPort,
        UpdatingRoutePolicyQueryPort routePolicyQueryPort,
        ReadyVoiceSessionResolver voiceSessionResolver)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IVoiceDemoAgentCommandPort>(voiceDemoCommandPort);
        builder.Services.AddSingleton<IUserAgentCatalogCommandPort>(catalogCommandPort);
        builder.Services.AddSingleton<IUserAgentCatalogQueryPort>(catalogQueryPort);
        builder.Services.AddSingleton<IChatRoutePolicyCommandPort>(routePolicyCommandPort);
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(routePolicyQueryPort);
        builder.Services.AddSingleton(new ChatRouteResolver(new StaticFallbackProvider()));
        builder.Services.AddSingleton<IVoicePresenceSessionResolver>(voiceSessionResolver);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AevatarStandardClaimTypes.ScopeId, Scope)],
                authenticationType: "test"));
            await next();
        });
        app.MapVoiceDemoBootstrapEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class RecordingVoiceDemoAgentCommandPort : IVoiceDemoAgentCommandPort
    {
        public List<(string ActorId, string VoiceModuleName)> Commands { get; } = [];

        public Task<VoiceDemoAgentCommandAcceptedReceipt> EnsureAsync(
            string actorId,
            string voiceModuleName,
            CancellationToken ct = default)
        {
            Commands.Add((actorId, voiceModuleName));
            return Task.FromResult(new VoiceDemoAgentCommandAcceptedReceipt(
                actorId,
                "voice-demo-command",
                "voice-demo-command"));
        }
    }

    private sealed class RecordingChatRoutePolicyCommandPort(
        UpdatingRoutePolicyQueryPort routePolicyQueryPort) : IChatRoutePolicyCommandPort
    {
        public List<(string ScopeId, UpsertChatRoutePolicyRequested Command)> Upserts { get; } = [];

        public Task<ChatRoutePolicyCommandAcceptedReceipt> UpsertAsync(
            string scopeId,
            UpsertChatRoutePolicyRequested command,
            CancellationToken ct = default)
        {
            Upserts.Add((scopeId, command.Clone()));
            routePolicyQueryPort.Observe(command);
            return Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "route-policy-command",
                "route-policy-command"));
        }

        public Task<ChatRoutePolicyCommandAcceptedReceipt> RemoveRuleAsync(
            string scopeId,
            RemoveChatRouteRuleRequested command,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatRoutePolicyCommandAcceptedReceipt(
                $"chat-route-policy:{scopeId}",
                "route-policy-remove",
                "route-policy-remove"));
    }

    private sealed class RecordingCatalogCommandPort : IUserAgentCatalogCommandPort
    {
        public List<UserAgentCatalogUpsertCommand> Commands { get; } = [];

        public Task UpsertAsync(UserAgentCatalogUpsertCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public Task TombstoneAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingCatalogQueryPort : IUserAgentCatalogQueryPort
    {
        public Task<UserAgentCatalogReadModelEntry?> GetForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<UserAgentCatalogReadModelEntry?>(new()
            {
                AgentId = agentId,
                OwnerScope = caller.Clone(),
            });

        public Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryByCallerAsync(
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>([]);

        public Task<long?> GetStateVersionForCallerAsync(
            string agentId,
            ScheduledOwnerScope caller,
            CancellationToken ct = default) =>
            Task.FromResult<long?>(null);
    }

    private sealed class UpdatingRoutePolicyQueryPort(ChatRoutePolicySnapshot initialSnapshot) : IChatRoutePolicyQueryPort
    {
        private ChatRoutePolicySnapshot _snapshot = initialSnapshot;

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            RoutingOwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult<ChatRoutePolicySnapshot?>(_snapshot);

        public void Observe(UpsertChatRoutePolicyRequested command)
        {
            _snapshot = new ChatRoutePolicySnapshot(command.DefaultTarget, command.Rules);
        }
    }

    private sealed class ReadyVoiceSessionResolver : IVoicePresenceSessionResolver
    {
        public Task<VoicePresenceSession?> ResolveAsync(
            VoicePresenceSessionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<VoicePresenceSession?>(new VoicePresenceSession(
                isInitialized: static () => true,
                isTransportAttached: static () => false,
                attachTransportAsync: static (_, _) => Task.CompletedTask,
                detachTransportAsync: static (_, _) => Task.CompletedTask));
    }

    private sealed class StaticFallbackProvider : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() =>
            new()
            {
                Action = new ChatRouteAction
                {
                    ForwardToModel = new ForwardToModel { ModelName = "fallback" },
                },
            };
    }
}
