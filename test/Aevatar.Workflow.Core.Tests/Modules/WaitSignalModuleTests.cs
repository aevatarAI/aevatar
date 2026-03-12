using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
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

public sealed class WaitSignalModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenSameRunAndSignalHaveMultipleWaiters_ShouldRequireStepIdForPreciseResume()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-1"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-a",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-a",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-b",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-b",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);
        context.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                Payload = "ambiguous",
            }),
            context,
            CancellationToken.None);
        context.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-shared",
                SignalName = "approval",
                StepId = "wait-b",
                Payload = "resolved-b",
            }),
            context,
            CancellationToken.None);

        var completion = context.Published.Select(item => item.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("wait-b");
        completion.RunId.Should().Be("run-shared");
        completion.Output.Should().Be("resolved-b");
    }

    [Fact]
    public async Task HandleAsync_WhenSignalArrivesBeforeWaitStep_ShouldBufferAndConsumeOnActivation()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-early"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-early",
                StepId = "wait-approval",
                SignalName = "approval",
                Payload = "early-payload",
            }),
            context,
            CancellationToken.None);

        context.Published.Select(item => item.Event).OfType<WorkflowSignalBufferedEvent>().Should().ContainSingle();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-early",
                Input = "fallback",
                Parameters = { ["signal_name"] = "approval" },
            }),
            context,
            CancellationToken.None);

        var completion = context.Published.Select(item => item.Event).OfType<StepCompletedEvent>().Last();
        completion.Success.Should().BeTrue();
        completion.RunId.Should().Be("run-early");
        completion.StepId.Should().Be("wait-approval");
        completion.Output.Should().Be("early-payload");

        context.Published.Select(item => item.Event).OfType<WaitingForSignalEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenSignalMissingStepId_ShouldNotBuffer()
    {
        var module = new WaitSignalModule();
        var context = new RecordingEventHandlerContext(
            new EmptyServiceProvider(),
            new StubAgent("workflow-early"),
            NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                RunId = "run-no-step",
                SignalName = "approval",
                Payload = "payload",
            }),
            context,
            CancellationToken.None);

        context.Published.Should().BeEmpty();
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = new EnvelopeRoute
            {
                PublisherActorId = "test",
                Direction = EventDirection.Self,
            },
        };
    }

    private sealed class RecordingEventHandlerContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage Event, EventDirection Direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }
        public string RunId => AgentId;

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new()
        {
            var states = new List<KeyValuePair<string, TState>>();
            foreach (var (scopeKey, packed) in _states)
            {
                if (!string.IsNullOrEmpty(scopeKeyPrefix) &&
                    !scopeKey.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!packed.Is(new TState().Descriptor))
                    continue;

                states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
            }

            return states;
        }

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = options;
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
