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
            ProjectionId = "service-revisions:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)),
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r1",
                ImplementationKind = ServiceImplementationKind.Static,
                ArtifactHash = "hash-1",
                PreparedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                Endpoints = { GAgentServiceTestKit.CreateEndpointDescriptor() },
            }));

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
            ProjectionId = "service-revisions:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparationFailedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                FailureReason = "boom",
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPublishedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                PublishedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionRetiredEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                RetiredAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }));

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
            ProjectionId = "service-revisions:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new StringValue { Value = "noop" }));

        (await store.ListAsync()).Should().BeEmpty();

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
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRevisionCatalogProjectionContext
        {
            ProjectionId = "service-revisions:tenant:app:default:svc",
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
    public async Task ProjectAsync_ShouldAddEntry_WhenCatalogExistsButRevisionIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var projector = new ServiceRevisionCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRevisionCatalogProjectionContext
        {
            ProjectionId = "service-revisions:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionCreatedEvent
            {
                Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5)),
            }));
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRevisionPreparedEvent
            {
                Identity = identity.Clone(),
                RevisionId = "r2",
                ImplementationKind = ServiceImplementationKind.Static,
                ArtifactHash = "hash-2",
                PreparedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                Endpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat", kind: ServiceEndpointKind.Chat) },
            }));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.Revisions.Select(x => x.RevisionId).Should().Contain(["r1", "r2"]);
        readModel.Revisions.Should().Contain(x => x.RevisionId == "r2" && x.Status == ServiceRevisionStatus.Prepared.ToString());
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
