using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
        var projector = new ServiceDeploymentCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceDeploymentCatalogQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceDeploymentCatalogProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-deployments",
        };
        var state = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };

        state.Deployments["dep-b"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-b",
            Status = ServiceDeploymentStatus.Active,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T01:00:00+00:00")),
        };
        state.Deployments["dep-a"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-a",
            RevisionId = "r1",
            PrimaryActorId = "actor-a",
            ArtifactHash = "HASH-A",
            Status = ServiceDeploymentStatus.Deactivated,
            ActivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T02:00:00+00:00")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T03:00:00+00:00")),
        };
        state.ActivationFailures["r-failed"] = new ServiceDeploymentActivationFailureRecord
        {
            RevisionId = "r-failed",
            FailureCode = ServiceDeploymentActivationFailureCode.PreparedArtifactMissing,
            FailureReason = "projection deadline exceeded",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T02:30:00+00:00")),
            ActivationAttemptId = "attempt-projection",
        };
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceDeploymentDeactivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-a",
                    RevisionId = "r1",
                    DeactivatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T03:00:00+00:00")),
                },
                state,
                eventId: "evt-deployment-state",
                stateVersion: 4,
                observedAt: DateTimeOffset.Parse("2026-03-15T03:00:00+00:00")));
        await projector.ProjectAsync(context, BuildEnvelope(new StringValue { Value = "noop" }));
        await projector.ProjectAsync(context, CreateEnvelopeWithoutPayload());

        var snapshot = await reader.GetAsync(identity);

        snapshot.Should().NotBeNull();
        snapshot!.Deployments.Select(x => x.DeploymentId).Should().Equal("dep-a", "dep-b");
        snapshot.Deployments[0].Status.Should().Be(ServiceDeploymentStatus.Deactivated.ToString());
        snapshot.Deployments[0].RevisionId.Should().Be("r1");
        snapshot.Deployments[0].ArtifactHash.Should().Be("HASH-A");
        snapshot.Deployments[1].Status.Should().Be(ServiceDeploymentStatus.Active.ToString());
        snapshot.Deployments[1].RevisionId.Should().BeEmpty();
        snapshot.ActivationFailures.Should().ContainSingle();
        snapshot.ActivationFailures[0].RevisionId.Should().Be("r-failed");
        snapshot.ActivationFailures[0].FailureCode
            .Should().Be(ServiceDeploymentActivationFailureCode.PreparedArtifactMissing);
        snapshot.ActivationFailures[0].FailureReason.Should().Be("projection deadline exceeded");
        snapshot.ActivationFailures[0].OccurredAt
            .Should().Be(DateTimeOffset.Parse("2026-03-15T02:30:00+00:00"));
        snapshot.ActivationFailures[0].ActivationAttemptId.Should().Be("attempt-projection");
        JsonSerializer.Serialize(snapshot).Should().NotContain("attempt-projection");
    }

    [Fact]
    public async Task DeploymentCatalogProjector_ShouldOverwriteStaleDeployments_FromLatestStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceDeploymentCatalogReadModel>(x => x.Id);
        await store.UpsertAsync(new ServiceDeploymentCatalogReadModel
        {
            Id = "tenant:app:default:svc",
            ActorId = "tenant:app:default:svc",
            StateVersion = 8,
            LastEventId = "evt-stale",
            UpdatedAt = DateTimeOffset.Parse("2026-03-15T00:00:00+00:00"),
            Deployments =
            {
                new ServiceDeploymentReadModel
                {
                    DeploymentId = "dep-stale",
                    RevisionId = "old-revision",
                    PrimaryActorId = "old-actor",
                    Status = ServiceDeploymentStatus.Active.ToString(),
                    UpdatedAt = DateTimeOffset.Parse("2026-03-15T00:01:00+00:00"),
                },
            },
        });
        var projector = new ServiceDeploymentCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var state = new ServiceDeploymentState
        {
            Identity = identity.Clone(),
        };
        state.Deployments["dep-fresh"] = new ServiceDeploymentRecord
        {
            DeploymentId = "dep-fresh",
            RevisionId = "fresh-revision",
            PrimaryActorId = "fresh-actor",
            Status = ServiceDeploymentStatus.Active,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T02:00:00+00:00")),
        };

        await projector.ProjectAsync(
            new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = "tenant:app:default:svc",
                ProjectionKind = "service-deployments",
            },
            BuildCommittedEnvelope(
                new ServiceDeploymentActivatedEvent
                {
                    Identity = identity.Clone(),
                    DeploymentId = "dep-fresh",
                    RevisionId = "fresh-revision",
                    PrimaryActorId = "fresh-actor",
                    Status = ServiceDeploymentStatus.Active,
                },
                state,
                eventId: "evt-fresh",
                stateVersion: 9,
                observedAt: DateTimeOffset.Parse("2026-03-15T02:00:00+00:00")));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));

        readModel.Should().NotBeNull();
        readModel!.StateVersion.Should().Be(9);
        readModel.LastEventId.Should().Be("evt-fresh");
        readModel.Deployments.Select(x => x.DeploymentId).Should().Equal("dep-fresh");
        readModel.Deployments.Should().NotContain(x => x.DeploymentId == "dep-stale");
    }

    [Fact]
    public async Task DeploymentCatalogProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceDeploymentCatalogReadModel>(x => x.Id);
        var projector = new ServiceDeploymentCatalogProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
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
    public async Task ServingSetProjector_ShouldAcceptExactReplay_AndSurfaceConflictingVersion()
    {
        var store = new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var projector = new ServiceServingSetProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceServingSetProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-serving",
        };
        var observedAt = DateTimeOffset.Parse("2026-03-15T08:00:00+00:00");
        var committed = BuildCommittedEnvelope(
            new ServiceServingSetUpdatedEvent
            {
                Identity = identity.Clone(),
                Generation = 10,
                RolloutId = "rollout-a",
                Targets = { CreateTarget("dep-a", "r1", "actor-a", 100, "run") },
            },
            new StringValue { Value = "state" },
            eventId: "evt-serving-10",
            stateVersion: 10,
            observedAt: observedAt);

        await projector.ProjectAsync(context, committed);
        await projector.ProjectAsync(context, committed.Clone());

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceServingSetUpdatedEvent
                {
                    Identity = identity.Clone(),
                    Generation = 9,
                    RolloutId = "rollout-stale",
                    Targets = { CreateTarget("dep-stale", "r0", "actor-stale", 100, "run") },
                },
                new StringValue { Value = "state" },
                eventId: "evt-serving-9",
                stateVersion: 9,
                observedAt: observedAt.AddMinutes(-1)));

        var conflicting = BuildCommittedEnvelope(
            new ServiceServingSetUpdatedEvent
            {
                Identity = identity.Clone(),
                Generation = 10,
                RolloutId = "rollout-conflict",
                Targets = { CreateTarget("dep-b", "r2", "actor-b", 100, "run") },
            },
            new StringValue { Value = "state" },
            eventId: "evt-serving-10",
            stateVersion: 10,
            observedAt: observedAt);

        Func<Task> act = async () => await projector.ProjectAsync(context, conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*serving-set projection*state version 10: Conflict*");
        var snapshot = await store.GetAsync(ServiceKeys.Build(identity));
        snapshot!.LastEventId.Should().Be("evt-serving-10");
        snapshot.ActiveRolloutId.Should().Be("rollout-a");
    }

    [Fact]
    public async Task RolloutProjectorAndQueryReader_ShouldProjectLifecycleAcrossEvents()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var reader = new ServiceRolloutQueryReader(store);
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        var baseline = CreateTarget("dep-base", "r0", "actor-base", 100, "run");
        var rolloutState = new ServiceRolloutExecutionState
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
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
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-z",
                        Targets = { CreateTarget("dep-z", "r9", "actor-z", 100, "run") },
                    },
                },
            },
            Status = ServiceRolloutStatus.Completed,
            CurrentStageIndex = 5,
            FailureReason = "boom",
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T01:00:00+00:00")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T07:00:00+00:00")),
            BaselineTargets = { baseline.Clone() },
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutCompletedEvent
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-a",
                    OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-15T07:00:00+00:00")),
                },
                rolloutState,
                eventId: "evt-rollout-state",
                stateVersion: 8,
                observedAt: DateTimeOffset.Parse("2026-03-15T07:00:00+00:00")));
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
        snapshot.Stages.Select(x => x.StageIndex).Should().Equal(0, 1, 2);
        snapshot.Stages.Last().StageId.Should().Be("stage-z");
    }

    [Fact]
    public async Task RolloutProjector_ShouldRespectCancellation_AndReaderShouldReturnNull()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var reader = new ServiceRolloutQueryReader(store);
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        (await reader.GetAsync(GAgentServiceTestKit.CreateIdentity())).Should().BeNull();
    }

    [Fact]
    public async Task RolloutProjector_ShouldCreateReadModelAndStamp_FromCommittedStateRoot()
    {
        var observedAt = DateTimeOffset.Parse("2026-03-15T09:00:00+00:00");
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
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
                new ServiceRolloutExecutionState
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-committed",
                    Plan = new ServiceRolloutPlanSpec
                    {
                        RolloutId = "rollout-committed",
                        Stages =
                        {
                            new ServiceRolloutStageSpec
                            {
                                StageId = "stage-2",
                                Targets =
                                {
                                    CreateTarget("dep-2", "rev-2", "actor-2", 100, "run"),
                                },
                            },
                        },
                    },
                    Status = ServiceRolloutStatus.InProgress,
                    CurrentStageIndex = 2,
                },
                eventId: "evt-rollout-stage",
                stateVersion: 17,
                observedAt: observedAt));

        var readModel = await store.GetAsync("tenant:app:default:svc");
        readModel.Should().NotBeNull();
        readModel!.RolloutId.Should().Be("rollout-committed");
        readModel.CurrentStageIndex.Should().Be(2);
        readModel.Stages.Should().ContainSingle(x => x.StageIndex == 0 && x.StageId == "stage-2");
        readModel.ActorId.Should().Be("tenant:app:default:svc");
        readModel.StateVersion.Should().Be(17);
        readModel.LastEventId.Should().Be("evt-rollout-stage");
        readModel.UpdatedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task RolloutProjector_ShouldOverwriteStaleStagesAndStatus_FromLatestStateRoot()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        await UpsertStaleRolloutReadModelAsync(store);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var observedAt = DateTimeOffset.Parse("2026-03-15T10:00:00+00:00");
        var state = CreateFreshRolloutState(identity, observedAt);

        await projector.ProjectAsync(
            new ServiceRolloutProjectionContext
            {
                RootActorId = "tenant:app:default:svc",
                ProjectionKind = "service-rollout",
            },
            BuildCommittedEnvelope(
                new ServiceRolloutStageAdvancedEvent
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-fresh",
                    StageId = "stage-fresh",
                    StageIndex = 0,
                    Targets = { CreateTarget("dep-fresh", "fresh-revision", "fresh-actor", 100, "run") },
                    OccurredAt = Timestamp.FromDateTimeOffset(observedAt),
                },
                state,
                eventId: "evt-fresh-rollout",
                stateVersion: 12,
                observedAt: observedAt));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));

        readModel.Should().NotBeNull();
        readModel!.RolloutId.Should().Be("rollout-fresh");
        readModel.Status.Should().Be(ServiceRolloutStatus.InProgress.ToString());
        readModel.CurrentStageIndex.Should().Be(0);
        readModel.FailureReason.Should().BeEmpty();
        readModel.StateVersion.Should().Be(12);
        readModel.Stages.Select(x => x.StageId).Should().Equal("stage-fresh");
        readModel.Stages.Should().NotContain(x => x.StageId == "stage-stale");
        readModel.Stages.Single().Targets.Select(x => x.DeploymentId).Should().Equal("dep-fresh");
    }

    [Fact]
    public async Task RolloutProjector_ShouldIgnoreEvents_WhenIdentityIsMissing()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutFailedEvent
                {
                    RolloutId = "rollout-no-identity",
                    FailureReason = "boom",
                },
                new ServiceRolloutExecutionState
                {
                    RolloutId = "rollout-no-identity",
                },
                eventId: "evt-no-identity",
                stateVersion: 1,
                observedAt: DateTimeOffset.UtcNow));
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
    public async Task RolloutProjector_ShouldIgnoreStateRoot_WhenCommittedVersionIsNotPositive()
    {
        var store = new RecordingDocumentStore<ServiceRolloutReadModel>(x => x.Id);
        var projector = new ServiceRolloutProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var identity = GAgentServiceTestKit.CreateIdentity();

        await projector.ProjectAsync(
            new ServiceRolloutProjectionContext
            {
                RootActorId = "tenant:app:default:svc",
                ProjectionKind = "service-rollout",
            },
            BuildCommittedEnvelope(
                new ServiceRolloutStartedEvent
                {
                    Identity = identity.Clone(),
                    Plan = new ServiceRolloutPlanSpec
                    {
                        RolloutId = "rollout-zero-version",
                    },
                },
                new ServiceRolloutExecutionState
                {
                    Identity = identity.Clone(),
                    RolloutId = "rollout-zero-version",
                    Plan = new ServiceRolloutPlanSpec
                    {
                        RolloutId = "rollout-zero-version",
                    },
                },
                eventId: "evt-zero-version",
                stateVersion: 0,
                observedAt: DateTimeOffset.Parse("2026-03-15T11:00:00+00:00")));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public void ServiceArtifactProjectors_ShouldDependOnlyOnWriteDispatcherAndClock()
    {
        AssertStateRootProjectorConstructor<ServiceCatalogProjector, ServiceCatalogReadModel>();
        AssertStateRootProjectorConstructor<ServiceDeploymentCatalogProjector, ServiceDeploymentCatalogReadModel>();
        AssertStateRootProjectorConstructor<ServiceRolloutProjector, ServiceRolloutReadModel>();
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
                new StringValue { Value = "observation-projector-does-not-read-state-root" },
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
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-15T00:00:00+00:00")));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceRolloutProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-rollout",
        };
        var startedAt = DateTimeOffset.Parse("2026-03-15T01:00:00+00:00");
        var observedAt = DateTimeOffset.Parse("2026-03-15T02:00:00+00:00");

        var state = new ServiceRolloutExecutionState
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
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
            Status = ServiceRolloutStatus.InProgress,
            CurrentStageIndex = -1,
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(startedAt),
        };
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceRolloutStartedEvent
                {
                    Identity = identity.Clone(),
                    Plan = state.Plan.Clone(),
                    StartedAt = Timestamp.FromDateTimeOffset(startedAt),
                },
                state,
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
                state,
                eventId: "evt-rollout-observed",
                stateVersion: 5,
                observedAt: observedAt));

        var readModel = await store.GetAsync(ServiceKeys.Build(identity));

        readModel.Should().NotBeNull();
        readModel!.Status.Should().Be(ServiceRolloutStatus.InProgress.ToString());
        readModel.StateVersion.Should().Be(5);
        readModel.LastEventId.Should().Be("evt-rollout-observed");
        readModel.UpdatedAt.Should().Be(observedAt);
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

    [Fact]
    public async Task TrafficViewProjector_ShouldAcceptExactReplay_AndSurfaceConflictingVersion()
    {
        var store = new RecordingDocumentStore<ServiceTrafficViewReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var projector = new ServiceTrafficViewProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var identity = GAgentServiceTestKit.CreateIdentity();
        var context = new ServiceTrafficViewProjectionContext
        {
            RootActorId = "tenant:app:default:svc",
            ProjectionKind = "service-traffic",
        };
        var observedAt = DateTimeOffset.Parse("2026-03-15T08:00:00+00:00");
        var committed = BuildCommittedEnvelope(
            new ServiceServingSetUpdatedEvent
            {
                Identity = identity.Clone(),
                Generation = 10,
                RolloutId = "rollout-a",
                Targets = { CreateTarget("dep-a", "r1", "actor-a", 100, "run") },
            },
            new StringValue { Value = "state" },
            eventId: "evt-serving-10",
            stateVersion: 10,
            observedAt: observedAt);

        await projector.ProjectAsync(context, committed);
        await projector.ProjectAsync(context, committed.Clone());

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                new ServiceServingSetUpdatedEvent
                {
                    Identity = identity.Clone(),
                    Generation = 9,
                    RolloutId = "rollout-stale",
                    Targets = { CreateTarget("dep-stale", "r0", "actor-stale", 100, "run") },
                },
                new StringValue { Value = "state" },
                eventId: "evt-serving-9",
                stateVersion: 9,
                observedAt: observedAt.AddMinutes(-1)));

        var conflicting = BuildCommittedEnvelope(
            new ServiceServingSetUpdatedEvent
            {
                Identity = identity.Clone(),
                Generation = 10,
                RolloutId = "rollout-conflict",
                Targets = { CreateTarget("dep-b", "r2", "actor-b", 100, "run") },
            },
            new StringValue { Value = "state" },
            eventId: "evt-serving-10",
            stateVersion: 10,
            observedAt: observedAt);

        Func<Task> act = async () => await projector.ProjectAsync(context, conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*traffic-view projection*state version 10: Conflict*");
        var snapshot = await store.GetAsync(ServiceKeys.Build(identity));
        snapshot!.LastEventId.Should().Be("evt-serving-10");
        snapshot.ActiveRolloutId.Should().Be("rollout-a");
    }

    private static EventEnvelope BuildEnvelope<T>(T evt)
        where T : IMessage =>
        BuildCommittedEnvelope(
            evt,
            new StringValue { Value = "not-target-state-root" },
            Guid.NewGuid().ToString("N"),
            1,
            DateTimeOffset.UtcNow);

    private static EventEnvelope BuildCommittedEnvelope<TEvent, TState>(
        TEvent evt,
        TState state,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt)
        where TEvent : IMessage
        where TState : IMessage =>
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

    private static Task UpsertStaleRolloutReadModelAsync(
        RecordingDocumentStore<ServiceRolloutReadModel> store) =>
        store.UpsertAsync(new ServiceRolloutReadModel
        {
            Id = "tenant:app:default:svc",
            ActorId = "tenant:app:default:svc",
            StateVersion = 11,
            LastEventId = "evt-stale",
            RolloutId = "rollout-stale",
            Status = ServiceRolloutStatus.Paused.ToString(),
            CurrentStageIndex = 99,
            FailureReason = "stale failure",
            UpdatedAt = DateTimeOffset.Parse("2026-03-15T00:00:00+00:00"),
            Stages =
            {
                new ServiceRolloutStageReadModel
                {
                    StageId = "stage-stale",
                    StageIndex = 99,
                    Targets =
                    {
                        new ServiceServingTargetReadModel
                        {
                            DeploymentId = "dep-stale",
                            RevisionId = "old-revision",
                            PrimaryActorId = "old-actor",
                            AllocationWeight = 100,
                            ServingState = ServiceServingState.Active.ToString(),
                            EnabledEndpointIds = { "run" },
                        },
                    },
                },
            },
        });

    private static ServiceRolloutExecutionState CreateFreshRolloutState(
        ServiceIdentity identity,
        DateTimeOffset observedAt) =>
        new()
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-fresh",
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-fresh",
                DisplayName = "Fresh rollout",
                Stages =
                {
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-fresh",
                        Targets =
                        {
                            CreateTarget("dep-fresh", "fresh-revision", "fresh-actor", 100, "run"),
                        },
                    },
                },
            },
            Status = ServiceRolloutStatus.InProgress,
            CurrentStageIndex = 0,
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
        };

    private static void AssertStateRootProjectorConstructor<TProjector, TReadModel>()
        where TReadModel : class, IProjectionReadModel
    {
        var constructor = typeof(TProjector).GetConstructors().Should().ContainSingle().Subject;

        constructor.GetParameters()
            .Select(x => x.ParameterType)
            .Should()
            .Equal(typeof(IProjectionWriteDispatcher<TReadModel>), typeof(IProjectionClock));
    }
}
