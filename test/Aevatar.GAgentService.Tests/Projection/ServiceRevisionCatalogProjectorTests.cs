using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceRevisionCatalogProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldCreateThenPrepareRevisionEntry()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };
        var createdAt = DateTimeOffset.Parse("2026-03-14T01:00:00+00:00");
        var preparedAt = DateTimeOffset.Parse("2026-03-14T02:00:00+00:00");
        var createdState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                ServiceRevisionStatus.Created,
                createdAt: createdAt));
        var preparedState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                ServiceRevisionStatus.Prepared,
                artifactHash: "hash-1",
                createdAt: createdAt,
                preparedAt: preparedAt,
                endpoints: [GAgentServiceTestKit.CreateEndpointDescriptor()]));

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            }, createdState));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Static,
                ArtifactHash = "hash-1",
                PreparedAt = Timestamp.FromDateTimeOffset(preparedAt),
                Endpoints = { GAgentServiceTestKit.CreateEndpointDescriptor() },
            }, preparedState));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.Revisions.Should().ContainSingle();
        readModel.Revisions[0].Status.Should().Be(ServiceRevisionStatus.Prepared.ToString());
        readModel.Revisions[0].ArtifactHash.Should().Be("hash-1");
        readModel.Revisions[0].Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
    }

    [Fact]
    public async Task ProjectAsync_ShouldApplyFailurePublishAndRetireTransitions()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };
        var publishedAt = DateTimeOffset.Parse("2026-03-14T03:00:00+00:00");
        var retiredAt = DateTimeOffset.Parse("2026-03-14T04:00:00+00:00");
        var failedState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r2"),
                ServiceRevisionStatus.PreparationFailed,
                failureReason: "boom"));
        var publishedState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r2"),
                ServiceRevisionStatus.Published,
                failureReason: "boom",
                publishedAt: publishedAt));
        var retiredState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r2"),
                ServiceRevisionStatus.Retired,
                failureReason: "boom",
                publishedAt: publishedAt,
                retiredAt: retiredAt));

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparationFailedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                FailureReason = "boom",
            }, failedState));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPublishedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                PublishedAt = Timestamp.FromDateTimeOffset(publishedAt),
            }, publishedState));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionRetiredEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                RetiredAt = Timestamp.FromDateTimeOffset(retiredAt),
            }, retiredState));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.Revisions.Should().ContainSingle();
        readModel.Revisions[0].FailureReason.Should().Be("boom");
        readModel.Revisions[0].Status.Should().Be(ServiceRevisionStatus.Retired.ToString());
        readModel.Revisions[0].PublishedAt.Should().NotBeNull();
        readModel.Revisions[0].RetiredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnrelatedPayload_AndCancellationHooks()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new StringValue { Value = "noop" }));

        (await store.ReadItemsAsync()).Should().BeEmpty();

    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
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
    public async Task ProjectAsync_ShouldAddEntry_WhenCatalogExistsButRevisionIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };
        var firstState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                ServiceRevisionStatus.Created,
                createdAt: DateTimeOffset.Parse("2026-03-14T01:00:00+00:00")));
        var secondState = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                ServiceRevisionStatus.Created,
                createdAt: DateTimeOffset.Parse("2026-03-14T01:00:00+00:00")),
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r2"),
                ServiceRevisionStatus.Prepared,
                artifactHash: "hash-2",
                preparedAt: DateTimeOffset.Parse("2026-03-14T02:00:00+00:00"),
                endpoints:
                [
                    GAgentServiceTestKit.CreateEndpointDescriptor(
                        endpointId: "chat",
                        kind: ServiceEndpointKind.Chat),
                ]));

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-14T01:00:00+00:00")),
            }, firstState));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                ImplementationKind = ServiceImplementationKind.Static,
                ArtifactHash = "hash-2",
                PreparedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-14T02:00:00+00:00")),
                Endpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat", kind: ServiceEndpointKind.Chat) },
            }, secondState));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.Revisions.Select(x => x.RevisionId).Should().Contain(["r1", "r2"]);
        readModel.Revisions.Should().Contain(x => x.RevisionId == "r2" && x.Status == ServiceRevisionStatus.Prepared.ToString());
    }

    [Fact]
    public async Task ProjectAsync_ShouldStampReadModel_WhenUsingCommittedEnvelope()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-14T12:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };
        var state = CreateRevisionState(
            identity,
            CreateRevisionRecord(
                GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                ServiceRevisionStatus.Created,
                createdAt: observedAt.AddMinutes(-5)));

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRevisionCreatedEvent
                {
                    Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                    CreatedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-5)),
                },
                eventId: "evt-revision-created",
                stateVersion: 13,
                observedAt: observedAt,
                state: state));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.ActorId.Should().Be("tenant:app:default:svc");
        readModel.StateVersion.Should().Be(13);
        readModel.LastEventId.Should().Be("evt-revision-created");
        readModel.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreCommittedEnvelope_WhenEventDataIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRevisionCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-revisions",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing",
                        Version = 2,
                    },
                }),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    private static EventEnvelope BuildEnvelope<T>(T evt, ServiceRevisionCatalogState? state = null)
        where T : Google.Protobuf.IMessage =>
        BuildCommittedEnvelope(
            evt,
            Guid.NewGuid().ToString("N"),
            1,
            DateTimeOffset.UtcNow,
            state);

    private static EventEnvelope BuildCommittedEnvelope<T>(
        T evt,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt,
        ServiceRevisionCatalogState? state = null)
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
                StateRoot = state == null ? null : Any.Pack(state),
            }),
        };

    private static ServiceRevisionCatalogState CreateRevisionState(
        ServiceIdentity identity,
        params ServiceRevisionRecordState[] revisions)
    {
        var state = new ServiceRevisionCatalogState
        {
            Identity = identity.Clone(),
        };
        foreach (var revision in revisions)
        {
            var revisionId = revision.Spec?.RevisionId ?? string.Empty;
            state.Revisions[revisionId] = revision.Clone();
        }

        return state;
    }

    private static ServiceRevisionRecordState CreateRevisionRecord(
        ServiceRevisionSpec spec,
        ServiceRevisionStatus status,
        string artifactHash = "",
        string failureReason = "",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? preparedAt = null,
        DateTimeOffset? publishedAt = null,
        DateTimeOffset? retiredAt = null,
        IReadOnlyList<ServiceEndpointDescriptor>? endpoints = null)
    {
        var record = new ServiceRevisionRecordState
        {
            Spec = spec.Clone(),
            Status = status,
            ArtifactHash = artifactHash,
            FailureReason = failureReason,
        };
        if (createdAt.HasValue)
            record.CreatedAt = Timestamp.FromDateTimeOffset(createdAt.Value);
        if (preparedAt.HasValue)
            record.PreparedAt = Timestamp.FromDateTimeOffset(preparedAt.Value);
        if (publishedAt.HasValue)
            record.PublishedAt = Timestamp.FromDateTimeOffset(publishedAt.Value);
        if (retiredAt.HasValue)
            record.RetiredAt = Timestamp.FromDateTimeOffset(retiredAt.Value);
        if (endpoints != null)
            record.Endpoints.Add(endpoints.Select(x => x.Clone()));
        return record;
    }
}
