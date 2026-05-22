using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.AI.Abstractions;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Scheduled;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;

namespace Aevatar.Hosting.Tests;

// Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
//   Old pattern: voice bootstrap removed request-path projection priming without endpoint behavior coverage.
//   New principle: test the route-policy upsert command shape and dispatch-only request path.
public sealed class VoiceDemoBootstrapEndpointsTests
{
    private const string Scope = "voice-scope-1";

    [Fact]
    public async Task Bootstrap_UpsertsVoiceRoutePolicyWithoutProjectionPriming()
    {
        var actorRuntime = new RecordingActorRuntime();
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
        var dispatchPort = new RecordingActorDispatchPort(routePolicyQueryPort);
        await using var app = await CreateAppAsync(
            actorRuntime,
            dispatchPort,
            catalogCommandPort,
            new RecordingCatalogQueryPort(),
            routePolicyQueryPort,
            new ReadyVoiceSessionResolver());
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        body.Should().ContainKey("actor_id");
        var demoActorId = body!["actor_id"].ToString()!;
        actorRuntime.CreatedActors.Should().Equal(demoActorId, $"chat-route-policy:{Scope}");
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(demoActorId);

        dispatchPort.Dispatches.Should().HaveCount(2);
        dispatchPort.Dispatches.Should().ContainSingle(dispatch => dispatch.ActorId == demoActorId)
            .Which.Envelope.Payload.Is(InitializeRoleAgentEvent.Descriptor).Should().BeTrue();
        dispatchPort.Dispatches.Should().ContainSingle(dispatch => dispatch.ActorId == $"chat-route-policy:{Scope}");
        var routeDispatch = dispatchPort.Dispatches.Single(dispatch => dispatch.ActorId == $"chat-route-policy:{Scope}");
        var command = routeDispatch.Envelope.Payload.Unpack<UpsertChatRoutePolicyRequested>();
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
        RecordingActorRuntime actorRuntime,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogCommandPort catalogCommandPort,
        RecordingCatalogQueryPort catalogQueryPort,
        UpdatingRoutePolicyQueryPort routePolicyQueryPort,
        ReadyVoiceSessionResolver voiceSessionResolver)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IActorRuntime>(actorRuntime);
        builder.Services.AddSingleton<IActorDispatchPort>(dispatchPort);
        builder.Services.AddSingleton<IUserAgentCatalogCommandPort>(catalogCommandPort);
        builder.Services.AddSingleton<IUserAgentCatalogQueryPort>(catalogQueryPort);
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

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> CreatedActors { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ArgumentNullException.ThrowIfNull(id);
            CreatedActors.Add(id);
            return Task.FromResult<IActor>(new StubActor(id));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<IAgent>(id, ct);

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new StubActor(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class RecordingActorDispatchPort(UpdatingRoutePolicyQueryPort routePolicyQueryPort) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            if (envelope.Payload.Is(UpsertChatRoutePolicyRequested.Descriptor))
            {
                routePolicyQueryPort.Observe(envelope.Payload.Unpack<UpsertChatRoutePolicyRequested>());
            }

            return Task.CompletedTask;
        }
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
