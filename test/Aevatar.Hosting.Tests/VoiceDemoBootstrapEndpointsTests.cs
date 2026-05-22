using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.AI.Abstractions;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.Voice;
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

namespace Aevatar.Hosting.Tests;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Endpoint tests expected synchronous readiness after request-path polling.
//   New principle: Tests assert accepted command receipt semantics and dispatch-only behavior, with readiness left to readmodels or events.
public sealed class VoiceDemoBootstrapEndpointsTests
{
    private const string Scope = "voice-scope-1";

    [Fact]
    public async Task Bootstrap_AcceptsTypedCommandWithoutReadinessPolling()
    {
        var actorRuntime = new RecordingActorRuntime();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var dispatchPort = new RecordingActorDispatchPort();
        await using var app = await CreateAppAsync(
            actorRuntime,
            dispatchPort,
            catalogCommandPort);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/demo/voice/bootstrap", content: null);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        body.Should().ContainKey("status").WhoseValue.ToString().Should().Be("accepted");
        body.Should().ContainKey("actor_id");
        body.Should().ContainKey("correlation_id");
        body.Should().ContainKey("agent_command_id");
        body.Should().ContainKey("route_policy_command_id");
        var demoActorId = body!["actor_id"].ToString()!;
        var correlationId = body["correlation_id"].ToString();
        actorRuntime.CreatedActors.Should().Equal(demoActorId, $"chat-route-policy:{Scope}");
        catalogCommandPort.Commands.Should().ContainSingle()
            .Which.AgentId.Should().Be(demoActorId);

        dispatchPort.Dispatches.Should().HaveCount(2);
        var initDispatch = dispatchPort.Dispatches.Should().ContainSingle(dispatch => dispatch.ActorId == demoActorId)
            .Subject;
        initDispatch.Envelope.Payload.Is(InitializeRoleAgentEvent.Descriptor).Should().BeTrue();
        dispatchPort.Dispatches.Should().ContainSingle(dispatch => dispatch.ActorId == $"chat-route-policy:{Scope}");
        var routeDispatch = dispatchPort.Dispatches.Single(dispatch => dispatch.ActorId == $"chat-route-policy:{Scope}");
        var command = routeDispatch.Envelope.Payload.Unpack<UpsertChatRouteRuleRequested>();
        body["agent_command_id"].ToString().Should().Be(initDispatch.Envelope.Id);
        body["route_policy_command_id"].ToString().Should().Be(routeDispatch.Envelope.Id);
        initDispatch.Envelope.Propagation.CorrelationId.Should().Be(correlationId);
        routeDispatch.Envelope.Propagation.CorrelationId.Should().Be(correlationId);
        initDispatch.Envelope.Runtime.Deduplication.OperationId.Should().Be(initDispatch.Envelope.Id);
        routeDispatch.Envelope.Runtime.Deduplication.OperationId.Should().Be(routeDispatch.Envelope.Id);
        command.OwnerScope.NyxUserId.Should().Be(Scope);
        command.OwnerScope.Platform.Should().Be(RoutingOwnerScope.NyxIdPlatform);
        command.DefaultTargetIfUninitialized.ForwardToGagent.ActorId.Should().Be(demoActorId);
        command.DefaultTargetIfUninitialized.ForwardToGagent.VoiceModuleName.Should().Be("voice_presence_openai");
        var voiceRule = command.Rule;
        voiceRule.RuleId.Should().Be("voice-demo");
        voiceRule.Priority.Should().Be(1000);
        voiceRule.Match.SourceKind.Should().Be(ChatSourceKind.Voice);
        voiceRule.Action.ForwardToGagent.ActorId.Should().Be(demoActorId);
        voiceRule.Action.ForwardToGagent.VoiceModuleName.Should().Be("voice_presence_openai");
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingActorRuntime actorRuntime,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogCommandPort catalogCommandPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IActorRuntime>(actorRuntime);
        builder.Services.AddSingleton<IActorDispatchPort>(dispatchPort);
        builder.Services.AddSingleton<IUserAgentCatalogCommandPort>(catalogCommandPort);
        builder.Services.AddSingleton<VoiceDemoAgentCommandPort>();

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

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
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
}
