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
    public async Task ProjectAsync_ShouldOverwriteDefinitionOnly_FromCommittedStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var state = new ServiceDefinitionState
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
            DefaultServingRevisionId = "r-default",
        };
        state.Spec.ExternalExposure = new ExternalExposure
        {
            NyxidSlug = "aevatar-orders",
            RegisteredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-11T01:02:03+00:00")),
        };
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceDefinitionCreatedEvent { Spec = state.Spec.Clone() },
                state,
                eventId: "evt-definition-created",
                stateVersion: 3,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DisplayName.Should().Be("Service");
        readModel.DefaultServingRevisionId.Should().Be("r-default");
        readModel.Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        readModel.ExternalExposure.Should().NotBeNull();
        readModel.ExternalExposure.NyxidSlug.Should().Be("aevatar-orders");
        readModel.ExternalExposure.RegisteredAt.Should().Be(DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializePendingExposureWithoutSlugOrRegisteredAt()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var state = new ServiceDefinitionState
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        };
        state.Spec.ExternalExposure = new ExternalExposure
        {
            Status = ServiceRegistrationStatus.Pending,
            DesiredSpecHash = "hash-pending",
            Attempt = 1,
            ExposureDesired = true,
        };

        await projector.ProjectAsync(
            new ServiceCatalogProjectionContext
            {
                RootActorId = "tenant:app:default:svc",
                ProjectionKind = "service-catalog",
            },
            BuildCommittedEnvelope(
                new ServiceRegistrationRequestedEvent
                {
                    Identity = identity.Clone(),
                    DesiredSpecHash = "hash-pending",
                    Attempt = 1,
                },
                state,
                eventId: "evt-registration-requested",
                stateVersion: 4,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.ExternalExposure.Should().NotBeNull();
        readModel.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Pending);
        readModel.ExternalExposure.DesiredSpecHash.Should().Be("hash-pending");
        readModel.ExternalExposure.ExposureDesired.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnrelatedPayload()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
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
    public async Task ProjectAsync_ShouldOverwriteDefinition_FromLatestStateRoot()
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
                new ServiceDefinitionState
                {
                    Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
                },
                eventId: "evt-created",
                stateVersion: 1,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceDefinitionUpdatedEvent
                {
                    Spec = updatedSpec,
                },
                new ServiceDefinitionState
                {
                    Spec = updatedSpec.Clone(),
                    DefaultServingRevisionId = "r2",
                },
                eventId: "evt-updated",
                stateVersion: 2,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:01:00+00:00")));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DisplayName.Should().Be("Updated Service");
        readModel.DefaultServingRevisionId.Should().Be("r2");
        readModel.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat" && x.Kind == ServiceEndpointKind.Chat.ToString());
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
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
    public async Task ProjectAsync_ShouldMaterializeDefaultServingRevision_FromStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var state = new ServiceDefinitionState
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
            DefaultServingRevisionId = "r9",
        };
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new DefaultServingRevisionChangedEvent
                {
                    Identity = identity.Clone(),
                    RevisionId = "r9",
                },
                state,
                eventId: "evt-default",
                stateVersion: 5,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:02:00+00:00")));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.DefaultServingRevisionId.Should().Be("r9");
        readModel.ServiceId.Should().Be("svc");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreDeploymentStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceDeploymentHealthChangedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-1",
                    Status = ServiceDeploymentStatus.Active,
                },
                new ServiceDeploymentState
                {
                    Identity = identity.Clone(),
                },
                eventId: "evt-health",
                stateVersion: 7,
                observedAt: DateTimeOffset.Parse("2026-03-14T00:03:00+00:00")));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldStampReadModel_WhenUsingCommittedEnvelope()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-14T09:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
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
                new ServiceDefinitionState
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
        var projector = new ServiceCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
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
            new StringValue { Value = "not-service-definition-state" },
            Guid.NewGuid().ToString("N"),
            1,
            DateTimeOffset.UtcNow);

    private static EventEnvelope BuildCommittedEnvelope<TEvent, TState>(
        TEvent evt,
        TState state,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt)
        where TEvent : Google.Protobuf.IMessage
        where TState : Google.Protobuf.IMessage =>
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
                StateRoot = Any.Pack(state),
            }),
        };
}
