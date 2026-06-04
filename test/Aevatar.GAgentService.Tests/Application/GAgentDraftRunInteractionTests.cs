using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunInteractionTests
{
    private const string ExpectedAgentKind = "tests.draft-run-expected";

    [Fact]
    public async Task Resolver_ShouldRejectExistingActor_WhenActorOwnedKindVerifierDoesNotConfirmExpectedKind()
    {
        var verifier = new StubAgentKindVerifier(result: false);
        var runtime = new StubActorRuntime(new StubActor("actor-1", new ExpectedAgent()));
        var resolver = new GAgentDraftRunCommandTargetResolver(
            runtime,
            new NoOpDraftRunProjectionPort(),
            new NoOpGAgentRunTerminalProjectionPort(),
            verifier,
            agentKindRegistry: BuildRegistry());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: ExpectedAgentKind,
                Prompt: "hello",
                PreferredActorId: "actor-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorKindMismatch);
        verifier.Calls.Should().ContainSingle().Which.Should().Be(("actor-1", ExpectedAgentKind));
    }

    [Fact]
    public async Task Resolver_ShouldAllowExistingActor_WhenVerifierConfirmsExpectedKind()
    {
        var existingActor = new StubActor("actor-1", new ProxyAgent());
        var runtime = new StubActorRuntime(existingActor);
        var verifier = new StubAgentKindVerifier(result: true);
        var resolver = new GAgentDraftRunCommandTargetResolver(
            runtime,
            new NoOpDraftRunProjectionPort(),
            new NoOpGAgentRunTerminalProjectionPort(),
            verifier,
            BuildRegistry());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: ExpectedAgentKind,
                Prompt: "hello",
                PreferredActorId: "actor-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.Actor.Should().BeSameAs(existingActor);
        runtime.CreateByKindCalls.Should().BeEmpty();
        verifier.Calls.Should().ContainSingle().Which.Should().Be(("actor-1", ExpectedAgentKind));
    }

    private sealed class StubActorRuntime(IActor? existingActor) : IActorRuntime
    {
        public List<(Type AgentType, string? ActorId)> CreateCalls { get; } = [];
        public List<(string AgentKind, string? ActorId)> CreateByKindCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(new StubActor(id ?? "created", (IAgent)Activator.CreateInstance(agentType)!));
        }

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            CreateByKindCalls.Add((agentKind, id));
            return Task.FromResult<IActor>(new StubActor(id ?? "created", new ExpectedAgent()));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(existingActor is not null && existingActor.Id == id ? existingActor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(existingActor is not null && existingActor.Id == id);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoOpDraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.FromResult<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?>(null);

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(null);

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpGAgentRunTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default) =>
            Task.FromResult<IGAgentRunTerminalProjectionLease?>(
                new NoOpGAgentRunTerminalProjectionLease(actorId, correlationId, interactionKind));

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record NoOpGAgentRunTerminalProjectionLease(
        string ActorId,
        string CorrelationId,
        GAgentRunTerminalInteractionKind InteractionKind) : IGAgentRunTerminalProjectionLease;

    private static IAgentKindRegistry BuildRegistry() =>
        new AgentKindRegistry(
            [
                new AgentRegistration(
                    Kind: ExpectedAgentKind,
                    ImplementationType: typeof(ExpectedAgent),
                    StateContractType: typeof(object)),
            ]);

    private sealed class StubAgentKindVerifier(bool result) : IAgentKindVerifier
    {
        public List<(string ActorId, string ExpectedKind)> Calls { get; } = [];

        public Task<bool> IsExpectedKindAsync(string actorId, string expectedKind, CancellationToken ct = default)
        {
            _ = ct;
            Calls.Add((actorId, expectedKind));
            return Task.FromResult(result);
        }
    }

    [GAgent(ExpectedAgentKind)]
    private sealed class ExpectedAgent : IAgent
    {
        public string Id { get; } = "expected";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ProxyAgent : IAgent
    {
        public string Id { get; } = "proxy";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
