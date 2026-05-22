using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthProbeTargetGAgentTests : IAsyncLifetime
{
    private HealthProbeTargetGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private FakeExecutor _executor = null!;
    private InMemoryEventStore _eventStore = null!;
    private TrackingCallbackScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        _executor = new FakeExecutor();
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
        _serviceProvider = services.BuildServiceProvider();

        _agent = new HealthProbeTargetGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<HealthProbeTargetState>>(),
        };
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
        _eventStore.ThrowOptimisticConcurrencyOnNextAppend = true;
        _executor.NextOutcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = "http_200",
        };

        await _agent.HandleTickAsync(new HealthProbeTickRequested { Slug = "nyxid-auth" });

        _executor.Invocations.Should().Be(1);
        _scheduler.ScheduledTimeouts.Should().Be(scheduledBeforeTick + 1,
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

        public Task<HealthProbeOutcome> ProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
        {
            Invocations++;
            if (ThrowOnNextProbe is { } ex)
            {
                ThrowOnNextProbe = null;
                throw ex;
            }
            return Task.FromResult(NextOutcome);
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public bool ThrowOptimisticConcurrencyOnNextAppend { get; set; }

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
            if (ThrowOptimisticConcurrencyOnNextAppend)
            {
                ThrowOptimisticConcurrencyOnNextAppend = false;
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

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            ScheduledTimeouts++;
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
}
