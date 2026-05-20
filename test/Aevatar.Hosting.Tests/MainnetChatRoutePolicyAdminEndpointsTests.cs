using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.ChatRouting;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

/// <summary>
/// REST admin surface for ChatRoutePolicyGAgent. Without these tests it is
/// easy to regress the JSON parsing, scope-stamp behavior, or the dispatch
/// shape (commandId / actor id / envelope route) — the actor itself
/// (validated by ChatRoutePolicyGAgentTests) is fire-and-forget on the
/// stream, so an endpoint bug surfaces only in operator pain.
/// </summary>
public sealed class MainnetChatRoutePolicyAdminEndpointsTests
{
    private const string Scope = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102";

    [Fact]
    public async Task PutPolicy_StampsOwnerScopeAndDispatchesUpsertCommandToScopeActor()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        await using var app = await CreateAppAsync(actorRuntime, dispatchPort);
        var client = app.GetTestClient();

        var body = """
        {
          "default_target": { "forward_to_model": { "model_name": "deepseek/deepseek-chat" } },
          "rules": [{
            "rule_id": "claude-for-responses",
            "priority": 100,
            "match": { "source_kind": "CHAT_SOURCE_KIND_NYX_RESPONSES" },
            "action": { "forward_to_model": { "model_name": "anthropic/claude-sonnet-4-6" } },
            "description": "use claude for /v1/responses"
          }]
        }
        """;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/scopes/{Scope}/chat-route-policy")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        actorRuntime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be($"chat-route-policy:{Scope}");
        dispatchPort.Dispatches.Should().ContainSingle();
        var (dispatchedActorId, envelope) = dispatchPort.Dispatches[0];
        dispatchedActorId.Should().Be($"chat-route-policy:{Scope}");
        envelope.Route.RouteCase.Should().Be(EnvelopeRoute.RouteOneofCase.Direct);
        envelope.Payload.Is(UpsertChatRoutePolicyRequested.Descriptor).Should().BeTrue();
        var command = envelope.Payload.Unpack<UpsertChatRoutePolicyRequested>();
        command.OwnerScope.NyxUserId.Should().Be(Scope,
            "server must stamp owner_scope from the URL, ignoring whatever the client sent");
        command.OwnerScope.Platform.Should().Be(OwnerScope.NyxIdPlatform);
        command.DefaultTarget.ForwardToModel.ModelName.Should().Be("deepseek/deepseek-chat");
        command.Rules.Should().ContainSingle()
            .Which.Action.ForwardToModel.ModelName.Should().Be("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public async Task PutPolicy_RejectsBodyMissingDefaultTarget()
    {
        // ChatRoutePolicyGAgent.HandleUpsertAsync would throw on an empty
        // default_target; catch the error synchronously at the REST boundary
        // so operators see a 400 + reason instead of a silent fire-and-forget
        // dispatch that drops on the actor side.
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        await using var app = await CreateAppAsync(actorRuntime, dispatchPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/scopes/{Scope}/chat-route-policy")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("default_target_required");
        dispatchPort.Dispatches.Should().BeEmpty(
            "REST validation must short-circuit before fire-and-forget dispatch when the body is invalid");
    }

    [Fact]
    public async Task DeleteRule_DispatchesRemoveCommandWithTrimmedRuleId()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        await using var app = await CreateAppAsync(actorRuntime, dispatchPort);
        var client = app.GetTestClient();

        var response = await client.DeleteAsync($"/api/scopes/{Scope}/chat-route-policy/rules/  claude-for-responses  ");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        dispatchPort.Dispatches.Should().ContainSingle();
        var command = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<RemoveChatRouteRuleRequested>();
        command.RuleId.Should().Be("claude-for-responses");
    }

    [Fact]
    public async Task GetPolicy_ReturnsNotFoundWhenSnapshotMissing()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var queryPort = new StaticPolicyQueryPort(snapshot: null);
        await using var app = await CreateAppAsync(actorRuntime, dispatchPort, queryPort);
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/scopes/{Scope}/chat-route-policy");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain("policy_not_found");
    }

    [Fact]
    public async Task GetPolicy_ReturnsProtobufJsonWithDefaultTargetAndRules()
    {
        var snapshot = new ChatRoutePolicySnapshot(
            new ChatRouteAction { ForwardToModel = new ForwardToModel { ModelName = "deepseek/deepseek-chat" } },
            [
                new ChatRouteRule
                {
                    RuleId = "rule-1", Priority = 50,
                    Match = new ChatRouteMatch { Channel = "lark" },
                    Action = new ChatRouteAction
                    {
                        ForwardToGagent = new ForwardToGAgent { ActorId = "agent-x" },
                    },
                    Description = "test rule",
                },
            ]);
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var queryPort = new StaticPolicyQueryPort(snapshot);
        await using var app = await CreateAppAsync(actorRuntime, dispatchPort, queryPort);
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/scopes/{Scope}/chat-route-policy");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"defaultTarget\"");
        body.Should().Contain("deepseek/deepseek-chat");
        body.Should().Contain("\"ruleId\": \"rule-1\"");
        body.Should().Contain("\"actorId\": \"agent-x\"");
    }

    // ----- Test fixtures -------------------------------------------------------

    private static async Task<WebApplication> CreateAppAsync(
        RecordingActorRuntime actorRuntime,
        RecordingActorDispatchPort dispatchPort,
        IChatRoutePolicyQueryPort? queryPort = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        // Scope guard reads Aevatar:Authentication:Enabled = false in Dev to
        // skip scope-claim cross-check.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:Authentication:Enabled"] = "false",
        });
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AlwaysSucceedAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IActorRuntime>(actorRuntime);
        builder.Services.AddSingleton<IActorDispatchPort>(dispatchPort);
        builder.Services.AddSingleton(queryPort ?? new StaticPolicyQueryPort(snapshot: null));
        // Admin endpoints require ChatRoutePolicyProjectionPort to activate
        // the per-scope projection runtime before dispatch — stub it with a
        // no-op activation service for tests.
        builder.Services.AddSingleton<Aevatar.CQRS.Projection.Core.Abstractions.IProjectionScopeActivationService<ChatRoutePolicyMaterializationRuntimeLease>,
            NoopActivationService>();
        builder.Services.AddSingleton<ChatRoutePolicyProjectionPort>();

        var app = builder.Build();
        app.MapChatRoutePolicyAdminEndpoints();
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

    private sealed class NoopActivationService
        : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionScopeActivationService<ChatRoutePolicyMaterializationRuntimeLease>
    {
        public Task<ChatRoutePolicyMaterializationRuntimeLease> EnsureAsync(
            Aevatar.CQRS.Projection.Core.Abstractions.ProjectionScopeStartRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ChatRoutePolicyMaterializationRuntimeLease(
                new ChatRoutePolicyMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                }));
    }

    private sealed class StaticPolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class AlwaysSucceedAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
