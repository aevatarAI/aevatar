using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceCommandApplicationServiceTests
{
    [Fact]
    public async Task CreateServiceAsync_ShouldCreateDefinitionActor_EnsureProjection_AndDispatchCommand()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(runtime, dispatchPort, catalogProjectionPort, revisionProjectionPort);

        var receipt = await service.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorType.Should().Be(typeof(ServiceDefinitionGAgent));
        runtime.CreateCalls[0].actorId.Should().Be(ServiceActorIds.Definition(identity));
        catalogProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Definition(identity));
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be(ServiceActorIds.Definition(identity));
        dispatchPort.Calls[0].envelope.Route.GetTargetActorId().Should().Be(ServiceActorIds.Definition(identity));
        receipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        receipt.CorrelationId.Should().Be(ServiceKeys.Build(identity));
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldRejectMissingDefinition()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingActorRuntime(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var act = () => service.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");
    }

    [Fact]
    public async Task PublishRevisionAsync_ShouldCreateRevisionActor_EnsureProjection_AndDispatchCommand()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        var dispatchPort = new RecordingActorDispatchPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(
            runtime,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            revisionProjectionPort);

        var receipt = await service.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorType.Should().Be(typeof(ServiceRevisionCatalogGAgent));
        runtime.CreateCalls[0].actorId.Should().Be(ServiceActorIds.RevisionCatalog(identity));
        revisionProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.RevisionCatalog(identity));
        receipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r1");
        dispatchPort.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateServiceAsync_ShouldReuseExistingDefinitionActor()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            runtime,
            dispatchPort,
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var receipt = await service.UpdateServiceAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });

        runtime.CreateCalls.Should().BeEmpty();
        catalogProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Definition(identity));
        dispatchPort.Calls.Should().ContainSingle();
        receipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
    }

    [Fact]
    public async Task PrepareRevisionAsync_ShouldReuseExistingRevisionActor_WhenCatalogAlreadyExists()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        runtime.MarkExisting(ServiceActorIds.RevisionCatalog(identity));
        var dispatchPort = new RecordingActorDispatchPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(
            runtime,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            revisionProjectionPort);

        var receipt = await service.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });

        runtime.CreateCalls.Should().BeEmpty();
        revisionProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.RevisionCatalog(identity));
        dispatchPort.Calls.Should().ContainSingle();
        receipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r2");
    }

    [Fact]
    public async Task SetDefaultServingRevisionAsync_ShouldCreateDefinitionActor_WhenMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            runtime,
            dispatchPort,
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var receipt = await service.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r3",
        });

        runtime.CreateCalls.Should().ContainSingle(x => x.actorType == typeof(ServiceDefinitionGAgent));
        catalogProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Definition(identity));
        dispatchPort.Calls.Should().ContainSingle();
        receipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
    }

    [Fact]
    public async Task ActivateServingRevisionAsync_ShouldCreateDeploymentActor_AndEnsureCatalogProjection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            runtime,
            dispatchPort,
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var receipt = await service.ActivateServingRevisionAsync(new ActivateServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r4",
        });

        runtime.CreateCalls.Should().ContainSingle(x => x.actorType == typeof(ServiceDeploymentManagerGAgent));
        catalogProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Deployment(identity));
        dispatchPort.Calls.Should().ContainSingle(x => x.actorId == ServiceActorIds.Deployment(identity));
        receipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
    }

    [Fact]
    public async Task PublishRevisionAsync_ShouldThrow_WhenActorDisappearsAfterExistsCheck()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExisting(ServiceActorIds.Definition(identity));
        runtime.MarkExistsWithoutActor(ServiceActorIds.RevisionCatalog(identity));
        var service = CreateService(
            runtime,
            new RecordingActorDispatchPort(),
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var act = () => service.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r5",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{ServiceActorIds.RevisionCatalog(identity)}*not found after existence check*");
    }

    private static ServiceCommandApplicationService CreateService(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogProjectionPort catalogProjectionPort,
        RecordingRevisionProjectionPort revisionProjectionPort) =>
        new(runtime, dispatchPort, catalogProjectionPort, revisionProjectionPort);

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _existingWithoutActor = new(StringComparer.Ordinal);

        public List<(Type actorType, string actorId)> CreateCalls { get; } = [];

        public void MarkExisting(string actorId) => _actors[actorId] = new RecordingActor(actorId);

        public void MarkExistsWithoutActor(string actorId) => _existingWithoutActor.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? AgentId.New(agentType);
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            _existingWithoutActor.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id) || _existingWithoutActor.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogProjectionPort : IServiceCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRevisionProjectionPort : IServiceRevisionCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
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
