using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRevisionCatalogGAgentTests
{
    [Fact]
    public async Task CreatePreparePublish_ShouldPersistArtifact_AndReplayPublishedState()
    {
        var eventStore = new InMemoryEventStore();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.RevisionCatalog(identity);
        var adapter = new RecordingAdapter(_ => Task.FromResult(
            GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1")));
        var agent = CreateAgent(eventStore, artifactStore, adapter, actorId);
        await agent.ActivateAsync();

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        await agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });
        await agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        var record = agent.State.Revisions["r1"];
        record.Status.Should().Be(ServiceRevisionStatus.Published);
        record.ArtifactHash.Should().NotBeNullOrWhiteSpace();
        record.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        (await artifactStore.GetAsync(ServiceKeys.Build(identity), "r1")).Should().NotBeNull();

        await agent.DeactivateAsync();

        var replayed = CreateAgent(eventStore, artifactStore, adapter, actorId);
        await replayed.ActivateAsync();
        replayed.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Published);
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldPersistPreparationFailure_WhenAdapterThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => throw new InvalidOperationException("prepare failed")),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("prepare failed");
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.PreparationFailed);
        agent.State.Revisions["r1"].FailureReason.Should().Be("prepare failed");
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRequirePreparedRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be prepared before publish*");
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectDuplicateRevision()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task HandleRetireRevisionAsync_ShouldPersistRetiredState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await agent.HandleRetireRevisionAsync(new RetireServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Retired);
        agent.State.Revisions["r1"].RetiredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandlePrepareRevisionAsync_ShouldRejectMissingAdapter()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = GAgentServiceTestKit.CreateStatefulAgent<ServiceRevisionCatalogGAgent, ServiceRevisionCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.RevisionCatalog(identity),
            () => new ServiceRevisionCatalogGAgent(
                [],
                new InMemoryServiceRevisionArtifactStore(),
                new PreparedServiceRevisionArtifactAssembler()));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No service implementation adapter*");
        agent.State.Revisions["r1"].Status.Should().Be(ServiceRevisionStatus.Created);
    }

    [Fact]
    public async Task HandlePublishRevisionAsync_ShouldRejectMissingRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandlePublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = string.Empty,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision_id is required*");
    }

    [Fact]
    public async Task HandleCreateRevisionAsync_ShouldRejectMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var otherIdentity = GAgentServiceTestKit.CreateIdentity("svc-other");
        var agent = CreateAgent(
            new InMemoryEventStore(),
            new InMemoryServiceRevisionArtifactStore(),
            new RecordingAdapter(_ => Task.FromResult(GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1"))),
            ServiceActorIds.RevisionCatalog(identity));

        await agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        var act = () => agent.HandleCreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(otherIdentity, "r2"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    private static ServiceRevisionCatalogGAgent CreateAgent(
        InMemoryEventStore eventStore,
        InMemoryServiceRevisionArtifactStore artifactStore,
        IServiceImplementationAdapter adapter,
        string actorId)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceRevisionCatalogGAgent, ServiceRevisionCatalogState>(
            eventStore,
            actorId,
            () => new ServiceRevisionCatalogGAgent(
                [adapter],
                artifactStore,
                new PreparedServiceRevisionArtifactAssembler()));
    }

    private sealed class RecordingAdapter : IServiceImplementationAdapter
    {
        private readonly Func<PrepareServiceRevisionRequest, Task<PreparedServiceRevisionArtifact>> _prepare;

        public RecordingAdapter(Func<PrepareServiceRevisionRequest, Task<PreparedServiceRevisionArtifact>> prepare)
        {
            _prepare = prepare;
        }

        public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Static;

        public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
            PrepareServiceRevisionRequest request,
            CancellationToken ct = default) =>
            _prepare(request);
    }
}
