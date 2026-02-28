using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Application;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeApplicationServiceTests
{
    [Fact]
    public async Task PublishImageAsync_ShouldWriteImageSnapshot()
    {
        var service = CreateService();

        var result = await service.PublishImageAsync(
            new PublishImageRequest("img-alpha", "latest", "sha256:001"),
            new DynamicCommandContext("idem-1"));
        var snapshot = await service.GetImageAsync("img-alpha");

        result.AggregateId.Should().Be("dynamic:image:img-alpha");
        result.Status.Should().Be("PUBLISHED");
        snapshot.Should().NotBeNull();
        snapshot!.Tags["latest"].Should().Be("sha256:001");
        snapshot.Digests.Should().Contain("sha256:001");
    }

    [Fact]
    public async Task ApplyComposeAsync_ShouldConvergeStackSnapshot()
    {
        var service = CreateService();

        var result = await service.ApplyComposeAsync(
            new ApplyComposeRequest(
                "stack-a",
                "spec-digest-a",
                3,
                [new ComposeServiceSpec("svc-a", 2, DynamicServiceMode.Hybrid)]),
            new DynamicCommandContext("idem-2"));

        var snapshot = await service.GetStackAsync("stack-a");

        result.Status.Should().Be("APPLIED");
        snapshot.Should().NotBeNull();
        snapshot!.DesiredGeneration.Should().Be(3);
        snapshot.ObservedGeneration.Should().Be(3);
        snapshot.ReconcileStatus.Should().Be("Converged");
    }

    [Fact]
    public async Task RunLifecycle_ShouldTransitionToSucceeded()
    {
        var service = CreateService();

        await service.StartRunAsync(new StartRunRequest("run-a", "container-a"), new DynamicCommandContext("idem-3"));
        await service.CompleteRunAsync(new CompleteRunRequest("run-a", "ok"), new DynamicCommandContext("idem-4"));

        var snapshot = await service.GetRunAsync("run-a");

        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be("Succeeded");
        snapshot.Result.Should().Be("ok");
    }

    [Fact]
    public async Task DuplicateIdempotencyKey_ShouldBeRejected()
    {
        var service = CreateService();
        var ctx = new DynamicCommandContext("idem-dup");

        await service.PublishImageAsync(new PublishImageRequest("img-dup", "v1", "sha256:dup"), ctx);
        var act = () => service.PublishImageAsync(new PublishImageRequest("img-dup", "v1", "sha256:dup"), ctx);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate command*");
    }

    [Fact]
    public async Task IfMatchVersionConflict_ShouldThrow()
    {
        var service = CreateService();

        await service.PublishImageAsync(
            new PublishImageRequest("img-ver", "latest", "sha256:ver"),
            new DynamicCommandContext("idem-ver-1"));

        var act = () => service.PublishImageAsync(
            new PublishImageRequest("img-ver", "stable", "sha256:ver2"),
            new DynamicCommandContext("idem-ver-2", "999"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("VERSION_CONFLICT");
    }

    private static DynamicRuntimeApplicationService CreateService()
    {
        var runtime = new FakeActorRuntime();
        var store = new InMemoryDynamicRuntimeReadStore();
        return new DynamicRuntimeApplicationService(
            runtime,
            store,
            new InMemoryIdempotencyPort(),
            new InMemoryConcurrencyTokenPort(),
            new DefaultImageReferenceResolver(),
            new DefaultScriptComposeSpecValidator(),
            new DefaultScriptComposeReconcilePort());
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult(_actors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RestoreAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent();
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public string Id => string.Empty;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
