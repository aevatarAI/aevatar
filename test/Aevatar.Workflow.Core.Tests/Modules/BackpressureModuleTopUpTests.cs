using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class BackpressureModuleTopUpTests
{
    [Fact]
    public async Task ParallelFanOutModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new ParallelFanOutModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-parallel-floor",
                Input = "payload",
                TargetRole = "worker",
                Parameters =
                {
                    ["parallel_count"] = "4",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("fanout_sub_0", "fanout_sub_1");
        context.Published.Select(x => x.Event).OfType<BackpressureAppliedEvent>().Should().ContainSingle()
            .Which.QueuedCount.Should().Be(1);

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "fanout_sub_0",
                RunId = "run-parallel-floor",
                Success = true,
                Output = "done-0",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("fanout_sub_2");
    }

    [Fact]
    public async Task ForEachModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new ForEachModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-floor",
                StepType = "foreach",
                RunId = "run-foreach-floor",
                Input = "alpha\n---\nbeta\n---\ngamma\n---\ndelta",
                Parameters =
                {
                    ["sub_step_type"] = "transform",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-floor_item_0", "foreach-floor_item_1");

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-floor_item_0",
                RunId = "run-foreach-floor",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("foreach-floor_item_2");
    }

    [Fact]
    public async Task MapReduceModule_ShouldHonorMinConcurrentWorkersAndTopUp()
    {
        var module = new MapReduceModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "map-floor",
                StepType = "map_reduce",
                RunId = "run-map-floor",
                Input = "alpha\n---\nbeta\n---\ngamma\n---\ndelta",
                Parameters =
                {
                    ["map_step_type"] = "transform",
                    ["min_concurrent_workers"] = "2",
                    ["max_concurrent_workers"] = "4",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("map-floor_map_0", "map-floor_map_1");

        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "map-floor_map_0",
                RunId = "run-map-floor",
                Success = true,
                Output = "A",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(x => x.Event).OfType<StepRequestEvent>().Select(x => x.StepId)
            .Should().Equal("map-floor_map_2");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldAllowExtendedLongTimeoutWindow()
    {
        var module = new WaitSignalModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-long",
                StepType = "wait_signal",
                RunId = "run-long-wait",
                Input = "fallback",
                Parameters =
                {
                    ["signal_name"] = "codex_worker_done",
                    ["timeout_ms"] = "5400000",
                },
            }),
            context,
            CancellationToken.None);

        var waiting = context.Published.Select(x => x.Event).OfType<WaitingForSignalEvent>().Single();
        waiting.TimeoutMs.Should().Be(5_400_000);

        var scheduled = context.Scheduled.Should().ContainSingle().Subject;
        scheduled.DueTime.Should().Be(TimeSpan.FromMilliseconds(5_400_000));
        scheduled.Event.Should().BeOfType<WaitSignalTimeoutFiredEvent>().Which.TimeoutMs.Should().Be(5_400_000);
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _callbackGenerations = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "workflow-agent";

        public string RunId => "workflow-run";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<ScheduledCallback> Scheduled { get; } = [];

        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            _ = startingTimestamp;
            return TimeSpan.Zero;
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            var generation = _callbackGenerations.GetValueOrDefault(callbackId, 0) + 1;
            _callbackGenerations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, dueTime, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed record ScheduledCallback(string CallbackId, long Generation, TimeSpan DueTime, IMessage Event);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
