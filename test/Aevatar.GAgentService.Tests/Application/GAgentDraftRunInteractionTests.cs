using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunInteractionTests
{
    [Fact]
    public async Task Resolver_ShouldRejectExistingActor_WhenRuntimeTypeDoesNotMatchRequestedType()
    {
        var runtime = new StubActorRuntime(new StubActor("actor-1", new DifferentAgent()));
        var resolver = new GAgentDraftRunCommandTargetResolver(runtime, new NoOpDraftRunProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                ActorTypeName: typeof(ExpectedAgent).AssemblyQualifiedName!,
                Prompt: "hello",
                PreferredActorId: "actor-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
    }

    [Fact]
    public async Task Resolver_ShouldAllowExistingActor_WhenVerifierConfirmsExpectedType()
    {
        var existingActor = new StubActor("actor-1", new ProxyAgent());
        var runtime = new StubActorRuntime(existingActor);
        var resolver = new GAgentDraftRunCommandTargetResolver(
            runtime,
            new NoOpDraftRunProjectionPort(),
            new StubAgentTypeVerifier(result: true));

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                ActorTypeName: typeof(ExpectedAgent).AssemblyQualifiedName!,
                Prompt: "hello",
                PreferredActorId: "actor-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.Actor.Should().BeSameAs(existingActor);
        runtime.CreateCalls.Should().BeEmpty();
    }

    private sealed class StubActorRuntime(IActor? existingActor) : IActorRuntime
    {
        public List<(Type AgentType, string? ActorId)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(new StubActor(id ?? "created", (IAgent)Activator.CreateInstance(agentType)!));
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

        public Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<IGAgentDraftRunProjectionLease?>(null);

        public Task AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DetachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAgentTypeVerifier(bool result) : IAgentTypeVerifier
    {
        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default)
        {
            _ = actorId;
            _ = expectedType;
            _ = ct;
            return Task.FromResult(result);
        }
    }

    private sealed class ExpectedAgent : IAgent
    {
        public string Id { get; } = "expected";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DifferentAgent : IAgent
    {
        public string Id { get; } = "different";
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
