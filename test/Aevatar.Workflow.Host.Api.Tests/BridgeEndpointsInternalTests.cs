using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Bridge;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class BridgeEndpointsInternalTests
{
    [Fact]
    public void HandleBridgeCallbackTokenIssue_ShouldReturnIssuedTokenPayload()
    {
        var tokenService = new FakeBridgeCallbackTokenService();
        var options = Options.Create(new WorkflowBridgeOptions
        {
            BridgeActorId = "bridge:default",
            DefaultTokenTtlMs = 30_000,
            MaxTokenTtlMs = 60_000,
        });

        var result = WorkflowCapabilityEndpoints.HandleBridgeCallbackTokenIssue(
            new BridgeCallbackTokenIssueInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-1",
                SignalName = "openclaw_reply",
                TimeoutMs = 15_000,
            },
            tokenService,
            options);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status200OK);
        payload.Json.GetProperty("token").GetString().Should().Be("issued-token");
        payload.Json.GetProperty("tokenId").GetString().Should().Be("token-1");
        payload.Json.GetProperty("bridgeActorId").GetString().Should().Be("bridge:default");
    }

    [Fact]
    public async Task HandleBridgeIngress_WhenSourceAllowed_ShouldForwardEventToBridgeActor()
    {
        var runtime = new RecordingActorRuntime();
        var options = Options.Create(new WorkflowBridgeOptions
        {
            BridgeActorId = "bridge:test",
            RequireSourceAllowList = true,
            AllowedSources = ["telegram.openclaw"],
        });

        var result = await WorkflowCapabilityEndpoints.HandleBridgeIngress(
            new BridgeIngressInput
            {
                CallbackToken = "token",
                Source = "telegram.openclaw",
                Payload = "reply",
                SourceMessageId = "msg-1",
                SourceChatId = "chat-1",
                SourceUserId = "user-1",
            },
            runtime,
            options,
            CancellationToken.None);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status200OK);
        payload.Json.GetProperty("accepted").GetBoolean().Should().BeTrue();
        payload.Json.GetProperty("bridgeActorId").GetString().Should().Be("bridge:test");

        runtime.Actor.Received.Should().ContainSingle();
        var bridgeEvent = runtime.Actor.Received[0].Payload!.Unpack<BridgeInboundCallbackReceivedEvent>();
        bridgeEvent.CallbackToken.Should().Be("token");
        bridgeEvent.Source.Should().Be("telegram.openclaw");
        bridgeEvent.Payload.Should().Be("reply");
        bridgeEvent.SourceMessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task HandleBridgeIngress_WhenBridgeAgentTypeConfigured_ShouldCreateConfiguredAgentType()
    {
        var runtime = new RecordingActorRuntime();
        var options = Options.Create(new WorkflowBridgeOptions
        {
            BridgeActorId = "bridge:create-me",
            BridgeAgentType = typeof(TestBridgeGAgent).AssemblyQualifiedName!,
            RequireSourceAllowList = false,
        });

        var result = await WorkflowCapabilityEndpoints.HandleBridgeIngress(
            new BridgeIngressInput
            {
                CallbackToken = "token",
                Source = "telegram.openclaw",
                Payload = "reply",
            },
            runtime,
            options,
            CancellationToken.None);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status200OK);
        runtime.CreatedAgentTypes.Should().ContainSingle()
            .Which.Should().Be(typeof(TestBridgeGAgent));
    }

    [Fact]
    public async Task HandleBridgeIngress_WhenSourceBlocked_ShouldReturnBadRequest()
    {
        var runtime = new RecordingActorRuntime();
        var options = Options.Create(new WorkflowBridgeOptions
        {
            BridgeActorId = "bridge:test",
            RequireSourceAllowList = true,
            AllowedSources = ["telegram.openclaw"],
        });

        var result = await WorkflowCapabilityEndpoints.HandleBridgeIngress(
            new BridgeIngressInput
            {
                CallbackToken = "token",
                Source = "unknown",
            },
            runtime,
            options,
            CancellationToken.None);

        var payload = ExtractResultPayload(result);
        payload.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        runtime.Actor.Received.Should().BeEmpty();
    }

    private static (int StatusCode, JsonElement Json) ExtractResultPayload(IResult result)
    {
        var resultType = result.GetType();
        var statusCode = resultType.GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;
        var value = resultType.GetProperty("Value")?.GetValue(result);
        var json = JsonSerializer.SerializeToElement(value);
        return (statusCode, json);
    }

    private sealed class FakeBridgeCallbackTokenService : IBridgeCallbackTokenService
    {
        public BridgeCallbackTokenIssueResult Issue(
            BridgeCallbackTokenIssueRequest request,
            DateTimeOffset nowUtc)
        {
            _ = request;
            _ = nowUtc;
            return new BridgeCallbackTokenIssueResult
            {
                Token = "issued-token",
                TokenId = "token-1",
                Claims = new BridgeCallbackTokenClaims
                {
                    TokenId = "token-1",
                    ActorId = "actor-1",
                    RunId = "run-1",
                    StepId = "wait-1",
                    SignalName = "openclaw_reply",
                    IssuedAtUnixTimeMs = 10,
                    ExpiresAtUnixTimeMs = 20,
                    Nonce = "nonce",
                },
            };
        }

        public bool TryValidate(
            string token,
            DateTimeOffset nowUtc,
            out BridgeCallbackTokenClaims claims,
            out string error)
        {
            _ = token;
            _ = nowUtc;
            claims = new BridgeCallbackTokenClaims
            {
                TokenId = "token-1",
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-1",
                SignalName = "openclaw_reply",
                IssuedAtUnixTimeMs = 10,
                ExpiresAtUnixTimeMs = 20,
                Nonce = "nonce",
            };
            error = string.Empty;
            return true;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public RecordingActor Actor { get; } = new("bridge:test");
        public List<Type> CreatedAgentTypes { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(Actor);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreatedAgentTypes.Add(agentType);
            if (!string.IsNullOrWhiteSpace(id))
                Actor.SetId(id);
            return Task.FromResult<IActor>(Actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _ = id;
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            if (string.Equals(id, Actor.Id, StringComparison.Ordinal))
                return Task.FromResult<IActor?>(Actor);
            return Task.FromResult<IActor?>(null);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(string.Equals(id, Actor.Id, StringComparison.Ordinal));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        private string _id = id;
        public List<EventEnvelope> Received { get; } = [];

        public string Id => _id;
        public IAgent Agent { get; } = new NoopAgent("bridge-agent");

        public void SetId(string id) => _id = id;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            Received.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestBridgeGAgent : BridgeGAgent
    {
        public TestBridgeGAgent(
            IActorRuntime runtime,
            IBridgeCallbackTokenService tokenService)
            : base(runtime, tokenService)
        {
        }
    }
}
