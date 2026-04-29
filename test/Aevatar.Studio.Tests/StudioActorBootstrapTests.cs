using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioActorBootstrapTests
{
    [Fact]
    public async Task EnsureAsync_ShouldCreateMissingActorAndActivateProjection()
    {
        var runtime = new RecordingRuntime();
        var activation = new RecordingActivationService();
        var bootstrap = new StudioActorBootstrap(
            runtime,
            new StudioProjectionPort(activation));

        var actor = await bootstrap.EnsureAsync<StudioMemberGAgent>(
            "studio-member:scope-1:m-1",
            CancellationToken.None);

        actor.Id.Should().Be("studio-member:scope-1:m-1");
        runtime.GetActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        runtime.CreateActorIds.Should().ContainSingle()
            .Which.Should().Be("studio-member:scope-1:m-1");
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("studio-member:scope-1:m-1");
        activation.Requests[0].ProjectionKind.Should().Be(StudioMemberGAgent.ProjectionKind);
        activation.Requests[0].Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public async Task EnsureAsync_ShouldReuseExistingActorAndActivateProjection()
    {
        var runtime = new RecordingRuntime();
        runtime.Actors["studio-member:scope-1:m-1"] = new StubActor("studio-member:scope-1:m-1");
        var activation = new RecordingActivationService();
        var bootstrap = new StudioActorBootstrap(
            runtime,
            new StudioProjectionPort(activation));

        var actor = await bootstrap.EnsureAsync<StudioMemberGAgent>(
            "studio-member:scope-1:m-1",
            CancellationToken.None);

        actor.Id.Should().Be("studio-member:scope-1:m-1");
        runtime.CreateActorIds.Should().BeEmpty();
        activation.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetExistingAsync_ShouldReturnNullWithoutActivatingProjection_WhenActorMissing()
    {
        var runtime = new RecordingRuntime();
        var activation = new RecordingActivationService();
        var bootstrap = new StudioActorBootstrap(
            runtime,
            new StudioProjectionPort(activation));

        var actor = await bootstrap.GetExistingAsync<StudioMemberGAgent>(
            "studio-member:scope-1:m-1",
            CancellationToken.None);

        actor.Should().BeNull();
        activation.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExistingActorAsync_ShouldNotActivateProjection()
    {
        var runtime = new RecordingRuntime();
        runtime.Actors["studio-member:scope-1:m-1"] = new StubActor("studio-member:scope-1:m-1");
        var activation = new RecordingActivationService();
        var bootstrap = new StudioActorBootstrap(
            runtime,
            new StudioProjectionPort(activation));

        var actor = await bootstrap.GetExistingActorAsync<StudioMemberGAgent>(
            "studio-member:scope-1:m-1",
            CancellationToken.None);

        actor.Should().NotBeNull();
        activation.Requests.Should().BeEmpty();
    }

    private sealed class RecordingActivationService
        : IProjectionScopeActivationService<StudioMaterializationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<StudioMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new StudioMaterializationRuntimeLease(new StudioMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        }
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> Actors { get; } = new(StringComparer.Ordinal);

        public List<string> GetActorIds { get; } = [];

        public List<string> CreateActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ArgumentNullException.ThrowIfNull(id);
            CreateActorIds.Add(id);
            var actor = new StubActor(id);
            Actors[id] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(id);
            CreateActorIds.Add(id);
            var actor = new StubActor(id);
            Actors[id] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            Actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            GetActorIds.Add(id);
            return Task.FromResult(Actors.TryGetValue(id, out var actor) ? actor : null);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
