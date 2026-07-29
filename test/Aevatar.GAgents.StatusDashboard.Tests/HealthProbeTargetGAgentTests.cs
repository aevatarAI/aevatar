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
    private HealthProbeTargetGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private FakeExecutor _executor = null!;
    private InMemoryEventStore _eventStore = null!;
    private TrackingCallbackScheduler _scheduler = null!;
    private FakeTimeProvider _timeProvider = null!;
    private InlineSelfPublisher _publisher = null!;

    public async Task InitializeAsync()
    {
        _executor = new FakeExecutor();
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        _executor.TimeProvider = _timeProvider;
        var registry = new HealthProbeExecutorRegistry(new IHealthProbeExecutor[] { _executor });

        var services = new ServiceCollection();
        _eventStore = new InMemoryEventStore();
        _scheduler = new TrackingCallbackScheduler();
        services.AddSingleton<IEventStore>(_eventStore);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IActorRuntimeCallbackScheduler>(_scheduler);
        services.AddSingleton<IHealthProbeExecutorRegistry>(registry);
        services.AddSingleton<TimeProvider>(_timeProvider);
        _serviceProvider = services.BuildServiceProvider();

        _agent = new HealthProbeTargetGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<HealthProbeTargetState>>(),
        };
        _publisher = new InlineSelfPublisher(_agent);
        _agent.EventPublisher = _publisher;
        SetActorId(_agent, "health-probe::test");
        await _agent.ActivateAsync();
    }

    private static void SetActorId(GAgentBase agent, string id)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetId not found on GAgentBase");
        method.Invoke(agent, new object[] { id });
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Configure_PersistsSpec()
    {
        var descriptor = NewDescriptor("nyxid-auth", intervalSeconds: 30, enabled: true);
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = descriptor });

        _agent.State.Spec.Should().NotBeNull();
        _agent.State.Spec.Slug.Should().Be("nyxid-auth");
        _agent.State.Spec.IntervalSeconds.Should().Be(30);
    }

    [Fact]
    public async Task Configure_SchedulesInitialTickFromInjectedClock()
    {
        var configuredAt = _timeProvider.GetUtcNow();

        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = NewDescriptor("nyxid-auth") });

        _scheduler.LastScheduledEvent.Should().NotBeNull();
        var tick = _scheduler.LastScheduledEvent.Should().BeOfType<HealthProbeTickRequested>().Subject;
        tick.ScheduledFor.ToDateTimeOffset().Should().Be(configuredAt.AddSeconds(1));
    }

    [Fact]
    public async Task Configure_TwiceWithSameDescriptor_DoesNotEmitDuplicateEvent()
    {
        var descriptor = NewDescriptor("nyxid-auth");
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = descriptor.Clone() });
        var versionAfterFirst = _agent.EventSourcing!.CurrentVersion;
        var scheduledAfterFirst = _scheduler.ScheduledTimeouts;
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = descriptor.Clone() });

        _agent.EventSourcing.CurrentVersion.Should().Be(versionAfterFirst,
            "identical descriptor should be a no-op — no new event committed");
        _scheduler.ScheduledTimeouts.Should().Be(scheduledAfterFirst,
            "OnActivateAsync owns restart re-arming for already-configured probes");
    }

    [Fact]
    public async Task Configure_WithChangedDescriptor_ClearsStaleOutcomes()
    {
        var descriptor = NewDescriptor("nyxid-auth");
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = descriptor.Clone() });

        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            Detail = "http_500",
        };
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        var updated = descriptor.Clone();
        updated.DisplayName = "nyxid-auth updated";
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand { Spec = updated });

        _agent.State.Spec.DisplayName.Should().Be("nyxid-auth updated");
        _agent.State.LastOutcome.Should().BeNull();
        _agent.State.LastCheckAt.Should().BeNull();
        _agent.State.LastSuccessAt.Should().BeNull();
        _agent.State.ConsecutiveFailures.Should().Be(0);
        _agent.State.RecentOutcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_InvokesExecutor_AndPersistsObservedOutcome()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
        };
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _executor.Invocations.Should().Be(1);
        _agent.State.LastOutcome.Should().NotBeNull();
        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        _agent.State.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Tick_SuccessfulProbe_CommitsOneTerminalEventAndClearsExecution()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });
        var versionBeforeTick = _agent.EventSourcing!.CurrentVersion;

        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
        };
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _agent.EventSourcing.CurrentVersion.Should().Be(versionBeforeTick + 2,
            "one execution-started event and one terminal observation are sufficient for a probe turn");
        _agent.State.ActiveExecution.Should().BeNull();
        _eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(1);
    }

    [Fact]
    public async Task Tick_SuccessfulProbe_DeliversCompletionThroughSelfHandlingGate()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
        };

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        // Regression guard for 298fb1355: HandleCompletedAsync is OnlySelfHandling, so the
        // completion must be published with a Self topology route. SendToAsync builds a Direct
        // route that the dispatch gate drops, stranding every probe on the timeout path forever.
        _publisher.LastCompletionRoute.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        _publisher.LastCompletionDelivered.Should().BeTrue();
        _agent.State.LastOutcome.Should().NotBeNull();
        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
    }

    [Fact]
    public async Task Tick_StampsObservedAtAndLatencyFromInjectedClock()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        _timeProvider.SetUtcNow(DateTimeOffset.Parse("2026-05-21T10:05:00Z"));
        _executor.Delay = TimeSpan.FromMilliseconds(250);
        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2001-01-01T00:00:00Z")),
        };

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _agent.State.LastOutcome.ObservedAt.ToDateTimeOffset()
            .Should().Be(DateTimeOffset.Parse("2026-05-21T10:05:00.250Z"));
        _agent.State.LastOutcome.LatencyMs.Should().Be(250);
    }

    [Fact]
    public async Task Tick_WhenProbeExceedsTimeout_UsesInjectedClockForCancellationAndOutcome()
    {
        const int timeoutBudgetMs = 30_000;
        var startedAt = DateTimeOffset.Parse("2026-05-21T10:10:00Z");
        _timeProvider.SetUtcNow(startedAt);
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth", timeoutMs: timeoutBudgetMs),
        });

        _executor.WaitForCancellation = true;
        var tickTask = _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var timeout = _scheduler.ScheduledEvents.OfType<HealthProbeTimeoutFiredEvent>().Single();
        await _agent.HandleTimeoutFiredAsync(timeout);

        _executor.Invocations.Should().Be(1);
        _agent.State.LastOutcome.Should().NotBeNull();
        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        _agent.State.LastOutcome.Detail.Should().Be("timeout");
        _agent.State.LastOutcome.ObservedAt.ToDateTimeOffset().Should().Be(startedAt.AddMilliseconds(timeoutBudgetMs));
        _agent.State.LastOutcome.LatencyMs.Should().Be(timeoutBudgetMs);
        tickTask.IsCompleted.Should().BeTrue("the tick turn only starts the actor-owned probe operation");
    }

    [Fact]
    public async Task Tick_WhenTimedOutProbeCompletesLate_IgnoresStaleCompletion()
    {
        const int timeoutBudgetMs = 30_000;
        var startedAt = DateTimeOffset.Parse("2026-05-21T10:12:00Z");
        _timeProvider.SetUtcNow(startedAt);
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth", timeoutMs: timeoutBudgetMs),
        });

        _executor.WaitForCancellation = true;
        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "late_success",
        };

        var tickTask = _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        await _executor.ProbeStarted.Task;

        var timeout = _scheduler.ScheduledEvents.OfType<HealthProbeTimeoutFiredEvent>().Single();
        await _agent.HandleTimeoutFiredAsync(timeout);

        var scheduledAfterTimeout = _scheduler.ScheduledTimeouts;
        var observedAfterTimeout = _eventStore.CountEvents(HealthProbeObserved.Descriptor);

        _agent.State.LastOutcome.Should().NotBeNull();
        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        _agent.State.LastOutcome.Detail.Should().Be("timeout");
        _agent.State.ActiveExecution.Should().BeNull();

        _executor.ProbeCompletion.SetResult();
        await _publisher.CompletionSettled.Task;
        await tickTask;

        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        _agent.State.LastOutcome.Detail.Should().Be("timeout");
        _agent.State.ActiveExecution.Should().BeNull();
        _eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(observedAfterTimeout,
            "a stale late completion must not publish a second terminal outcome");
        _agent.State.RecentOutcomes.Should().ContainSingle();
        _scheduler.ScheduledTimeouts.Should().Be(scheduledAfterTimeout,
            "a stale late completion must not schedule another next tick");
    }

    [Fact]
    public async Task Tick_RetainsRecentTwoHourOutcomeWindow()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        for (var i = 0; i < HealthProbeTargetGAgent.RetainedOutcomeCount + 5; i++)
        {
            _executor.NextOutcome = new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Ok,
                Detail = $"ok-{i}",
            };
            await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        }

        _agent.State.RecentOutcomes.Should().HaveCount(HealthProbeTargetGAgent.RetainedOutcomeCount);
        _agent.State.RecentOutcomes[0].Detail.Should().Be("ok-5");
        _agent.State.RecentOutcomes[^1].Detail.Should().Be("ok-124");
    }

    [Fact]
    public async Task Tick_FailureIncrementsConsecutiveFailures_AndKeepsLastSuccess()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        _executor.NextOutcome = new HealthProbeOutcome { Status = HealthOutcomeStatus.Ok };
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        var firstSuccessAt = _agent.State.LastSuccessAt;

        _executor.NextOutcome = new HealthProbeOutcome { Status = HealthOutcomeStatus.Down };
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _agent.State.ConsecutiveFailures.Should().Be(1);
        _agent.State.LastSuccessAt.Should().Be(firstSuccessAt);
        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
    }

    [Fact]
    public async Task Tick_OnUnconfiguredActor_NoOp()
    {
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "missing" });
        _executor.Invocations.Should().Be(0);
        _agent.State.LastOutcome.Should().BeNull();
    }

    [Fact]
    public async Task Tick_OnDisabledTarget_SkipsExecutor()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth", enabled: false),
        });

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });
        _executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task Tick_ExecutorThrows_PersistsDownOutcome()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        _executor.ThrowOnNextProbe = new InvalidOperationException("boom");
        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        _agent.State.LastOutcome.Detail.Should().Be("exception");
        _agent.State.LastOutcome.ErrorMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task Tick_WhenObservedCommitHitsOptimisticConcurrencyConflict_RearmsNextTick()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth"),
        });

        var scheduledBeforeTick = _scheduler.ScheduledTimeouts;
        _eventStore.ThrowOptimisticConcurrencyOnNextObservedAppend = true;
        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
        };

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _executor.Invocations.Should().Be(1);
        _scheduler.ScheduledTimeouts.Should().Be(scheduledBeforeTick + 2,
            "a transient duplicate tick race must not kill the probe schedule");
    }

    [Fact]
    public async Task Tick_UnknownProbeKind_PersistsDownOutcome()
    {
        await _agent.HandleConfigureAsync(new HealthProbeConfigureCommand
        {
            Spec = NewDescriptor("nyxid-auth", probeKind: "no_such_executor"),
        });

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _agent.State.LastOutcome.Status.Should().Be(HealthOutcomeStatus.Down);
        _agent.State.LastOutcome.Detail.Should().Be("unknown_probe_kind");
    }

    private static HealthProbeTargetDescriptor NewDescriptor(
        string slug,
        int intervalSeconds = 60,
        int timeoutMs = 1_000,
        bool enabled = true,
        string probeKind = FakeExecutor.Kind)
    {
        var d = new HealthProbeTargetDescriptor
        {
            Slug = slug,
            DisplayName = slug,
            Category = "upstream",
            ProbeKind = probeKind,
            IntervalSeconds = intervalSeconds,
            TimeoutMs = timeoutMs,
            Enabled = enabled,
        };
        return d;
    }

    private sealed class FakeExecutor : IHealthProbeExecutor
    {
        internal const string Kind = "fake_probe";
        string IHealthProbeExecutor.Kind => Kind;

        public int Invocations { get; private set; }
        public HealthProbeOutcome NextOutcome { get; set; } = new() { Status = HealthOutcomeStatus.Unknown };
        public Exception? ThrowOnNextProbe { get; set; }
        public FakeTimeProvider TimeProvider { get; set; } = null!;
        public TimeSpan Delay { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HealthProbeOutcome> ProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
        {
            Invocations++;
            ProbeStarted.TrySetResult();
            if (ThrowOnNextProbe is { } ex)
            {
                ThrowOnNextProbe = null;
                throw ex;
            }
            if (WaitForCancellation)
                await ProbeCompletion.Task;
            if (Delay > TimeSpan.Zero)
            {
                TimeProvider.Advance(Delay);
            }
            return NextOutcome;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public bool ThrowOptimisticConcurrencyOnNextObservedAppend { get; set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId, IEnumerable<StateEvent> events, long expectedVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (ThrowOptimisticConcurrencyOnNextObservedAppend &&
                events.Any(x => x.EventData.Is(HealthProbeObserved.Descriptor)))
            {
                ThrowOptimisticConcurrencyOnNextObservedAppend = false;
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion + 1);
            }
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
            string agentId, long? fromVersion = null, CancellationToken ct = default)
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
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public int CountEvents(MessageDescriptor descriptor) =>
            _events.Values.Sum(stream => stream.Count(x => x.EventData.Is(descriptor)));

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);
            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }

    private sealed class TrackingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public int ScheduledTimeouts { get; private set; }
        public IMessage? LastScheduledEvent { get; private set; }
        public List<IMessage> ScheduledEvents { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            ScheduledTimeouts++;
            if (request.TriggerEnvelope.Payload.Is(HealthProbeTimeoutFiredEvent.Descriptor))
                LastScheduledEvent = request.TriggerEnvelope.Payload.Unpack<HealthProbeTimeoutFiredEvent>();
            else
                LastScheduledEvent = request.TriggerEnvelope.Payload.Unpack<HealthProbeTickRequested>();
            ScheduledEvents.Add(LastScheduledEvent);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ScheduledTimeouts,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, 1, RuntimeCallbackBackend.InMemory));
        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Models the real OnlySelfHandling dispatch gate instead of blindly invoking the handler.
    // HandleCompletedAsync is [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)], so
    // the runtime (StaticHandlerAdapter) only invokes it when the envelope route resolves to
    // TopologyAudience.Self. A Direct route — what SendToAsync builds — resolves to Unspecified and
    // is silently dropped; that is exactly the 298fb1355 regression that stranded every probe on the
    // timeout path. Routing self events through this gate makes any Direct-routed completion fail loudly.
    private sealed class InlineSelfPublisher(HealthProbeTargetGAgent agent) : IEventPublisher
    {
        private const string SelfActorId = "health-probe::test";

        public TaskCompletionSource<HealthProbeCompletedEvent> CompletedHandled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completes whether the completion was delivered or dropped, so tests can await the
        // settlement without hanging when the gate (correctly) drops a misrouted completion.
        public TaskCompletionSource CompletionSettled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EnvelopeRoute? LastCompletionRoute { get; private set; }

        public bool LastCompletionDelivered { get; private set; }

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
            return DispatchAsync(evt, EnvelopeRouteSemantics.CreateTopologyPublication(SelfActorId, audience), ct);
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
            return DispatchAsync(evt, EnvelopeRouteSemantics.CreateDirect(SelfActorId, targetActorId), ct);
        }

        private async Task DispatchAsync(IMessage evt, EnvelopeRoute route, CancellationToken ct)
        {
            _ = ct;
            switch (evt)
            {
                case HealthProbeCompletedEvent completed:
                    LastCompletionRoute = route;
                    if (route.GetTopologyAudience() == TopologyAudience.Self)
                    {
                        LastCompletionDelivered = true;
                        await agent.HandleCompletedAsync(completed);
                        CompletedHandled.TrySetResult(completed);
                    }

                    CompletionSettled.TrySetResult();
                    break;
            }
        }
    }
}
