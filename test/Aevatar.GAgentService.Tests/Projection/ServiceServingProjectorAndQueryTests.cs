using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceServingProjectorAndQueryTests
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
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-deployments",
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
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-deployments",
        };
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task ServingSetProjectorAndQueryReader_ShouldProjectAndSortTargets()
    {
        var store = new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id);
        var projector = new ServiceServingSetProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceServingSetQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceServingSetProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-serving",
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
        var projector = new ServiceServingSetProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceServingSetQueryReader(store);
        var context = new ServiceServingSetProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-serving",
        };
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
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
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
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task RolloutProjector_ShouldCreateReadModelAndStamp_WhenCommittedStageAdvanceArrivesFirst()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-15T09:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutStageAdvancedEvent
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-committed",
                    StageIndex = 2,
                    StageId = "stage-2",
                    Targets =
                    {
                        CreateTarget("dep-2", "rev-2", "actor-2", 100, "run"),
                    },
                    OccurredAt = Timestamp.FromDateTimeOffset(observedAt),
                },
                eventId: "evt-rollout-stage",
                stateVersion: 17,
                observedAt: observedAt));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.RolloutId.Should().Be("rollout-committed");
        readModel.CurrentStageIndex.Should().Be(2);
        readModel.Stages.Should().ContainSingle(x => x.StageIndex == 2 && x.StageId == "stage-2");
        readModel.ActorId.Should().Be("tenant:app:default:svc");
        readModel.StateVersion.Should().Be(17);
        readModel.LastEventId.Should().Be("evt-rollout-stage");
        readModel.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task RolloutProjector_ShouldIgnoreEvents_WhenIdentityIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ServiceRolloutFailedEvent
            {
                RolloutId = "rollout-no-identity",
                FailureReason = "boom",
            }));
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-missing-data",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-missing-data",
                        Version = 1,
                    },
                }),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RolloutCommandObservationProjectorAndQueryReader_ShouldProjectObservedOutcome()
    {
        var store = new RecordingDocumentStore<ServiceRolloutCommandObservationReadModel>(x => x.Id);
        var projector = new ServiceRolloutCommandObservationProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceRolloutCommandObservationQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        var observedAt = DateTimeOffset.Parse("2026-03-15T08:00:00+00:00");

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutCommandObservedEvent
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-a",
                    CommandId = "cmd-rollout-pause",
                    CorrelationId = "corr-rollout-pause",
                    Status = ServiceRolloutStatus.Paused,
                    WasNoOp = true,
                    ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                },
                eventId: "evt-rollout-observed",
                stateVersion: 9,
                observedAt: observedAt));

        var snapshot = await reader.GetAsync("cmd-rollout-pause");

        snapshot.Should().NotBeNull();
        snapshot!.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        snapshot.RolloutId.Should().Be("rollout-a");
        snapshot.CorrelationId.Should().Be("corr-rollout-pause");
        snapshot.Status.Should().Be(ServiceRolloutStatus.Paused);
        snapshot.WasNoOp.Should().BeTrue();
        snapshot.StateVersion.Should().Be(9);
        snapshot.ObservedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task RolloutProjector_ShouldAdvanceVersionWithoutChangingStatus_WhenObservationArrives()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(
            store,
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        var startedAt = DateTimeOffset.Parse("2026-03-15T01:00:00+00:00");
        var observedAt = DateTimeOffset.Parse("2026-03-15T02:00:00+00:00");

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutStartedEvent
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
                                StageId = "stage-a",
                                Targets = { CreateTarget("dep-a", "r1", "actor-a", 100, "run") },
                            },
                        },
                    },
                    StartedAt = Timestamp.FromDateTimeOffset(startedAt),
                },
                eventId: "evt-rollout-start",
                stateVersion: 3,
                observedAt: startedAt));
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutCommandObservedEvent
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-a",
                    CommandId = "cmd-rollout-pause",
                    CorrelationId = "corr-rollout-pause",
                    Status = ServiceRolloutStatus.InProgress,
                    WasNoOp = true,
                    ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                },
                eventId: "evt-rollout-observed",
                stateVersion: 5,
                observedAt: observedAt));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));

        readModel.Should().NotBeNull();
        readModel!.Status.Should().Be(ServiceRolloutStatus.InProgress.ToString());
        readModel.StateVersion.Should().Be(5);
        readModel.LastEventId.Should().Be("evt-rollout-observed");
        readModel.UpdatedAt.Should().Be(startedAt);
    }

    [Fact]
    public async Task TrafficViewProjectorAndQueryReader_ShouldGroupEndpointsAndSortTargets()
    {
        var store = new RecordingDocumentStore<ServiceTrafficViewReadModel>(x => x.Id);
        var projector = new ServiceTrafficViewProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceTrafficViewQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceTrafficViewProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-traffic",
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
        var projector = new ServiceTrafficViewProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceTrafficViewQueryReader(store);
        var context = new ServiceTrafficViewProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-traffic",
        };
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : IMessage =>
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
        where T : IMessage =>
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
