using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthProbeTargetGAgentTests : IAsyncLifetime
{
    private const string ActorId = "health-probe::test";

    private HealthProbeTargetGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private FakeExecutor _executor = null!;
    private InMemoryEventStore _eventStore = null!;
    private TrackingCallbackScheduler _scheduler = null!;
    private FakeTimeProvider _timeProvider = null!;
    private InlineSelfPublisher _publisher = null!;
    private InMemoryHealthProbeOperationalSnapshotStore _snapshotStore = null!;
    private RecordingEventSourcingSnapshotStore<HealthProbeTargetState> _stateSnapshotStore = null!;
    private bool _deactivated;

    public async Task InitializeAsync()
    {
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        _executor = new FakeExecutor(_timeProvider);
        _eventStore = new InMemoryEventStore();
        _scheduler = new TrackingCallbackScheduler();
        _snapshotStore = new InMemoryHealthProbeOperationalSnapshotStore();
        _stateSnapshotStore = new RecordingEventSourcingSnapshotStore<HealthProbeTargetState>();
        _serviceProvider = BuildServiceProvider(
            _executor,
            _eventStore,
            _scheduler,
            _snapshotStore,
            _stateSnapshotStore,
            _timeProvider);

        (_agent, _publisher) = CreateAgent(_serviceProvider, ActorId);
        await _agent.ActivateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_deactivated)
            await _agent.DeactivateAsync();
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task Configure_PersistsOnlyTheDescriptor()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth", intervalSeconds: 30));

        _agent.EventSourcing!.CurrentVersion.Should().Be(1);
        _eventStore.CountEvents(HealthProbeConfigured.Descriptor).Should().Be(1);
        _eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(0);
        _eventStore.CountEvents(HealthProbeExecutionStarted.Descriptor).Should().Be(0);
        _agent.State.Spec.Slug.Should().Be("nyxid-auth");
        _agent.State.Spec.IntervalSeconds.Should().Be(30);
        _agent.State.LastOutcome.Should().BeNull();
        (await SnapshotAsync("nyxid-auth")).Target.Slug.Should().Be("nyxid-auth");
        _scheduler.ScheduledTimeouts.Should().Be(0);
    }

    [Fact]
    public async Task Configure_SchedulesInitialEphemeralTickFromInjectedClock()
    {
        var configuredAt = _timeProvider.GetUtcNow();
        await ConfigureAsync(NewDescriptor("nyxid-auth"));

        _timeProvider.Advance(TimeSpan.FromMilliseconds(999));
        _publisher.TickCount.Should().Be(0);
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var tick = await _publisher.TickHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tick.ScheduledFor.ToDateTimeOffset().Should().Be(configuredAt.AddSeconds(1));
        _publisher.LastTickRoute!.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        _scheduler.ScheduledTimeouts.Should().Be(0);
    }

    [Fact]
    public async Task Configure_TwiceWithSameDescriptor_DoesNotDuplicateEventOrInitialTick()
    {
        var descriptor = NewDescriptor("nyxid-auth");
        await ConfigureAsync(descriptor.Clone());
        await ConfigureAsync(descriptor.Clone());

        _agent.EventSourcing!.CurrentVersion.Should().Be(1);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await _publisher.TickHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _publisher.TickCount.Should().Be(1);
    }

    [Fact]
    public async Task Configure_WithChangedDescriptor_ClearsOperationalHistory()
    {
        var descriptor = NewDescriptor("nyxid-auth");
        await ConfigureAsync(descriptor);
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Down, "http_500");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = descriptor.Slug });
        (await SnapshotAsync(descriptor.Slug)).RecentOutcomes.Should().ContainSingle();

        var updated = descriptor.Clone();
        updated.DisplayName = "NyxID Auth Updated";
        await ConfigureAsync(updated);

        var snapshot = await SnapshotAsync(descriptor.Slug);
        snapshot.Target.DisplayName.Should().Be("NyxID Auth Updated");
        snapshot.LastOutcome.Should().BeNull();
        snapshot.LastCheckAt.Should().BeNull();
        snapshot.LastSuccessAt.Should().BeNull();
        snapshot.ConsecutiveFailures.Should().Be(0);
        snapshot.RecentOutcomes.Should().BeEmpty();
        _agent.State.LastOutcome.Should().BeNull();
    }

    [Fact]
    public async Task Tick_SuccessfulProbe_WritesSnapshotWithoutDurableSamplingState()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        var versionBeforeTick = _agent.EventSourcing!.CurrentVersion;
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200");

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var snapshot = await SnapshotAsync("nyxid-auth");
        _executor.Invocations.Should().Be(1);
        snapshot.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        snapshot.ConsecutiveFailures.Should().Be(0);
        snapshot.RecentOutcomes.Should().ContainSingle();
        _agent.EventSourcing.CurrentVersion.Should().Be(versionBeforeTick);
        _eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(0);
        _eventStore.CountEvents(HealthProbeExecutionStarted.Descriptor).Should().Be(0);
        _eventStore.CountEvents(HealthProbeExecutionCleared.Descriptor).Should().Be(0);
        _scheduler.ScheduledTimeouts.Should().Be(0);
    }

    [Fact]
    public async Task Tick_SuccessfulProbe_DeliversCompletionThroughSelfHandlingGate()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200");

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _publisher.LastCompletionRoute!.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        _publisher.LastCompletionDelivered.Should().BeTrue();
        (await SnapshotAsync("nyxid-auth")).LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
    }

    [Fact]
    public async Task Tick_StampsObservedAtAndLatencyFromInjectedClock()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-05-21T10:05:00Z"));
        _executor.Delay = TimeSpan.FromMilliseconds(250);
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200");

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var outcome = (await SnapshotAsync("nyxid-auth")).LastOutcome;
        outcome.ObservedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-05-21T10:05:00.250Z"));
        outcome.LatencyMs.Should().Be(250);
    }

    [Fact]
    public async Task Tick_WhenProbeTimesOut_WritesSnapshotWithoutDurableState()
    {
        const int timeoutMs = 30_000;
        var startedAt = DateTimeOffset.Parse("2026-05-21T10:10:00Z");
        _timeProvider.SetUtcNow(startedAt);
        await ConfigureAsync(NewDescriptor("nyxid-auth", timeoutMs: timeoutMs));
        _executor.WaitForCompletion = true;
        var versionBeforeTick = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await _publisher.TimeoutHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = (await SnapshotAsync("nyxid-auth")).LastOutcome;
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("timeout");
        outcome.ObservedAt.ToDateTimeOffset().Should().Be(startedAt.AddMilliseconds(timeoutMs));
        outcome.LatencyMs.Should().Be(timeoutMs);
        _agent.EventSourcing.CurrentVersion.Should().Be(versionBeforeTick);
        _scheduler.ScheduledTimeouts.Should().Be(0);

        _executor.ProbeCompletion.TrySetResult();
        await _publisher.CompletionHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tick_WhenTimedOutProbeCompletesLate_IgnoresStaleCompletion()
    {
        const int timeoutMs = 30_000;
        await ConfigureAsync(NewDescriptor("nyxid-auth", timeoutMs: timeoutMs));
        _executor.WaitForCompletion = true;
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "late_success");

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await _publisher.TimeoutHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var afterTimeout = await SnapshotAsync("nyxid-auth");

        _executor.ProbeCompletion.TrySetResult();
        await _publisher.CompletionHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var afterCompletion = await SnapshotAsync("nyxid-auth");
        afterCompletion.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        afterCompletion.LastOutcome.Detail.Should().Be("timeout");
        afterCompletion.RecentOutcomes.Should().ContainSingle();
        afterCompletion.UpdatedAt.Should().Be(afterTimeout.UpdatedAt);
    }

    [Fact]
    public async Task Tick_AfterSuccessfulCompletion_IgnoresStaleTimeout()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth", timeoutMs: 30_000));
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        var operationId = _publisher.LastCompletion!.OperationId;
        var afterCompletion = await SnapshotAsync("nyxid-auth");

        await _agent.HandleTimeoutFiredAsync(new HealthProbeTimeoutFiredEvent
        {
            Slug = "nyxid-auth",
            OperationId = operationId,
            TimeoutMs = 30_000,
            TimedOutAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow().AddSeconds(30)),
        });

        var afterTimeout = await SnapshotAsync("nyxid-auth");
        afterTimeout.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        afterTimeout.RecentOutcomes.Should().ContainSingle();
        afterTimeout.UpdatedAt.Should().Be(afterCompletion.UpdatedAt);
    }

    [Fact]
    public async Task Tick_WhileExecutionIsActive_IgnoresDuplicate()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth", timeoutMs: 30_000));
        _executor.WaitForCompletion = true;

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _executor.Invocations.Should().Be(1);
        _executor.ProbeCompletion.TrySetResult();
        await _publisher.CompletionHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Tick_RetainsAtMostOneHundredTwentyOutcomes()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        for (var i = 0; i < HealthProbeTargetGAgent.RetainedOutcomeCount + 5; i++)
        {
            _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, $"ok-{i}");
            await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        }

        var history = (await SnapshotAsync("nyxid-auth")).RecentOutcomes;
        history.Should().HaveCount(HealthProbeTargetGAgent.RetainedOutcomeCount);
        history[0].Detail.Should().Be("ok-5");
        history[^1].Detail.Should().Be("ok-124");
    }

    [Fact]
    public async Task Tick_FailureIncrementsFailuresAndKeepsLastSuccess()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "ok");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        var successAt = (await SnapshotAsync("nyxid-auth")).LastSuccessAt;

        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Down, "down");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var snapshot = await SnapshotAsync("nyxid-auth");
        snapshot.ConsecutiveFailures.Should().Be(1);
        snapshot.LastSuccessAt.Should().Be(successAt);
        snapshot.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
    }

    [Fact]
    public async Task Tick_OnUnconfiguredActor_DoesNothing()
    {
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "missing" });

        _executor.Invocations.Should().Be(0);
        (await _snapshotStore.GetAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task Tick_OnDisabledTarget_SkipsExecutor()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth", enabled: false));
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _executor.Invocations.Should().Be(0);
        (await SnapshotAsync("nyxid-auth")).LastOutcome.Should().BeNull();
    }

    [Fact]
    public async Task Tick_ExecutorThrows_WritesDownSnapshot()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _executor.ThrowOnNextProbe = new InvalidOperationException("boom");

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var outcome = (await SnapshotAsync("nyxid-auth")).LastOutcome;
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("exception");
        outcome.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task Tick_UnknownProbeKind_WritesDownSnapshot()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth", probeKind: "no_such_executor"));

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var outcome = (await SnapshotAsync("nyxid-auth")).LastOutcome;
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("unknown_probe_kind");
    }

    [Fact]
    public async Task Reactivation_ResetsOperationalHistory()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _executor.NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        (await SnapshotAsync("nyxid-auth")).RecentOutcomes.Should().ContainSingle();

        await _agent.DeactivateAsync();
        _deactivated = true;
        var (reactivated, _) = CreateAgent(_serviceProvider, ActorId);
        await reactivated.ActivateAsync();

        var snapshot = await SnapshotAsync("nyxid-auth");
        snapshot.RecentOutcomes.Should().BeEmpty();
        snapshot.LastOutcome.Should().BeNull();
        reactivated.EventSourcing!.CurrentVersion.Should().Be(1);
        await reactivated.DeactivateAsync();
    }

    [Fact]
    public async Task Activation_ReplaysLegacyEventsButExposesEmptyOperationalHistory()
    {
        const string actorId = "health-probe::legacy-probe";
        var descriptor = NewDescriptor("legacy-probe");
        _eventStore.SeedExternalEvent(actorId, new HealthProbeConfigured { Spec = descriptor });
        _eventStore.SeedExternalEvent(actorId, new HealthProbeExecutionStarted
        {
            Execution = new HealthProbeExecutionState
            {
                Slug = descriptor.Slug,
                OperationId = "legacy-operation",
                TimeoutMs = 1_000,
                StartedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            },
        });
        _eventStore.SeedExternalEvent(actorId, new HealthProbeObserved
        {
            OperationId = "legacy-operation",
            Outcome = Outcome(HealthOutcomeStatus.Down, "legacy-sample"),
        });
        var (agent, _) = CreateAgent(_serviceProvider, actorId);

        await agent.ActivateAsync();

        agent.EventSourcing!.CurrentVersion.Should().Be(3);
        agent.State.Spec.Slug.Should().Be("legacy-probe");
        agent.State.LastOutcome.Should().BeNull();
        agent.State.ActiveExecution.Should().BeNull();
        agent.State.RecentOutcomes.Should().BeEmpty();
        (await SnapshotAsync("legacy-probe")).RecentOutcomes.Should().BeEmpty();

        await agent.DeactivateAsync();
        var persisted = await _stateSnapshotStore.LoadAsync(actorId);
        persisted.Should().NotBeNull();
        persisted!.State.Spec.Slug.Should().Be("legacy-probe");
        persisted.State.LastOutcome.Should().BeNull();
        persisted.State.RecentOutcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Deactivation_CancelsDelayedSignals()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        await _agent.DeactivateAsync();
        _deactivated = true;

        _timeProvider.Advance(TimeSpan.FromHours(1));

        _publisher.TickCount.Should().Be(0);
        _publisher.TimeoutCount.Should().Be(0);
        _executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task Deactivation_CancelsActiveProbeWithoutPublishingCompletion()
    {
        await ConfigureAsync(NewDescriptor("nyxid-auth"));
        _executor.WaitForCompletion = true;
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _agent.DeactivateAsync();
        _deactivated = true;
        var executionWasCanceled = _executor.LastCancellationToken.IsCancellationRequested;
        _executor.ProbeCompletion.TrySetResult();
        await _executor.ProbeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        executionWasCanceled.Should().BeTrue();
        _publisher.LastCompletion.Should().BeNull();
        (await SnapshotAsync("nyxid-auth")).LastOutcome.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotWriteFailure_DoesNotFailProbeOrCommitSamplingEvents()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T11:00:00Z"));
        var executor = new FakeExecutor(timeProvider) { NextOutcome = Outcome(HealthOutcomeStatus.Ok, "http_200") };
        var eventStore = new InMemoryEventStore();
        var scheduler = new TrackingCallbackScheduler();
        var stateSnapshots = new RecordingEventSourcingSnapshotStore<HealthProbeTargetState>();
        await using var provider = BuildServiceProvider(
            executor,
            eventStore,
            scheduler,
            new ThrowingOperationalSnapshotStore(),
            stateSnapshots,
            timeProvider);
        var (agent, _) = CreateAgent(provider, "health-probe::snapshot-failure");
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = NewDescriptor("snapshot-failure") });
        var version = agent.EventSourcing!.CurrentVersion;

        var action = () => agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "snapshot-failure" });

        await action.Should().NotThrowAsync();
        agent.EventSourcing.CurrentVersion.Should().Be(version);
        eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(0);
        await agent.DeactivateAsync();
    }

    [Fact]
    public void Activation_PurgesLegacyCallbacksWithoutRegisteringNewOnes()
    {
        _scheduler.PurgeCalls.Should().Be(1);
        _scheduler.LastPurgedActorId.Should().Be(ActorId);
        _scheduler.ScheduledTimeouts.Should().Be(0);
        _scheduler.ScheduledTimers.Should().Be(0);
    }

    private Task ConfigureAsync(HealthProbeTargetDescriptor descriptor) =>
        _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = descriptor });

    private async Task<HealthProbeOperationalSnapshot> SnapshotAsync(string slug)
    {
        var snapshot = await _snapshotStore.GetAsync(slug);
        snapshot.Should().NotBeNull();
        return snapshot!;
    }

    private static HealthProbeOutcome Outcome(HealthOutcomeStatus status, string detail) => new()
    {
        Status = status,
        Detail = detail,
    };

    private static HealthProbeTargetDescriptor NewDescriptor(
        string slug,
        int intervalSeconds = 60,
        int timeoutMs = 1_000,
        bool enabled = true,
        string probeKind = FakeExecutor.Kind) => new()
    {
        Slug = slug,
        DisplayName = slug,
        Category = "upstream",
        ProbeKind = probeKind,
        IntervalSeconds = intervalSeconds,
        TimeoutMs = timeoutMs,
        Enabled = enabled,
    };

    private static ServiceProvider BuildServiceProvider(
        FakeExecutor executor,
        IEventStore eventStore,
        IActorRuntimeCallbackScheduler scheduler,
        IHealthProbeOperationalSnapshotStore operationalSnapshots,
        IEventSourcingSnapshotStore<HealthProbeTargetState> stateSnapshots,
        TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore);
        services.AddSingleton(new EventSourcingRuntimeOptions
        {
            SnapshotInterval = 1,
            EnableEventCompaction = false,
        });
        services.AddSingleton(stateSnapshots);
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton(scheduler);
        services.AddSingleton<IHealthProbeExecutorRegistry>(
            new HealthProbeExecutorRegistry(new IHealthProbeExecutor[] { executor }));
        services.AddSingleton(operationalSnapshots);
        services.AddSingleton(timeProvider);
        return services.BuildServiceProvider();
    }

    private static (HealthProbeTargetGAgent Agent, InlineSelfPublisher Publisher) CreateAgent(
        ServiceProvider services,
        string actorId)
    {
        var agent = new HealthProbeTargetGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<HealthProbeTargetState>>(),
        };
        var publisher = new InlineSelfPublisher(agent, actorId);
        agent.EventPublisher = publisher;
        SetActorId(agent, actorId);
        return (agent, publisher);
    }

    private static void SetActorId(GAgentBase agent, string id)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetId not found on GAgentBase");
        method.Invoke(agent, new object[] { id });
    }

    private sealed class FakeExecutor(FakeTimeProvider timeProvider) : IHealthProbeExecutor
    {
        internal const string Kind = "fake_probe";
        string IHealthProbeExecutor.Kind => Kind;

        public int Invocations { get; private set; }
        public HealthProbeOutcome NextOutcome { get; set; } = Outcome(HealthOutcomeStatus.Unknown, "unknown");
        public Exception? ThrowOnNextProbe { get; set; }
        public TimeSpan Delay { get; set; }
        public bool WaitForCompletion { get; set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HealthProbeOutcome> ProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
        {
            _ = descriptor;
            LastCancellationToken = ct;
            Invocations++;
            ProbeStarted.TrySetResult();
            try
            {
                if (ThrowOnNextProbe is { } ex)
                {
                    ThrowOnNextProbe = null;
                    throw ex;
                }

                if (WaitForCompletion)
                    await ProbeCompletion.Task.WaitAsync(ct);
                if (Delay > TimeSpan.Zero)
                    timeProvider.Advance(Delay);
                return NextOutcome.Clone();
            }
            finally
            {
                ProbeFinished.TrySetResult();
            }
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException($"version conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);
            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                !_events.TryGetValue(agentId, out var stream) || stream.Count == 0 ? 0L : stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);
            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }

        public int CountEvents(MessageDescriptor descriptor) =>
            _events.Values.Sum(stream => stream.Count(x => x.EventData.Is(descriptor)));

        public void SeedExternalEvent(string agentId, IMessage evt)
        {
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            stream.Add(new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = stream.Count == 0 ? 1 : stream[^1].Version + 1,
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                AgentId = agentId,
            });
        }
    }

    private sealed class TrackingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public int ScheduledTimeouts { get; private set; }
        public int ScheduledTimers { get; private set; }
        public int PurgeCalls { get; private set; }
        public string? LastPurgedActorId { get; private set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimeouts++;
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ScheduledTimeouts,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimers++;
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ScheduledTimers,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PurgeCalls++;
            LastPurgedActorId = actorId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventSourcingSnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly Dictionary<string, EventSourcingSnapshot<TState>> _snapshots = new(StringComparer.Ordinal);

        public Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.GetValueOrDefault(agentId) is { } snapshot
                ? new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version)
                : null);
        }

        public Task SaveAsync(
            string agentId,
            EventSourcingSnapshot<TState> snapshot,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _snapshots[agentId] = new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOperationalSnapshotStore : IHealthProbeOperationalSnapshotStore
    {
        public Task UpsertAsync(HealthProbeOperationalSnapshot snapshot, CancellationToken ct = default) =>
            throw new InvalidOperationException("snapshot write failed");

        public Task<HealthProbeOperationalSnapshot?> GetAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<HealthProbeOperationalSnapshot?>(null);
    }

    private sealed class InlineSelfPublisher(HealthProbeTargetGAgent agent, string selfActorId) : IEventPublisher
    {
        public TaskCompletionSource<HealthProbeTickRequested> TickHandled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HealthProbeTimeoutFiredEvent> TimeoutHandled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HealthProbeCompletedEvent> CompletionHandled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TickCount { get; private set; }
        public int TimeoutCount { get; private set; }
        public EnvelopeRoute? LastTickRoute { get; private set; }
        public EnvelopeRoute? LastCompletionRoute { get; private set; }
        public bool LastCompletionDelivered { get; private set; }
        public HealthProbeCompletedEvent? LastCompletion { get; private set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            return DispatchAsync(evt, EnvelopeRouteSemantics.CreateTopologyPublication(selfActorId, audience), ct);
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            return DispatchAsync(evt, EnvelopeRouteSemantics.CreateDirect(selfActorId, targetActorId), ct);
        }

        private async Task DispatchAsync(IMessage evt, EnvelopeRoute route, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var isSelf = route.GetTopologyAudience() == TopologyAudience.Self;
            switch (evt)
            {
                case HealthProbeTickRequested tick:
                    LastTickRoute = route;
                    if (!isSelf)
                        return;
                    TickCount++;
                    await agent.HandleTickAsync(tick);
                    TickHandled.TrySetResult(tick);
                    break;
                case HealthProbeTimeoutFiredEvent timeout:
                    if (!isSelf)
                        return;
                    TimeoutCount++;
                    await agent.HandleTimeoutFiredAsync(timeout);
                    TimeoutHandled.TrySetResult(timeout);
                    break;
                case HealthProbeCompletedEvent completed:
                    LastCompletion = completed.Clone();
                    LastCompletionRoute = route;
                    if (!isSelf)
                        return;
                    LastCompletionDelivered = true;
                    await agent.HandleCompletedAsync(completed);
                    CompletionHandled.TrySetResult(completed);
                    break;
            }
        }
    }
}
