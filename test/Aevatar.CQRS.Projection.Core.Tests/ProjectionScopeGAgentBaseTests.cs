using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
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
        bool recordFailureBeforeThrow = false)
    {
        var agent = new TestScopeAgent(onProcess, runtimeMode, recordFailureBeforeThrow);

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
        agent.Services = services.BuildServiceProvider();

        return agent;
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

    private sealed class TestScopeAgent : ProjectionScopeGAgentBase<TestContext>
    {
        private readonly Func<EventEnvelope, ProjectionScopeDispatchResult> _onProcess;
        private readonly ProjectionRuntimeMode _runtimeMode;
        private readonly bool _recordFailureBeforeThrow;

        public TestScopeAgent(
            Func<EventEnvelope, ProjectionScopeDispatchResult> onProcess,
            ProjectionRuntimeMode runtimeMode = ProjectionRuntimeMode.DurableMaterialization,
            bool recordFailureBeforeThrow = false)
        {
            _onProcess = onProcess;
            _runtimeMode = runtimeMode;
            _recordFailureBeforeThrow = recordFailureBeforeThrow;
        }

        protected override ProjectionRuntimeMode RuntimeMode => _runtimeMode;

        public Task InitializeForTestAsync() => OnActivateAsync(CancellationToken.None);

        protected override async ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct)
        {
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
        public int DiscardCallCount { get; private set; }
        public long CurrentVersion { get; private set; }
        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);
        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
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
        public Task PersistSnapshotAsync(ProjectionScopeState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;
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
                ProjectionScopeWatermarkAdvancedEvent watermark =>
                    ProjectionScopeStateApplier.ApplyWatermarkAdvanced(current, watermark),
                ProjectionScopeDispatchFailedEvent failed =>
                    ProjectionScopeStateApplier.ApplyDispatchFailed(current, failed),
                ProjectionScopeFailureReplayedEvent replayed =>
                    ProjectionScopeStateApplier.ApplyFailureReplayed(current, replayed),
                _ => current,
            };
    }

}
