using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Direct coverage for the chat route policy command port that Mainnet Host composes.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Host endpoint tests inspected Host-local EventEnvelope construction directly.
//   New principle: command-port tests own envelope/dispatch assertions; Host endpoint tests only verify admission into the port.
public sealed class ChatRoutePolicyCommandPortTests
{
    [Fact]
    public async Task UpsertAsync_DispatchesDirectEnvelopeAndReturnsAcceptedReceipt()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();
        var command = new UpsertChatRoutePolicyRequested
        {
            DefaultTarget = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = "chrono-llm/gpt-5.5" },
            },
        };

        var receipt = await commandPort.UpsertAsync(" scope-1 ", command);

        actorRuntime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be(("chat-route-policy:scope-1", typeof(ChatRoutePolicyGAgent)));
        dispatchPort.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatchPort.Dispatches[0];
        actorId.Should().Be("chat-route-policy:scope-1");
        envelope.Id.Should().NotBeNullOrWhiteSpace();
        envelope.Payload.Unpack<UpsertChatRoutePolicyRequested>()
            .DefaultTarget.ForwardToModel.ModelName.Should().Be("chrono-llm/gpt-5.5");
        envelope.Route.PublisherActorId.Should().Be("chat-route-policy-admin");
        envelope.Route.Direct.TargetActorId.Should().Be("chat-route-policy:scope-1");
        envelope.Propagation.CorrelationId.Should().Be(envelope.Id);
        envelope.Runtime.DeliveryIdentity.OperationId.Should().Be(envelope.Id);
        receipt.Should().Be(new ChatRoutePolicyCommandAcceptedReceipt(
            "chat-route-policy:scope-1",
            envelope.Id,
            envelope.Id));
    }

    [Fact]
    public async Task RemoveRuleAsync_DispatchesRemovePayloadToScopeActor()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();

        await commandPort.RemoveRuleAsync("scope-1", new RemoveChatRouteRuleRequested { RuleId = "drop-me" });

        dispatchPort.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatchPort.Dispatches[0];
        actorId.Should().Be("chat-route-policy:scope-1");
        envelope.Payload.Unpack<RemoveChatRouteRuleRequested>().RuleId.Should().Be("drop-me");
        envelope.Route.PublisherActorId.Should().Be("chat-route-policy-admin");
        envelope.Route.Direct.TargetActorId.Should().Be("chat-route-policy:scope-1");
    }

    [Fact]
    public async Task UpsertRuleAsync_DispatchesSingleRulePayloadToScopeActor()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();
        var command = new UpsertChatRouteRuleRequested
        {
            Rule = new ChatRouteRule
            {
                RuleId = "voice-demo",
                Action = new ChatRouteAction
                {
                    ForwardToModel = new ForwardToModel { ModelName = "voice-model" },
                },
            },
        };

        await commandPort.UpsertRuleAsync("scope-1", command);

        dispatchPort.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatchPort.Dispatches[0];
        actorId.Should().Be("chat-route-policy:scope-1");
        envelope.Payload.Unpack<UpsertChatRouteRuleRequested>()
            .Rule.RuleId.Should().Be("voice-demo");
        envelope.Route.PublisherActorId.Should().Be("chat-route-policy-admin");
        envelope.Route.Direct.TargetActorId.Should().Be("chat-route-policy:scope-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpsertAsync_RejectsMissingScopeId(string? scopeId)
    {
        using var provider = CreateProvider(new RecordingActorRuntime(), new RecordingActorDispatchPort());
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();
        var command = new UpsertChatRoutePolicyRequested();

        var act = () => commandPort.UpsertAsync(scopeId!, command);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("scopeId");
    }

    [Fact]
    public async Task UpsertAsync_RejectsNullCommand()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();

        var act = () => commandPort.UpsertAsync("scope-1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("command");
        actorRuntime.CreatedActors.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveRuleAsync_RejectsNullCommand()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();

        var act = () => commandPort.RemoveRuleAsync("scope-1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("command");
        actorRuntime.CreatedActors.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertRuleAsync_RejectsNullCommand()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IChatRoutePolicyCommandPort>();

        var act = () => commandPort.UpsertRuleAsync("scope-1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("command");
        actorRuntime.CreatedActors.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    private static ServiceProvider CreateProvider(
        RecordingActorRuntime actorRuntime,
        RecordingActorDispatchPort dispatchPort)
    {
        return new ServiceCollection()
            .AddSingleton<IActorRuntime>(actorRuntime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddChatRoutingAgents()
            .BuildServiceProvider();
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(string ActorId, System.Type AgentType)> CreatedActors { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            CreatedActors.Add((id, agentType));
            return Task.FromResult<IActor>(new StubActor(id));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new StubActor(id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
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
}
