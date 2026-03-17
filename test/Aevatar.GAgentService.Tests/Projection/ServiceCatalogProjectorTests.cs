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
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
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
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new StringValue { Value = "noop" }));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldApplyDefinitionMutations_ForExistingReadModel()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var updatedSpec = GAgentServiceTestKit.CreateDefinitionSpec(
            identity,
            GAgentServiceTestKit.CreateEndpointSpec(endpointId: "chat", kind: ServiceEndpointKind.Chat));
        updatedSpec.DisplayName = "Updated Service";
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
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
    public async Task ProjectAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldCreateReadModel_WhenDefaultServingChangesBeforeDefinitionProjection()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
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
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
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

    [Fact]
    public async Task ProjectAsync_ShouldStampReadModel_WhenUsingCommittedEnvelope()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-14T09:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceDefinitionCreatedEvent
                {
                    Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
                },
                eventId: "evt-definition-created",
                stateVersion: 11,
                observedAt: observedAt));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.ActorId.Should().Be("tenant:app:default:svc");
        readModel.StateVersion.Should().Be(11);
        readModel.LastEventId.Should().Be("evt-definition-created");
        readModel.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreCommittedEnvelope_WhenEventDataIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-14T09:05:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing",
                        Version = 4,
                    },
                }),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : Google.Protobuf.IMessage =>
        BuildCommittedEnvelope(
            evt,
            Guid.NewGuid().ToString("N"),
            1,
            DateTimeOffset.UtcNow);

    private static EventEnvelope BuildCommittedEnvelope<T>(
        T evt,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(evt),
                },
            }),
        };
}
