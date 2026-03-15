using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServicePhase3ProjectorAndQueryTests
{
    [Fact]
    public async Task DeploymentCatalogProjectorAndQueryReader_ShouldProjectLifecycleAndSortDeployments()
    {
        var store = new RecordingDocumentStore<ServiceDeploymentCatalogReadModel>(x => x.Id);
        var projector = new ServiceDeploymentCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceDeploymentCatalogQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceDeploymentCatalogProjectionContext
        {
            ProjectionId = "service-deployments:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceDeploymentHealthChangedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-b",
            Status = ServiceDeploymentStatus.Active,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T01:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-a",
            RevisionId = "r1",
            PrimaryActorId = "actor-a",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T02:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceDeploymentDeactivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-a",
            RevisionId = "r1",
            DeactivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T03:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, CreateEnvelopeWithoutPayload());

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Deployments.Select(x => x.DeploymentId).Should().Equal("dep-a", "dep-b");
        snapshot.Deployments[0].Status.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        snapshot.Deployments[0].RevisionId.Should().Be("r1");
        snapshot.Deployments[1].Status.Should().Be(ServiceDeploymentStatus.Active.ToString());
        snapshot.Deployments[1].RevisionId.Should().BeEmpty();
    }

    [Fact]
    public async Task DeploymentCatalogProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceDeploymentCatalogReadModel>(x => x.Id);
        var projector = new ServiceDeploymentCatalogProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceDeploymentCatalogQueryReader(store);
        var context = new ServiceDeploymentCatalogProjectionContext
        {
            ProjectionId = "service-deployments:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Invoking(() => projector.InitializeAsync(context, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => projector.CompleteAsync(context, [], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task ServingSetProjectorAndQueryReader_ShouldProjectAndSortTargets()
    {
        var store = new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id);
        var projector = new ServiceServingSetProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceServingSetQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceServingSetProjectionContext
        {
            ProjectionId = "service-serving:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceServingSetUpdatedEvent
        {
            Identity = identity.Clone(),
            Generation = 2,
            RolloutId = "rollout-a",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T04:00:00+00:00")),
            Targets =
            {
                CreateTarget("dep-b", "r2", "actor-b", 20, "chat", "run"),
                CreateTarget("dep-a", "r1", "actor-a", 80, "run"),
            },
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, CreateEnvelopeWithoutPayload());

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Generation.Should().Be(2);
        snapshot.ActiveRolloutId.Should().Be("rollout-a");
        snapshot.Targets.Select(x => x.DeploymentId).Should().Equal("dep-a", "dep-b");
        snapshot.Targets[0].EnabledEndpointIds.Should().Equal("run");
        snapshot.Targets[1].EnabledEndpointIds.Should().Equal("chat", "run");
    }

    [Fact]
    public async Task ServingSetProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id);
        var projector = new ServiceServingSetProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceServingSetQueryReader(store);
        var context = new ServiceServingSetProjectionContext
        {
            ProjectionId = "service-serving:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Invoking(() => projector.InitializeAsync(context, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => projector.CompleteAsync(context, [], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task RolloutProjectorAndQueryReader_ShouldProjectLifecycleAcrossEvents()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceRolloutQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            ProjectionId = "service-rollout:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        var baseline = CreateTarget("dep-base", "r0", "actor-base", 100, "run");

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutStartedEvent
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-a",
                DisplayName = "Primary rollout",
                Stages =
                {
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-b",
                        Targets = { CreateTarget("dep-b", "r2", "actor-b", 40, "chat") },
                    },
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-a",
                        Targets = { CreateTarget("dep-a", "r1", "actor-a", 60, "run") },
                    },
                },
            },
            BaselineTargets = { baseline.Clone() },
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T01:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutStageAdvancedEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            StageIndex = 5,
            StageId = "stage-z",
            Targets = { CreateTarget("dep-z", "r9", "actor-z", 100, "run") },
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T02:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutPausedEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            Reason = "pause",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T03:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutResumedEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T04:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutRolledBackEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            Targets = { baseline.Clone() },
            Reason = "rollback",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T05:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutFailedEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            FailureReason = "boom",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T06:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new ServiceRolloutCompletedEvent
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T07:00:00+00:00")),
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, CreateEnvelopeWithoutPayload());

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.RolloutId.Should().Be("rollout-a");
        snapshot.DisplayName.Should().Be("Primary rollout");
        snapshot.Status.Should().Be(ServiceRolloutStatus.Completed.ToString());
        snapshot.CurrentStageIndex.Should().Be(5);
        snapshot.FailureReason.Should().Be("boom");
        snapshot.BaselineTargets.Select(x => x.DeploymentId).Should().Equal("dep-base");
        snapshot.Stages.Select(x => x.StageIndex).Should().Equal(0, 1, 5);
        snapshot.Stages.Last().StageId.Should().Be("stage-z");
    }

    [Fact]
    public async Task RolloutProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceRolloutQueryReader(store);
        var context = new ServiceRolloutProjectionContext
        {
            ProjectionId = "service-rollout:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Invoking(() => projector.InitializeAsync(context, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => projector.CompleteAsync(context, [], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task TrafficViewProjectorAndQueryReader_ShouldGroupEndpointsAndSortTargets()
    {
        var store = new RecordingDocumentStore<ServiceTrafficViewReadModel>(x => x.Id);
        var projector = new ServiceTrafficViewProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceTrafficViewQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceTrafficViewProjectionContext
        {
            ProjectionId = "service-traffic:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };

        await projector.ProjectAsync(context, BuildEnvelope(new ServiceServingSetUpdatedEvent
        {
            Identity = identity.Clone(),
            Generation = 9,
            RolloutId = "rollout-a",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T08:00:00+00:00")),
            Targets =
            {
                CreateTarget("dep-b", "r2", "actor-b", 20, "run", "", "chat"),
                CreateTarget("dep-a", "r1", "actor-a", 80, "run"),
            },
        }));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, CreateEnvelopeWithoutPayload());

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Generation.Should().Be(9);
        snapshot.Endpoints.Select(x => x.EndpointId).Should().Equal("chat", "run");
        snapshot.Endpoints.Single(x => x.EndpointId == "run").Targets.Select(x => x.DeploymentId).Should().Equal("dep-a", "dep-b");
        snapshot.Endpoints.Single(x => x.EndpointId == "chat").Targets.Select(x => x.DeploymentId).Should().Equal("dep-b");
    }

    [Fact]
    public async Task TrafficViewProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceTrafficViewReadModel>(x => x.Id);
        var projector = new ServiceTrafficViewProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceTrafficViewQueryReader(store);
        var context = new ServiceTrafficViewProjectionContext
        {
            ProjectionId = "service-traffic:tenant:app:default:svc",
            RootActorId = "tenant:app:default:svc",
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Invoking(() => projector.InitializeAsync(context, cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => projector.CompleteAsync(context, [], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
        };

    private static EventEnvelope CreateEnvelopeWithoutPayload() =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static ServiceServingTargetSpec CreateTarget(
        string deploymentId,
        string revisionId,
        string actorId,
        int allocationWeight,
        params string[] enabledEndpointIds)
    {
        return new ServiceServingTargetSpec
        {
            DeploymentId = deploymentId,
            RevisionId = revisionId,
            PrimaryActorId = actorId,
            AllocationWeight = allocationWeight,
            ServingState = ServiceServingState.Active,
            EnabledEndpointIds = { enabledEndpointIds },
        };
    }
}
