using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionContextAdapterTests
{
    [Fact]
    public void Create_ShouldValidateArguments()
    {
        var inner = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();

        FluentActions.Invoking(() => WorkflowExecutionContextAdapter.Create(null!, host))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowExecutionContextAdapter.Create(inner, null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void LoadState_ShouldReturnSavedValue_AndFallbackToDefault()
    {
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            new RecordingStateHost
            {
                States =
                {
                    ["matched"] = Any.Pack(new StringValue { Value = "ready" }),
                    ["mismatch"] = Any.Pack(new Int32Value { Value = 7 }),
                },
            });

        adapter.LoadState<StringValue>("matched").Value.Should().Be("ready");
        adapter.LoadState<StringValue>("missing").Value.Should().BeEmpty();
        adapter.LoadState<StringValue>("mismatch").Value.Should().BeEmpty();

        FluentActions.Invoking(() => adapter.LoadState<StringValue>(" "))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void LoadStates_ShouldFilterByPrefix_AndPayloadType()
    {
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            new RecordingStateHost
            {
                States =
                {
                    ["scope.alpha"] = Any.Pack(new StringValue { Value = "a" }),
                    ["scope.beta"] = Any.Pack(new StringValue { Value = "b" }),
                    ["scope.gamma"] = Any.Pack(new Int32Value { Value = 3 }),
                    ["other.delta"] = Any.Pack(new StringValue { Value = "d" }),
                },
            });

        var scoped = adapter.LoadStates<StringValue>("scope.");
        scoped.Should().HaveCount(2);
        scoped.Select(x => x.Key).Should().BeEquivalentTo("scope.alpha", "scope.beta");
        scoped.Select(x => x.Value.Value).Should().BeEquivalentTo("a", "b");

        var all = adapter.LoadStates<StringValue>();
        all.Should().HaveCount(3);
        all.Select(x => x.Key).Should().Contain("other.delta");
    }

    [Fact]
    public async Task SaveAndClearState_ShouldValidateArguments_AndPersistThroughStateHost()
    {
        var stateHost = new RecordingStateHost();
        var adapter = WorkflowExecutionContextAdapter.Create(
            new RecordingEventHandlerContext(),
            stateHost);

        await adapter.SaveStateAsync("scope.a", new StringValue { Value = "saved" }, CancellationToken.None);
        stateHost.States["scope.a"].Unpack<StringValue>().Value.Should().Be("saved");

        await adapter.ClearStateAsync("scope.a", CancellationToken.None);
        stateHost.States.Should().NotContainKey("scope.a");

        var saveWithBlankScope = () => adapter.SaveStateAsync(" ", new StringValue(), CancellationToken.None);
        await saveWithBlankScope.Should().ThrowAsync<ArgumentException>();

        var saveWithNullState = () => adapter.SaveStateAsync<StringValue>("scope.b", null!, CancellationToken.None);
        await saveWithNullState.Should().ThrowAsync<ArgumentNullException>();

        var clearWithBlankScope = () => adapter.ClearStateAsync(" ", CancellationToken.None);
        await clearWithBlankScope.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ForwardingApis_ShouldDelegateToInnerContext()
    {
        var inner = new RecordingEventHandlerContext();
        var adapter = WorkflowExecutionContextAdapter.Create(inner, new RecordingStateHost { RunId = "run-42" });
        var timeoutEvent = new Empty();
        var timerEvent = new StringValue { Value = "tick" };
        var cancelLease = new RuntimeCallbackLease("agent-1", "cancel-me", 7, RuntimeCallbackBackend.InMemory);

        adapter.AgentId.Should().Be("agent-1");
        adapter.RunId.Should().Be("run-42");
        adapter.InboundEnvelope.Should().BeSameAs(inner.InboundEnvelope);
        adapter.Services.Should().BeSameAs(inner.Services);
        adapter.Logger.Should().BeSameAs(inner.Logger);

        await adapter.PublishAsync(new StringValue { Value = "published" }, EventDirection.Self, CancellationToken.None);
        await adapter.SendToAsync("child-1", new Int32Value { Value = 3 }, CancellationToken.None);

        var timeoutLease = await adapter.ScheduleSelfDurableTimeoutAsync(
            "timeout-1",
            TimeSpan.FromSeconds(5),
            timeoutEvent,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    Baggage = { ["mode"] = "timeout" },
                },
            },
            CancellationToken.None);
        var timerLease = await adapter.ScheduleSelfDurableTimerAsync(
            "timer-1",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            timerEvent,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    Baggage = { ["mode"] = "timer" },
                },
            },
            CancellationToken.None);
        await adapter.CancelDurableCallbackAsync(cancelLease, CancellationToken.None);

        inner.Published.Should().ContainSingle(x =>
            x.Direction == EventDirection.Self &&
            x.Event.Unpack<StringValue>().Value == "published");
        inner.Sent.Should().ContainSingle(x =>
            x.TargetActorId == "child-1" &&
            x.Event.Unpack<Int32Value>().Value == 3);
        timeoutLease.CallbackId.Should().Be("timeout-1");
        timerLease.CallbackId.Should().Be("timer-1");
        inner.ScheduledTimeouts.Should().ContainSingle(x => x.CallbackId == "timeout-1");
        inner.ScheduledTimers.Should().ContainSingle(x => x.CallbackId == "timer-1");
        inner.Canceled.Should().ContainSingle(x => x.CallbackId == "cancel-me");
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(Any Event, EventDirection Direction)> Published { get; } = [];

        public List<(string TargetActorId, Any Event)> Sent { get; } = [];

        public List<RecordedCallback> ScheduledTimeouts { get; } = [];

        public List<RecordedTimer> ScheduledTimers { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((targetActorId, Any.Pack(evt)));
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
            ScheduledTimeouts.Add(new RecordedCallback(callbackId, dueTime, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimers.Add(new RecordedTimer(callbackId, dueTime, period, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 2, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; set; } = "run-1";

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(global::System.Type serviceType) => null;
    }

    private sealed record RecordedCallback(
        string CallbackId,
        TimeSpan DueTime,
        Any Event,
        EventEnvelopePublishOptions? Options);

    private sealed record RecordedTimer(
        string CallbackId,
        TimeSpan DueTime,
        TimeSpan Period,
        Any Event,
        EventEnvelopePublishOptions? Options);
}
