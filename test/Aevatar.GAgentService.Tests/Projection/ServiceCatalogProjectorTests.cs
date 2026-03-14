using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCatalogProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldUpsertDefinitionThenMutateDeploymentState()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDefinitionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDeploymentActivatedEvent
            {
                Identity = identity.Clone(),
                DeploymentId = "dep-1",
                RevisionId = "r1",
                PrimaryActorId = "actor-1",
                Status = ServiceDeploymentStatus.Active,
            }));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DisplayName.Should().Be("Service");
        readModel.ActiveServingRevisionId.Should().Be("r1");
        readModel.PrimaryActorId.Should().Be("actor-1");
        readModel.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnrelatedPayload()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new StringValue { Value = "noop" }));

        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldApplyDefinitionMutations_ForExistingReadModel()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var updatedSpec = GAgentServiceTestKit.CreateDefinitionSpec(
            identity,
            GAgentServiceTestKit.CreateEndpointSpec(endpointId: "chat", kind: ServiceEndpointKind.Chat));
        updatedSpec.DisplayName = "Updated Service";
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDefinitionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDefinitionUpdatedEvent
            {
                Spec = updatedSpec,
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new DefaultServingRevisionChangedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDeploymentHealthChangedEvent
            {
                Identity = identity.Clone(),
                DeploymentId = "dep-1",
                Status = ServiceDeploymentStatus.Active,
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDeploymentDeactivatedEvent
            {
                Identity = identity.Clone(),
                DeploymentId = "dep-1",
                RevisionId = "r2",
            }));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DisplayName.Should().Be("Updated Service");
        readModel.DefaultServingRevisionId.Should().Be("r2");
        readModel.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        readModel.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat" && x.Kind == ServiceEndpointKind.Chat.ToString());
    }

    [Fact]
    public async Task InitializeAndCompleteAsync_ShouldRespectCancellation()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var initialize = () => projector.InitializeAsync(context, cts.Token).AsTask();
        var complete = () => projector.CompleteAsync(context, [], cts.Token).AsTask();

        await initialize.Should().ThrowAsync<OperationCanceledException>();
        await complete.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCreateReadModel_WhenDefaultServingChangesBeforeDefinitionProjection()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new DefaultServingRevisionChangedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r9",
            }));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DefaultServingRevisionId.Should().Be("r9");
        readModel.ServiceId.Should().Be("svc");
    }

    [Fact]
    public async Task ProjectAsync_ShouldCreateReadModel_WhenHealthEventArrivesFirst()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            ProjectionId = "service-catalog:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceDeploymentHealthChangedEvent
            {
                Identity = identity.Clone(),
                DeploymentId = "dep-1",
                Status = ServiceDeploymentStatus.Active,
            }));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DeploymentStatus.Should().Be(ServiceDeploymentStatus.Active.ToString());
    }

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
        };
}
