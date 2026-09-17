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

public sealed class RaceModuleDeterministicWorkerTests
{
    [Fact]
    public async Task HandleAsync_WithDeterministicSubStep_ShouldSelectFirstSuccessAndIgnoreLaterCompletions()
    {
        var module = new RaceModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "race",
                StepType = "race",
                RunId = "run-1",
                Input = "synthetic",
                Parameters =
                {
                    ["count"] = "3",
                    ["sub_step_type"] = "assign",
                    ["sub_param_target"] = "worker_${index}",
                    ["sub_param_value"] = "result-${index}",
                },
            }),
            ctx,
            CancellationToken.None);

        var requests = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        requests.Should().HaveCount(3);
        requests.Select(x => x.StepType).Should().OnlyContain(x => x == "assign");
        requests.Select(x => x.Parameters["target"]).Should().Equal("worker_0", "worker_1", "worker_2");
        requests.Select(x => x.Parameters["value"]).Should().Equal("result-0", "result-1", "result-2");

        ctx.Published.Clear();
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "race_race_1", RunId = "run-1", Success = true, Output = "result-1" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "race_race_0", RunId = "run-1", Success = true, Output = "result-0" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "race_race_2", RunId = "run-1", Success = true, Output = "result-2" }), ctx, CancellationToken.None);

        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("race");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("result-1");
        completed.Annotations["race.winner"].Should().Be("race_race_1");
        ctx.LoadState<RaceModuleState>("race").Races.Should().BeEmpty();
    }

    private static EventEnvelope Envelope(IMessage message) => new() { Payload = Any.Pack(message) };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new();
        public string AgentId => "agent-1";
        public string RunId => "run-1";
        public IServiceProvider Services => EmptyServiceProvider.Instance;
        public ILogger Logger { get; } = NullLogger.Instance;
        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var state) || !state.Is(new TState().Descriptor))
                return new TState();

            return state.Unpack<TState>() ?? new TState();
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
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(System.Type serviceType) => null;
    }
}
