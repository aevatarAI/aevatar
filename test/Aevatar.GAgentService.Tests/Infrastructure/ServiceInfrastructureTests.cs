using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ServiceInfrastructureTests
{
    [Fact]
    public async Task DefaultServiceCommandTargetProvisioner_ShouldCreateAndReuseControlPlaneActors()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var provisioner = new DefaultServiceCommandTargetProvisioner(runtime);

        var definitionTarget = await provisioner.EnsureDefinitionTargetAsync(identity);
        var revisionTarget = await provisioner.EnsureRevisionCatalogTargetAsync(identity);
        var deploymentTarget = await provisioner.EnsureDeploymentTargetAsync(identity);

        definitionTarget.Should().Be(ServiceActorIds.Definition(identity));
        revisionTarget.Should().Be(ServiceActorIds.RevisionCatalog(identity));
        deploymentTarget.Should().Be(ServiceActorIds.Deployment(identity));
        runtime.CreateCalls.Should().Contain((typeof(ServiceDefinitionGAgent), ServiceActorIds.Definition(identity)));
        runtime.CreateCalls.Should().Contain((typeof(ServiceRevisionCatalogGAgent), ServiceActorIds.RevisionCatalog(identity)));
        runtime.CreateCalls.Should().Contain((typeof(ServiceDeploymentManagerGAgent), ServiceActorIds.Deployment(identity)));

        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        runtime.MarkExisting(ServiceActorIds.RevisionCatalog(identity));
        runtime.MarkExisting(ServiceActorIds.Deployment(identity));
        runtime.CreateCalls.Clear();

        await provisioner.EnsureDefinitionTargetAsync(identity);
        await provisioner.EnsureRevisionCatalogTargetAsync(identity);
        await provisioner.EnsureDeploymentTargetAsync(identity);

        runtime.CreateCalls.Should().BeEmpty();
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public List<(Type actorType, string actorId)> CreateCalls { get; } = [];

        public void MarkExisting(string actorId)
        {
            _actors[actorId] = new RecordingActor(actorId);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
