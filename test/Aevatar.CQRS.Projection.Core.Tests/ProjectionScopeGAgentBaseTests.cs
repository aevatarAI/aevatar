using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeGAgentBaseTests
{
    [Fact]
    public void ProjectionScopeGAgentBase_ShouldOptIntoEventSourcingVersionDriftRecovery()
    {
        var agent = new TestScopeAgent(_ => ProjectionScopeDispatchResult.Skip());

        agent.Should().BeAssignableTo<IEventSourcingVersionDriftRecoverableActor>();
    }

    [Fact]
    public void PublicationRecoverySnapshotPolicy_ShouldDeferOnlyWhileDurableObservationIsInFlight()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-recovery-snapshot-policy",
            onProcess: _ => ProjectionScopeDispatchResult.Skip(),
            enableDurableObservationRecovery: true);

        agent.ShouldPersistRecoverySnapshotForTest().Should().BeTrue();

        agent.State.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 1,
                EventId = "event-1",
            },
        };

        agent.ShouldPersistRecoverySnapshotForTest().Should().BeTrue(
            "an incomplete recovery coordinate cannot be resumed and must not suppress snapshots");

        agent.State.InFlightObservation.Envelope = new EventEnvelope { Id = "envelope-1" };

        agent.ShouldPersistRecoverySnapshotForTest().Should().BeFalse();

        agent.State.InFlightObservation = null;
        agent.ShouldPersistRecoverySnapshotForTest().Should().BeTrue();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldPropagate_RetryableOptimisticConcurrencyException()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-retry",
            onProcess: _ => throw new EventStoreOptimisticConcurrencyException("root-1", 3, 4));

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-retry");

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(envelope);

        await act.Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldDiscardPendingEvents_WhenOccIsThrown()
    {
        var es = new TrackingEventSourcing();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-occ-discard",
            onProcess: _ => throw new EventStoreOptimisticConcurrencyException("root-1", 3, 4),
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-occ-discard");

        await Assert.ThrowsAsync<EventStoreOptimisticConcurrencyException>(
            () => agent.HandleObservedEnvelopeAsync(envelope));

        es.DiscardCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldDiscardPendingEvents_WhenWrappedOccIsThrown()
    {
        var es = new TrackingEventSourcing();
        var wrappedOcc = new ProjectionDispatchAggregateException(
        [
            new ProjectionDispatchFailure(
                "projector", 1,
                new EventStoreOptimisticConcurrencyException("root-1", 3, 4)),
        ]);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-wrapped-occ",
            onProcess: _ => throw wrappedOcc,
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-wrapped-occ");

        await Assert.ThrowsAsync<ProjectionDispatchAggregateException>(
            () => agent.HandleObservedEnvelopeAsync(envelope));

        es.DiscardCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldNotDiscardPendingEvents_ForNonOccFailure()
    {
        var es = new TrackingEventSourcing();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-non-occ",
            onProcess: _ => throw new InvalidOperationException("not occ"),
            eventSourcing: es);

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-non-occ");

        await agent.HandleObservedEnvelopeAsync(envelope);

        es.DiscardCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldProcessCommittedObservation_WhenVersionIsUnspecified()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-version-zero",
            onProcess: _ =>
            {
                processed++;
                return ProjectionScopeDispatchResult.Success(0, "event-type");
            });
        var envelope = BuildForwardedCommittedObservationEnvelope("projection-scope-version-zero", version: 0);

        await agent.HandleObservedEnvelopeAsync(envelope);

        processed.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldInvokeContextHooksAroundMaterialization()
    {
        var dispatchStages = new List<string>();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-context-hooks",
            onProcess: _ => ProjectionScopeDispatchResult.Success(7, "event-type"),
            dispatchStages: dispatchStages);

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-context-hooks",
            version: 7,
            eventId: "evt-7"));

        dispatchStages.Should().Equal("prepare", "process", "materialized");
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldCapturePayloadFreeCommittedEnvelopeMetadata()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-recent-envelope",
            onProcess: _ => ProjectionScopeDispatchResult.Success(7, "event-type"),
            eventSourcing: new TrackingEventSourcing());
        var timestamp = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 30, 1, 2, 3, TimeSpan.Zero));
        var envelope = BuildForwardedCommittedObservationEnvelope(
            "projection-scope-recent-envelope",
            version: 7,
            eventId: "evt-7",
            timestamp: timestamp);

        await agent.HandleObservedEnvelopeAsync(envelope);

        var recent = agent.State.RecentObservedEnvelopes.Should().ContainSingle().Subject;
        recent.EventId.Should().Be("evt-7");
        recent.TypeUrl.Should().Be(Any.Pack(new StringValue { Value = "payload" }).TypeUrl);
        recent.StateVersion.Should().Be(7);
        recent.TimestampUtc.Should().Be(timestamp);
        typeof(ProjectionObservedEnvelopeMetadata).GetProperty("Payload").Should().BeNull();
        typeof(ProjectionObservedEnvelopeMetadata).GetProperty("Envelope").Should().BeNull();
        typeof(ProjectionObservedEnvelopeMetadata).GetProperty("EventData").Should().BeNull();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenDurableRecoveryEnabled_ShouldStageBeforeWriteAndClearWithExactWatermark()
    {
        TestScopeAgent? agent = null;
        agent = BuildActivatedAgent(
            scopeId: "projection-scope-durable-stage",
            onProcess: _ =>
            {
                agent!.State.InFlightObservation.Should().NotBeNull();
                agent.State.InFlightObservation.Source.ActorId.Should().Be("publisher-actor");
                agent.State.InFlightObservation.Source.StateVersion.Should().Be(7);
                agent.State.InFlightObservation.Source.EventId.Should().Be("evt-7");
                return ProjectionScopeDispatchResult.Success(7, "event-type");
            },
            eventSourcing: new TrackingEventSourcing(),
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(agent);

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-durable-stage",
            version: 7,
            eventId: "evt-7"));

        agent.State.InFlightObservation.Should().BeNull();
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .Should().BeEquivalentTo(new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 7,
                EventId = "evt-7",
            });
    }

    [Fact]
    public async Task HandleResumeInFlightObservationAsync_ShouldRecoverGraphCommitBeforeScopeWatermark()
    {
        var graphApplyCount = 0;
        var failedEventSourcing = new TrackingEventSourcing(
            failOnEventType: typeof(ProjectionScopeWatermarkAdvancedEvent));
        var failedScope = BuildActivatedAgent(
            scopeId: "projection-scope-crash-recovery",
            onProcess: _ =>
            {
                graphApplyCount++;
                return ProjectionScopeDispatchResult.Success(3, "event-type");
            },
            eventSourcing: failedEventSourcing,
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(failedScope);
        var envelope = BuildForwardedCommittedObservationEnvelope(
            "projection-scope-crash-recovery",
            version: 3,
            eventId: "evt-3");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failedScope.HandleObservedEnvelopeAsync(envelope));
        failedScope.State.InFlightObservation.Should().NotBeNull();
        failedScope.State.LastSuccessfulSourceCoordinatesByActor.Should().BeEmpty();
        failedScope.State.AttemptedEnvelopeTotal.Should().Be(1);

        var recoveryEventSourcing = new TrackingEventSourcing();
        var recoveredScope = BuildActivatedAgent(
            scopeId: "projection-scope-crash-recovery",
            onProcess: _ =>
            {
                graphApplyCount++;
                return ProjectionScopeDispatchResult.Success(3, "event-type");
            },
            eventSourcing: recoveryEventSourcing,
            enableDurableObservationRecovery: true);
        recoveredScope.State.MergeFrom(failedScope.State);
        await InitializeFailureTrackerAsync(recoveredScope);

        await recoveredScope.HandleResumeInFlightObservationAsync(
            new ResumeProjectionInFlightObservationCommand
            {
                ExpectedSource = failedScope.State.InFlightObservation.Source.Clone(),
            });

        graphApplyCount.Should().Be(2, "the same durable source is replayed after the crash boundary");
        recoveredScope.State.InFlightObservation.Should().BeNull();
        recoveredScope.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"].EventId
            .Should().Be("evt-3");
        recoveredScope.State.AttemptedEnvelopeTotal.Should().Be(1,
            "the staged attempt is already the durable authority for in-flight recovery");
        recoveryEventSourcing.CommittedEventTypes.Should().Equal(typeof(ProjectionScopeWatermarkAdvancedEvent));
        recoveryEventSourcing.Snapshots.Should().ContainSingle()
            .Which.InFlightObservation.Should().BeNull();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenNewerSourceFollowsWatermarkFailure_ShouldRecoverInFlightFirst()
    {
        var processedVersions = new List<long>();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-newer-source-recovery",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryUnpack(envelope, out var published).Should().BeTrue();
                var version = published!.StateEvent.Version;
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing(
                failOnEventType: typeof(ProjectionScopeWatermarkAdvancedEvent)),
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(agent);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
                "projection-scope-newer-source-recovery",
                version: 8,
                eventId: "evt-8")));

        agent.State.InFlightObservation!.Source.Should().BeEquivalentTo(
            new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 8,
                EventId = "evt-8",
            });

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-newer-source-recovery",
            version: 9,
            eventId: "evt-9"));

        processedVersions.Should().Equal(8, 8, 9);
        agent.State.InFlightObservation.Should().BeNull();
        agent.State.SuccessfulMaterializationTotal.Should().Be(2);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .Should().BeEquivalentTo(new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 9,
                EventId = "evt-9",
            });
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenSameVersionHasDifferentEventId_ShouldFailClosed()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-coordinate-conflict",
            onProcess: _ =>
            {
                processed++;
                return ProjectionScopeDispatchResult.Success(5, "event-type");
            },
            enableDurableObservationRecovery: true);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"] =
            new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 5,
                EventId = "evt-original",
            };

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(
            BuildForwardedCommittedObservationEnvelope(
                "projection-scope-coordinate-conflict",
                version: 5,
                eventId: "evt-conflict"));

        await act.Should().ThrowAsync<ProjectionSourceCoordinateConflictException>();
        processed.Should().Be(0);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenDifferentSourceIsInFlight_ShouldRecoverItBeforeNewSource()
    {
        var processedVersions = new List<long>();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-pending-source",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryUnpack(envelope, out var published).Should().BeTrue();
                var version = published!.StateEvent.Version;
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing(),
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(agent);
        agent.State.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 1,
                EventId = "evt-1",
            },
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-pending-source",
                version: 1,
                eventId: "evt-1"),
        };

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-pending-source",
            version: 2,
            eventId: "evt-2"));

        processedVersions.Should().Equal(1, 2);
        agent.State.InFlightObservation.Should().BeNull();
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .Should().BeEquivalentTo(new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 2,
                EventId = "evt-2",
            });
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenInFlightEnvelopeIsMissing_ShouldFailClosed()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-missing-in-flight-envelope",
            onProcess: _ =>
            {
                processed++;
                return ProjectionScopeDispatchResult.Success(2, "event-type");
            },
            enableDurableObservationRecovery: true);
        agent.State.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 1,
                EventId = "evt-1",
            },
        };

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(
            BuildForwardedCommittedObservationEnvelope(
                "projection-scope-missing-in-flight-envelope",
                version: 2,
                eventId: "evt-2"));

        await act.Should().ThrowAsync<ProjectionScopeInFlightObservationPendingException>();
        processed.Should().Be(0);
    }

    [Theory]
    [InlineData(7L)]
    [InlineData(17L)]
    public async Task HandleObservedEnvelopeAsync_WhenMaintenanceRepublishTargetsPendingActor_ShouldSupersedeInFlight(
        long maintenanceVersion)
    {
        const long pendingVersion = 7;
        var maintenanceEventId = CommittedStateRepublish.BuildEventId("publisher-actor", maintenanceVersion);
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-maintenance-supersession",
            onProcess: envelope =>
            {
                processed++;
                CommittedStateEventEnvelope.TryUnpack(envelope, out var published).Should().BeTrue();
                published!.StateEvent.EventId.Should().Be(maintenanceEventId);
                return ProjectionScopeDispatchResult.Success(maintenanceVersion, "event-type");
            },
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(agent);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"] =
            new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = pendingVersion - 1,
                EventId = "evt-committed",
            };
        agent.State.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = pendingVersion,
                EventId = "evt-stuck",
            },
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-maintenance-supersession",
                pendingVersion,
                eventId: "evt-stuck"),
        };

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-maintenance-supersession",
            maintenanceVersion,
            maintenanceEventId));

        processed.Should().Be(1);
        agent.State.InFlightObservation.Should().BeNull();
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .EventId.Should().Be(maintenanceEventId);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .StateVersion.Should().Be(maintenanceVersion);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenOrdinaryEventFollowsCommittedMaintenance_ShouldFenceIt()
    {
        const long version = 7;
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-maintenance-fence",
            onProcess: _ =>
            {
                processed++;
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            enableDurableObservationRecovery: true);
        await InitializeFailureTrackerAsync(agent);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"] =
            new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = version,
                EventId = CommittedStateRepublish.BuildEventId("publisher-actor", version),
            };

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-maintenance-fence",
            version,
            eventId: "evt-delayed"));

        processed.Should().Be(0);
        agent.State.LastSuccessfulSourceCoordinatesByActor["publisher-actor"]
            .EventId.Should().StartWith(CommittedStateRepublish.EventIdPrefix);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldRetainOnlyTheFiftyMostRecentCommittedEnvelopes()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-bounded-recent",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out _, out var version)
                    .Should().BeTrue();
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing());

        for (var version = 1; version <= 51; version++)
        {
            await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
                "projection-scope-bounded-recent",
                version,
                eventId: $"evt-{version}"));
        }

        agent.State.RecentObservedEnvelopes.Should().HaveCount(50);
        agent.State.RecentObservedEnvelopes[0].EventId.Should().Be("evt-2");
        agent.State.RecentObservedEnvelopes[^1].EventId.Should().Be("evt-51");
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldProcessCommittedObservation_WhenScopeWatermarkAdvancedByOtherPublisher()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-mixed-publishers",
            onProcess: _ =>
            {
                processed++;
                return ProjectionScopeDispatchResult.Success(2, "event-type");
            });
        agent.State.HighestSeenVersion = 26;
        var envelope = BuildForwardedCommittedObservationEnvelope("projection-scope-mixed-publishers", version: 2);

        await agent.HandleObservedEnvelopeAsync(envelope);

        processed.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_DurableScopeProcessesLowerVersionAfterLineageRegression()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-lineage-regression",
            onProcess: envelope =>
            {
                processed++;
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                version.Should().Be(2);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing(),
            runtimeMode: ProjectionRuntimeMode.DurableMaterialization);
        agent.State.LastSuccessfulVersionsByActor["publisher-actor"] = 3;

        await agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservationEnvelope(
            "projection-scope-lineage-regression",
            version: 2,
            eventId: "rebuild:publisher-actor:2"));

        processed.Should().Be(1, "durable repair must not inherit the session observation version fence");
        agent.State.LastSuccessfulVersionsByActor["publisher-actor"].Should().Be(
            3,
            "the historical high-water mark remains diagnostic and is not rewritten by repair");
        agent.State.SuccessfulMaterializationTotal.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldIgnoreDuplicateAndStaleVersionsFromSamePublisher()
    {
        var processed = 0;
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-source-watermark",
            onProcess: envelope =>
            {
                processed++;
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing(),
            runtimeMode: ProjectionRuntimeMode.SessionObservation);
        var versionTwo = BuildForwardedCommittedObservationEnvelope(
            "projection-scope-source-watermark",
            version: 2);
        var versionOne = BuildForwardedCommittedObservationEnvelope(
            "projection-scope-source-watermark",
            version: 1);

        await agent.HandleObservedEnvelopeAsync(versionTwo);
        await agent.HandleObservedEnvelopeAsync(versionTwo.Clone());
        await agent.HandleObservedEnvelopeAsync(versionOne);

        processed.Should().Be(1);
        agent.State.LastSuccessfulVersionsByActor["publisher-actor"].Should().Be(2);
    }

    [Fact]
    public async Task HandleReplayAsync_ShouldReprocessRecordedFailure_WhenNewerVersionAlreadySucceeded()
    {
        var processed = 0;
        var eventSourcing = new TrackingEventSourcing();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-failure-gap",
            onProcess: envelope =>
            {
                processed++;
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                version.Should().Be(1);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: eventSourcing,
            runtimeMode: ProjectionRuntimeMode.SessionObservation);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;

        var failedEnvelope = BuildForwardedCommittedObservationEnvelope(
            "projection-scope-failure-gap",
            version: 1);
        agent.State.LastSuccessfulVersionsByActor["publisher-actor"] = 2;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-version-1",
            SourceVersion = 1,
            Envelope = failedEnvelope,
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 1 });

        processed.Should().Be(1);
        agent.State.ReceivedEnvelopeTotal.Should().Be(0, "replay is an attempt, not a newly received envelope");
        agent.State.AttemptedEnvelopeTotal.Should().Be(1);
        agent.State.SuccessfulMaterializationTotal.Should().Be(1);
        agent.State.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReplayAsync_WhenMaterializationFails_ShouldUpdateOriginalFailureWithoutDuplicatingBacklog()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-replay-failure",
            onProcess: _ => throw new InvalidOperationException("still failing"),
            eventSourcing: new TrackingEventSourcing(),
            recordFailureBeforeThrow: true);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-original",
            EventType = "type.googleapis.com/aevatar.TestEvent",
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-failure",
                version: 1),
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 1 });

        agent.State.Failures.Should().ContainSingle().Which.FailureId.Should().Be("failure-original");
        agent.State.Failures[0].Attempts.Should().Be(1);
        agent.State.ReceivedEnvelopeTotal.Should().Be(0, "replay is an attempt, not a newly received envelope");
        agent.State.AttemptedEnvelopeTotal.Should().Be(1);
        agent.State.FailedAttemptTotal.Should().Be(1);
    }

    [Fact]
    public async Task HandleReplayAsync_WithEarlierInFlightObservation_ShouldResumeItBeforeChargingBacklog()
    {
        var processCount = 0;
        var publisher = new RecordingEventPublisher();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-replay-serialized-existing",
            onProcess: _ =>
            {
                processCount++;
                throw new InvalidOperationException("the in-flight source must resume on its own turn");
            },
            eventSourcing: new TrackingEventSourcing(),
            enableDurableObservationRecovery: true);
        agent.EventPublisher = publisher;
        await InitializeFailureTrackerAsync(agent);
        agent.State.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "publisher-actor",
                StateVersion = 1,
                EventId = "evt-1",
            },
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-serialized-existing",
                version: 1,
                eventId: "evt-1"),
        };
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-2",
            SourceVersion = 2,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-serialized-existing",
                version: 2,
                eventId: "evt-2"),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-3",
            SourceVersion = 3,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-serialized-existing",
                version: 3,
                eventId: "evt-3"),
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 2 });

        processCount.Should().Be(0);
        agent.State.Failures.Should().OnlyContain(static failure => failure.Attempts == 0);
        publisher.Published.Should().ContainSingle().Which.Message
            .Should().BeOfType<ResumeProjectionInFlightObservationCommand>();
    }

    [Fact]
    public async Task HandleReplayAsync_WhenFailureStagesSource_ShouldStopBeforeChargingNextBacklogItem()
    {
        var processedVersions = new List<long>();
        var publisher = new RecordingEventPublisher();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-replay-serialized-new",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                processedVersions.Add(version);
                throw new InvalidOperationException("still failing");
            },
            eventSourcing: new TrackingEventSourcing(),
            enableDurableObservationRecovery: true);
        agent.EventPublisher = publisher;
        await InitializeFailureTrackerAsync(agent);
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-2",
            SourceVersion = 2,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-serialized-new",
                version: 2,
                eventId: "evt-2"),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-3",
            SourceVersion = 3,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-replay-serialized-new",
                version: 3,
                eventId: "evt-3"),
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 2 });

        processedVersions.Should().Equal(2);
        agent.State.InFlightObservation!.Source.Should().BeEquivalentTo(new ProjectionSourceCoordinate
        {
            ActorId = "publisher-actor",
            StateVersion = 2,
            EventId = "evt-2",
        });
        agent.State.Failures.Should().ContainSingle(static failure => failure.FailureId == "failure-2")
            .Which.Attempts.Should().Be(1);
        agent.State.Failures.Should().ContainSingle(static failure => failure.FailureId == "failure-3")
            .Which.Attempts.Should().Be(0);
        publisher.Published.Should().ContainSingle().Which.Message
            .Should().BeOfType<ResumeProjectionInFlightObservationCommand>();
    }

    [Fact]
    public async Task HandleReplayAsync_WhenAutomatic_ShouldSkipRetryExhaustedFailures()
    {
        var processedVersions = new List<long>();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-automatic-replay",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing());
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-exhausted",
            SourceVersion = 1,
            RetryExhausted = true,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-automatic-replay",
                version: 1),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-recoverable",
            SourceVersion = 2,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-automatic-replay",
                version: 2),
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand
        {
            MaxItems = 1,
            AutomaticRecovery = true,
        });

        processedVersions.Should().Equal(2);
        agent.State.Failures.Should().ContainSingle()
            .Which.FailureId.Should().Be("failure-exhausted");
    }

    [Fact]
    public async Task HandleReplayAsync_WhenExactCoordinateSucceeds_ShouldResolveAllDuplicateFailures()
    {
        const string sourceActorId = "publisher-actor";
        const string eventId = "evt-shared-coordinate";
        const long sourceVersion = 7;
        var processedVersions = new List<long>();
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-exact-coordinate-replay",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: new TrackingEventSourcing());
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-exhausted-duplicate",
            SourceActorId = sourceActorId,
            SourceVersion = sourceVersion,
            EventId = eventId,
            RetryExhausted = true,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-exact-coordinate-replay",
                version: sourceVersion,
                eventId: eventId),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-recoverable-duplicate",
            SourceActorId = sourceActorId,
            SourceVersion = sourceVersion,
            EventId = eventId,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-exact-coordinate-replay",
                version: sourceVersion,
                eventId: eventId),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-other-coordinate",
            SourceActorId = sourceActorId,
            SourceVersion = sourceVersion + 1,
            EventId = "evt-other-coordinate",
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-exact-coordinate-replay",
                version: sourceVersion + 1,
                eventId: "evt-other-coordinate"),
        });

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand
        {
            MaxItems = 1,
            AutomaticRecovery = true,
        });

        processedVersions.Should().Equal(sourceVersion);
        agent.State.Failures.Should().ContainSingle()
            .Which.FailureId.Should().Be("failure-other-coordinate");
    }

    [Fact]
    public async Task HandleRetryExhaustedReplayAsync_ShouldReplayOnlyExhaustedFailuresBehindExactManifest()
    {
        var processedVersions = new List<long>();
        var eventSourcing = new TrackingEventSourcing(initialVersion: 40);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-operator-exhausted-replay",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: eventSourcing);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-recoverable",
            SourceActorId = "publisher-actor",
            SourceVersion = 1,
            EventId = "evt-recoverable",
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-operator-exhausted-replay",
                version: 1,
                eventId: "evt-recoverable"),
        });
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-exhausted",
            SourceActorId = "publisher-actor",
            SourceVersion = 2,
            EventId = "evt-exhausted",
            RetryExhausted = true,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-operator-exhausted-replay",
                version: 2,
                eventId: "evt-exhausted"),
        });

        await agent.HandleRetryExhaustedReplayAsync(
            new ReplayRetryExhaustedProjectionFailuresCommand
            {
                MaxItems = 1,
                ExpectedScopeStateVersion = eventSourcing.CurrentVersion,
                ExpectedUnresolvedFailureCount = 2,
                ExpectedRetryExhaustedFailureCount = 1,
                RequestId = "operator-replay-1",
                Reason = "storage recovery completed",
                RequestedBySubjectId = "operator-alpha",
            });

        processedVersions.Should().Equal(2);
        agent.State.Failures.Should().ContainSingle()
            .Which.FailureId.Should().Be("failure-recoverable");
    }

    [Fact]
    public async Task HandleRetryExhaustedReplayAsync_WhenManifestIsStale_ShouldNotSpendRetryBudget()
    {
        var processCount = 0;
        var eventSourcing = new TrackingEventSourcing(initialVersion: 40);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-stale-operator-replay",
            onProcess: _ =>
            {
                processCount++;
                return ProjectionScopeDispatchResult.Success(1, "event-type");
            },
            eventSourcing: eventSourcing);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-exhausted",
            SourceActorId = "publisher-actor",
            SourceVersion = 1,
            EventId = "evt-exhausted",
            RetryExhausted = true,
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-stale-operator-replay",
                version: 1,
                eventId: "evt-exhausted"),
        });

        await agent.HandleRetryExhaustedReplayAsync(
            new ReplayRetryExhaustedProjectionFailuresCommand
            {
                MaxItems = 1,
                ExpectedScopeStateVersion = eventSourcing.CurrentVersion - 1,
                ExpectedUnresolvedFailureCount = 1,
                ExpectedRetryExhaustedFailureCount = 1,
                RequestId = "operator-replay-stale",
                Reason = "storage recovery completed",
                RequestedBySubjectId = "operator-alpha",
            });

        processCount.Should().Be(0);
        agent.State.Failures.Should().ContainSingle().Which.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task HandleReplayAsync_WhenAutomaticBacklogExceedsBatch_ShouldAdmitNextBatchAtAdvancedStateVersion()
    {
        var processedVersions = new List<long>();
        var eventSourcing = new TrackingEventSourcing(initialVersion: 1);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-automatic-batches",
            onProcess: envelope =>
            {
                CommittedStateEventEnvelope.TryGetObservedPayload(
                    envelope,
                    out _,
                    out _,
                    out var version).Should().BeTrue();
                processedVersions.Add(version);
                return ProjectionScopeDispatchResult.Success(version, "event-type");
            },
            eventSourcing: eventSourcing);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        for (var version = 1; version <= 101; version++)
        {
            agent.State.Failures.Add(new ProjectionScopeFailure
            {
                FailureId = $"failure-{version}",
                SourceVersion = version,
                Envelope = BuildForwardedCommittedObservationEnvelope(
                    "projection-scope-automatic-batches",
                    version,
                    eventId: $"evt-{version}"),
            });
        }

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand
        {
            MaxItems = 100,
            AutomaticRecovery = true,
            ObservedScopeStateVersion = 1,
        });

        processedVersions.Should().HaveCount(100);
        agent.State.Failures.Should().ContainSingle()
            .Which.FailureId.Should().Be("failure-101");
        var nextObservedScopeStateVersion = eventSourcing.CurrentVersion;
        nextObservedScopeStateVersion.Should().BeGreaterThan(1);

        await agent.HandleReplayAsync(new ReplayProjectionFailuresCommand
        {
            MaxItems = 100,
            AutomaticRecovery = true,
            ObservedScopeStateVersion = nextObservedScopeStateVersion,
        });

        processedVersions.Should().Equal(Enumerable.Range(1, 101).Select(static value => (long)value));
        agent.State.Failures.Should().BeEmpty();
        agent.State.LastAutomaticRecoveryObservedStateVersion.Should().Be(nextObservedScopeStateVersion);
    }

    [Fact]
    public async Task HandleReplayAsync_WhenAutomaticTokenIsRepeated_ShouldSpendRetryBudgetOnce()
    {
        var processCount = 0;
        var eventSourcing = new TrackingEventSourcing(initialVersion: 1);
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-automatic-deduplication",
            onProcess: _ =>
            {
                processCount++;
                throw new InvalidOperationException("still failing");
            },
            eventSourcing: eventSourcing);
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
        agent.State.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-recoverable",
            EventType = "type.googleapis.com/aevatar.TestEvent",
            Envelope = BuildForwardedCommittedObservationEnvelope(
                "projection-scope-automatic-deduplication",
                version: 1),
        });
        var commandFromReplica = new ReplayProjectionFailuresCommand
        {
            MaxItems = 1,
            AutomaticRecovery = true,
            ObservedScopeStateVersion = 1,
        };

        await agent.HandleReplayAsync(commandFromReplica);
        var stateVersionAfterFirstCommand = eventSourcing.CurrentVersion;
        await agent.HandleReplayAsync(commandFromReplica.Clone());

        processCount.Should().Be(1);
        eventSourcing.CurrentVersion.Should().Be(stateVersionAfterFirstCommand);
        agent.State.LastAutomaticRecoveryObservedStateVersion.Should().Be(1);
        agent.State.Failures.Should().ContainSingle();
        agent.State.Failures[0].Attempts.Should().Be(1);
        agent.State.FailedAttemptTotal.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ShouldSwallow_DeterministicProjectionFailure()
    {
        var agent = BuildActivatedAgent(
            scopeId: "projection-scope-swallow",
            onProcess: _ => throw new InvalidOperationException("deterministic boom"));

        var envelope = BuildForwardedObserverEnvelope(targetStreamId: "projection-scope-swallow");

        Func<Task> act = () => agent.HandleObservedEnvelopeAsync(envelope);

        await act.Should().NotThrowAsync();
    }

    private static TestScopeAgent BuildActivatedAgent(
        string scopeId,
        Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess,
        IEventSourcingBehavior<ProjectionScopeState>? eventSourcing = null,
        ProjectionRuntimeMode runtimeMode = ProjectionRuntimeMode.DurableMaterialization,
        bool recordFailureBeforeThrow = false,
        bool enableDurableObservationRecovery = false,
        ICollection<string>? dispatchStages = null)
    {
        var agent = new TestScopeAgent(
            onProcess,
            runtimeMode,
            recordFailureBeforeThrow,
            enableDurableObservationRecovery,
            dispatchStages);

        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, scopeId);

        agent.EventSourcing = eventSourcing ?? new TrackingEventSourcing();

        agent.State.RootActorId = "root-actor";
        agent.State.ProjectionKind = "test-kind";
        agent.State.SessionId = "session-1";
        agent.State.Mode = ProjectionScopeMode.DurableMaterialization;
        agent.State.Active = true;
        agent.State.Released = false;

        var services = new ServiceCollection();
        services.AddSingleton<Func<ProjectionRuntimeScopeKey, TestContext>>(
            static _ => new TestContext("root-actor", "test-kind"));
        services.AddSingleton<IStreamProvider>(new NoOpStreamProvider());
        agent.Services = services.BuildServiceProvider();

        return agent;
    }

    private static async Task InitializeFailureTrackerAsync(TestScopeAgent agent)
    {
        agent.State.Active = false;
        await agent.InitializeForTestAsync();
        agent.State.Active = true;
    }

    private static EventEnvelope BuildForwardedObserverEnvelope(string targetStreamId)
    {
        var original = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("publisher-actor"),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: "publisher-actor",
            targetStreamId: targetStreamId,
            StreamForwardingMode.HandleThenForward);
    }

    private static EventEnvelope BuildForwardedCommittedObservationEnvelope(
        string targetStreamId,
        long version,
        string eventId = "evt-duplicate",
        Timestamp? timestamp = null)
    {
        var original = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("publisher-actor"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = timestamp,
                    EventData = Any.Pack(new StringValue { Value = "payload" }),
                    AgentId = "publisher-actor",
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            }),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: "publisher-actor",
            targetStreamId: targetStreamId,
            StreamForwardingMode.HandleThenForward);
    }

    private sealed class NoOpStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId) => new NoOpStream(actorId);

        private sealed class NoOpStream(string streamId) : IStream
        {
            public string StreamId { get; } = streamId;

            public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage =>
                throw new NotSupportedException();

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new() =>
                throw new NotSupportedException();

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage Message, TopologyAudience Audience)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeAgent : ProjectionScopeGAgentBase<TestContext>
    {
        private readonly Func<EventEnvelope, ProjectionScopeDispatchResult> _onProcess;
        private readonly ProjectionRuntimeMode _runtimeMode;
        private readonly bool _recordFailureBeforeThrow;
        private readonly bool _enableDurableObservationRecovery;
        private readonly ICollection<string>? _dispatchStages;

        public TestScopeAgent(
            Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess,
            ProjectionRuntimeMode runtimeMode = ProjectionRuntimeMode.DurableMaterialization,
            bool recordFailureBeforeThrow = false,
            bool enableDurableObservationRecovery = false,
            ICollection<string>? dispatchStages = null)
        {
            _onProcess = onProcess;
            _runtimeMode = runtimeMode;
            _recordFailureBeforeThrow = recordFailureBeforeThrow;
            _enableDurableObservationRecovery = enableDurableObservationRecovery;
            _dispatchStages = dispatchStages;
        }

        protected override ProjectionRuntimeMode RuntimeMode => _runtimeMode;

        protected override bool EnablesDurableObservationRecovery =>
            _enableDurableObservationRecovery;

        public Task InitializeForTestAsync() => OnActivateAsync(CancellationToken.None);

        public bool ShouldPersistRecoverySnapshotForTest() =>
            ShouldPersistSnapshotAfterPublicationRecovery(State);

        protected override ValueTask PrepareObservationContextAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct)
        {
            _dispatchStages?.Add("prepare");
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnObservationMaterializedAsync(
            TestContext context,
            EventEnvelope envelope,
            ProjectionScopeDispatchResult result,
            CancellationToken ct)
        {
            _dispatchStages?.Add("materialized");
            return ValueTask.CompletedTask;
        }

        protected override async ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct)
        {
            _dispatchStages?.Add("process");
            if (_recordFailureBeforeThrow)
            {
                await RecordDispatchFailureAsync(
                    "projection-execution",
                    envelope.Id,
                    envelope.Payload?.TypeUrl ?? string.Empty,
                    1,
                    "still failing",
                    envelope);
            }

            return _onProcess(envelope);
        }
    }

    private sealed record TestContext(string RootActorId, string ProjectionKind)
        : IProjectionMaterializationContext;

    private sealed class TrackingEventSourcing : IEventSourcingBehavior<ProjectionScopeState>
    {
        private readonly List<IMessage> _pending = [];
        private readonly System.Type? _failOnEventType;
        private int _remainingInjectedFailures;

        public TrackingEventSourcing(
            long initialVersion = 0,
            System.Type? failOnEventType = null)
        {
            CurrentVersion = initialVersion;
            _failOnEventType = failOnEventType;
            _remainingInjectedFailures = failOnEventType == null ? 0 : 1;
        }

        public int DiscardCallCount { get; private set; }
        public long CurrentVersion { get; private set; }
        public List<System.Type> CommittedEventTypes { get; } = [];
        public List<ProjectionScopeState> Snapshots { get; } = [];
        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);
        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            if (_remainingInjectedFailures > 0 &&
                _failOnEventType != null &&
                _pending.Any(evt => evt.GetType() == _failOnEventType))
            {
                _remainingInjectedFailures--;
                _pending.Clear();
                throw new InvalidOperationException("Injected failure before scope watermark commit.");
            }

            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                CommittedEventTypes.Add(evt.GetType());
                CurrentVersion++;
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }
        public Task PersistSnapshotAsync(ProjectionScopeState currentState, CancellationToken ct = default)
        {
            Snapshots.Add(currentState.Clone());
            return Task.CompletedTask;
        }
        public Task<ProjectionScopeState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeState?>(null);
        public void DiscardPendingEvents()
        {
            DiscardCallCount++;
            _pending.Clear();
        }
        public ProjectionScopeState TransitionState(ProjectionScopeState current, IMessage evt) =>
            evt switch
            {
                ProjectionScopeEnvelopeReceivedEvent received =>
                    ProjectionScopeStateApplier.ApplyEnvelopeReceived(current, received),
                ProjectionScopeEnvelopeAttemptedEvent attempted =>
                    ProjectionScopeStateApplier.ApplyEnvelopeAttempted(current, attempted),
                ProjectionScopeObservationStagedEvent staged =>
                    ProjectionScopeStateApplier.ApplyObservationStaged(current, staged),
                ProjectionScopeWatermarkAdvancedEvent watermark =>
                    ProjectionScopeStateApplier.ApplyWatermarkAdvanced(current, watermark),
                ProjectionScopeDispatchFailedEvent failed =>
                    ProjectionScopeStateApplier.ApplyDispatchFailed(current, failed),
                ProjectionScopeFailureReplayedEvent replayed =>
                    ProjectionScopeStateApplier.ApplyFailureReplayed(current, replayed),
                ProjectionScopeAutomaticRecoveryRequestedEvent automaticRecovery =>
                    ProjectionScopeStateApplier.ApplyAutomaticRecoveryRequested(current, automaticRecovery),
                _ => current,
            };
    }

}
