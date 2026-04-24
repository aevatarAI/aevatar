using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunActorPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_ShouldReturnUnknownActorType_WhenTypeCannotBeResolved()
    {
        var service = new GAgentDraftRunActorPreparationService(
            new StubActorRuntime(_ => null),
            new RecordingGAgentActorStore());

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest("scope-a", "Aevatar.IamNotReal, Aevatar.IamNotReal"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.UnknownActorType);
    }

    [Fact]
    public async Task PrepareAsync_ShouldReuseExistingActor_WithoutRegisteringAgain()
    {
        var runtime = new StubActorRuntime(id => id == "existing-actor" ? new StubActor(id) : null);
        var actorStore = new RecordingGAgentActorStore
        {
            Actors =
            [
                new GAgentActorGroup(typeof(FakeAgent).AssemblyQualifiedName!, ["existing-actor"])
            ]
        };
        var service = new GAgentDraftRunActorPreparationService(runtime, actorStore);

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.PreparedActor.Should().BeEquivalentTo(new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "existing-actor",
            false));
        actorStore.AddedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ShouldRejectExistingActor_WhenItIsNotRegisteredInRequestedScope()
    {
        var runtime = new StubActorRuntime(id => id == "existing-actor" ? new StubActor(id) : null);
        var actorStore = new RecordingGAgentActorStore();
        var service = new GAgentDraftRunActorPreparationService(runtime, actorStore);

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        actorStore.AddedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_ShouldRegisterGeneratedActorId_WhenActorDoesNotExist()
    {
        var actorStore = new RecordingGAgentActorStore();
        var service = new GAgentDraftRunActorPreparationService(
            new StubActorRuntime(_ => null),
            actorStore);

        var result = await service.PrepareAsync(
            new GAgentDraftRunPreparationRequest(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.PreparedActor.Should().NotBeNull();
        result.PreparedActor!.ScopeId.Should().Be("scope-a");
        result.PreparedActor.ActorTypeName.Should().Be(typeof(FakeAgent).AssemblyQualifiedName!);
        result.PreparedActor.ActorId.Should().NotBeNullOrWhiteSpace();
        result.PreparedActor.RequiresRollbackOnFailure.Should().BeTrue();
        actorStore.AddedActors.Should().ContainSingle();
        actorStore.AddedActors[0].ScopeId.Should().Be("scope-a");
        actorStore.AddedActors[0].GAgentType.Should().Be(typeof(FakeAgent).AssemblyQualifiedName!);
        actorStore.AddedActors[0].ActorId.Should().Be(result.PreparedActor.ActorId);
    }

    [Fact]
    public async Task RollbackAsync_ShouldDestroyActorAndRemoveRegistration_WhenRollbackIsRequired()
    {
        var runtime = new StubActorRuntime(_ => null);
        var actorStore = new RecordingGAgentActorStore();
        var service = new GAgentDraftRunActorPreparationService(runtime, actorStore);
        var preparedActor = new GAgentDraftRunPreparedActor(
            "scope-a",
            typeof(FakeAgent).AssemblyQualifiedName!,
            "generated-actor",
            true);

        await service.RollbackAsync(preparedActor, CancellationToken.None);

        runtime.DestroyedActorIds.Should().ContainSingle("generated-actor");
        actorStore.RemovedActors.Should().ContainSingle();
        actorStore.RemovedActors[0].Should().Be(("scope-a", typeof(FakeAgent).AssemblyQualifiedName!, "generated-actor"));
    }

    [Fact]
    public async Task RollbackAsync_ShouldSkipWork_WhenRollbackIsNotRequired()
    {
        var runtime = new StubActorRuntime(_ => null);
        var actorStore = new RecordingGAgentActorStore();
        var service = new GAgentDraftRunActorPreparationService(runtime, actorStore);

        await service.RollbackAsync(
            new GAgentDraftRunPreparedActor(
                "scope-a",
                typeof(FakeAgent).AssemblyQualifiedName!,
                "existing-actor",
                false),
            CancellationToken.None);

        runtime.DestroyedActorIds.Should().BeEmpty();
        actorStore.RemovedActors.Should().BeEmpty();
    }

    private sealed class RecordingGAgentActorStore : IGAgentActorStore
    {
        public List<GAgentActorGroup> Actors { get; set; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> AddedActors { get; } = [];
        public List<(string ScopeId, string GAgentType, string ActorId)> RemovedActors { get; } = [];

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);

        public Task<IReadOnlyList<GAgentActorGroup>> GetAsync(string scopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GAgentActorGroup>>(Actors);

        public Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            AddedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task AddActorAsync(string scopeId, string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            AddedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            RemovedActors.Add((string.Empty, gagentType, actorId));
            return Task.CompletedTask;
        }

        public Task RemoveActorAsync(string scopeId, string gagentType, string actorId, CancellationToken cancellationToken = default)
        {
            RemovedActors.Add((scopeId, gagentType, actorId));
            return Task.CompletedTask;
        }
    }

    private sealed class StubActorRuntime(Func<string, IActor?> getAsync) : IActorRuntime
    {
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new StubActor(id ?? "created"));

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new StubActor(id ?? "created"));

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult(getAsync(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(getAsync(id) is not null);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new FakeAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public string Id { get; } = "fake-agent";

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
