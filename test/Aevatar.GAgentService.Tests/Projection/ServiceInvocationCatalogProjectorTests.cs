using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceInvocationCatalogProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldCloneAggregateStateUsingCommittedVersion()
    {
        var observedAt = DateTimeOffset.Parse("2026-06-05T01:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceInvocationCatalogReadModel>(x => x.Id);
        var projector = new ServiceInvocationCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-06-05T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceInvocationCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc:invocation-catalog",
            ProjectionKind = "service-invocation-catalog",
        };
        var state = new ServiceInvocationCatalogState
        {
            Identity = identity.Clone(),
            SourceCatalogVersion = 11,
            SourceServingVersion = 12,
            SourceRevisionVersion = 13,
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            Entries =
            {
                new ServiceInvocationCatalogEntryState
                {
                    EndpointId = "chat",
                    ReadinessStatus = ServiceInvokeReadinessStatus.Ready,
                    UnavailableReason = ServiceInvokeUnavailableReason.Unspecified,
                    SelectedRevisionId = "r1",
                    SelectedDeploymentId = "dep-1",
                    SelectedActorId = "actor-1",
                },
            },
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceInvocationCatalogObservedEvent
                {
                    Identity = identity.Clone(),
                },
                state,
                eventId: "evt-invocation-observed",
                stateVersion: 42,
                observedAt: observedAt));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));
        readModel.Should().NotBeNull();
        readModel!.ActorId.Should().Be(context.RootActorId);
        readModel.StateVersion.Should().Be(42);
        readModel.LastEventId.Should().Be("evt-invocation-observed");
        readModel.ObservedAt.Should().Be(observedAt);
        readModel.SourceCatalogVersion.Should().Be(11);
        readModel.SourceServingVersion.Should().Be(12);
        readModel.SourceRevisionVersion.Should().Be(13);
        readModel.Entries.Should().ContainSingle();
        readModel.Entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready);
    }

    [Fact]
    public async Task ProjectAsync_ShouldOverwriteIdempotently_WithoutLocalVersionCounter()
    {
        var store = new RecordingDocumentStore<ServiceInvocationCatalogReadModel>(x => x.Id);
        var projector = new ServiceInvocationCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceInvocationCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc:invocation-catalog",
            ProjectionKind = "service-invocation-catalog",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceInvocationCatalogObservedEvent { Identity = identity.Clone() },
                State(identity, ServiceInvokeReadinessStatus.Unavailable, ServiceInvokeUnavailableReason.PreparedArtifactMissing),
                "evt-1",
                5,
                DateTimeOffset.Parse("2026-06-05T01:00:00+00:00")));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceInvocationCatalogObservedEvent { Identity = identity.Clone() },
                State(identity, ServiceInvokeReadinessStatus.Ready, ServiceInvokeUnavailableReason.Unspecified),
                "evt-2",
                9,
                DateTimeOffset.Parse("2026-06-05T01:05:00+00:00")));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));
        readModel.Should().NotBeNull();
        readModel!.StateVersion.Should().Be(9);
        readModel.LastEventId.Should().Be("evt-2");
        readModel.Entries.Should().ContainSingle();
        readModel.Entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready);
        (await store.ReadItemsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task QueryReader_ShouldReturnSnapshotFromReadModel()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var store = new RecordingDocumentStore<ServiceInvocationCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceInvocationCatalogReadModel
        {
            Id = ServiceKeys.Build(identity),
            ServiceKey = ServiceKeys.Build(identity),
            TenantId = identity.TenantId,
            AppId = identity.AppId,
            Namespace = identity.Namespace,
            ServiceId = identity.ServiceId,
            StateVersion = 7,
            LastEventId = "evt-7",
            ObservedAt = DateTimeOffset.Parse("2026-06-05T02:00:00+00:00"),
            SourceCatalogVersion = 1,
            SourceServingVersion = 2,
            SourceRevisionVersion = 3,
            Entries =
            {
                new ServiceInvocationReadinessEntryReadModel
                {
                    ServiceKey = ServiceKeys.Build(identity),
                    EndpointId = "chat",
                    ReadinessStatus = ServiceInvokeReadinessStatus.Unavailable,
                    UnavailableReason = ServiceInvokeUnavailableReason.RevisionNotPrepared,
                    SelectedRevisionId = "r1",
                    SelectedDeploymentId = "dep-1",
                    SelectedActorId = "actor-1",
                },
            },
        });
        var reader = new ServiceInvocationCatalogQueryReader(store);

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.AggregateStateVersion.Should().Be(7);
        snapshot.Entries.Should().ContainSingle();
        snapshot.Entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.RevisionNotPrepared);
    }

    private static ServiceInvocationCatalogState State(
        ServiceIdentity identity,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason) =>
        new()
        {
            Identity = identity.Clone(),
            Entries =
            {
                new ServiceInvocationCatalogEntryState
                {
                    EndpointId = "chat",
                    ReadinessStatus = status,
                    UnavailableReason = reason,
                    SelectedRevisionId = status == ServiceInvokeReadinessStatus.Ready ? "r1" : string.Empty,
                    SelectedDeploymentId = status == ServiceInvokeReadinessStatus.Ready ? "dep-1" : string.Empty,
                    SelectedActorId = status == ServiceInvokeReadinessStatus.Ready ? "actor-1" : string.Empty,
                },
            },
        };

    private static EventEnvelope BuildCommittedEnvelope<T>(
        T evt,
        ServiceInvocationCatalogState state,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt)
        where T : Google.Protobuf.IMessage =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
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
