using System.Collections.Concurrent;
using System.Text.Json;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.CQRS.Sagas.Core.Tests;

public class SagaRuntimeTests
{
    [Fact]
    public async Task LifecycleSaga_ShouldTrackLifecycleByCorrelationId()
    {
        var repository = new InMemorySagaRepository();
        var commandEmitter = new CapturingCommandEmitter();
        var timeoutScheduler = new CapturingTimeoutScheduler();
        var runtime = CreateRuntime(
            [new LifecycleSaga()],
            repository,
            commandEmitter,
            timeoutScheduler);

        const string correlationId = "cmd-1001";
        await runtime.ObserveAsync("actor-root", Wrap(new StartWorkflowEvent { WorkflowName = "demo" }, correlationId));
        await runtime.ObserveAsync("actor-root", Wrap(new StepRequestEvent { StepId = "s1", StepType = "llm_call" }, correlationId));
        await runtime.ObserveAsync("actor-root", Wrap(new StepCompletedEvent { StepId = "s1", Success = true }, correlationId));
        await runtime.ObserveAsync("actor-root", Wrap(new WorkflowCompletedEvent { WorkflowName = "demo", Success = true }, correlationId));

        var state = await repository.LoadAsync<LifecycleSagaState>(LifecycleSaga.NameValue, correlationId);

        state.Should().NotBeNull();
        state!.WorkflowName.Should().Be("demo");
        state.RequestedSteps.Should().Be(1);
        state.CompletedSteps.Should().Be(1);
        state.FailedSteps.Should().Be(0);
        state.IsCompleted.Should().BeTrue();
        state.Success.Should().BeTrue();
        commandEmitter.EnqueueActions.Should().BeEmpty();
        timeoutScheduler.TimeoutActions.Should().BeEmpty();
    }

    [Fact]
    public async Task SagaRuntime_ShouldDispatchCommandAndTimeoutActions()
    {
        var repository = new InMemorySagaRepository();
        var commandEmitter = new CapturingCommandEmitter();
        var timeoutScheduler = new CapturingTimeoutScheduler();
        var runtime = CreateRuntime(
            [new ActionSaga()],
            repository,
            commandEmitter,
            timeoutScheduler);

        const string correlationId = "cmd-2001";
        await runtime.ObserveAsync("actor-x", Wrap(new StartWorkflowEvent { WorkflowName = "demo" }, correlationId));

        var state = await repository.LoadAsync<ActionSagaState>("action_saga", correlationId);
        state.Should().NotBeNull();
        state!.IsCompleted.Should().BeTrue();

        commandEmitter.EnqueueActions.Should().HaveCount(1);
        commandEmitter.ScheduleActions.Should().HaveCount(1);
        timeoutScheduler.TimeoutActions.Should().HaveCount(1);
    }

    private static SagaRuntime CreateRuntime(
        IReadOnlyList<ISaga> sagas,
        ISagaRepository repository,
        ISagaCommandEmitter commandEmitter,
        ISagaTimeoutScheduler timeoutScheduler)
    {
        return new SagaRuntime(
            sagas,
            repository,
            new Aevatar.CQRS.Sagas.Core.Runtime.DefaultSagaCorrelationResolver(),
            commandEmitter,
            timeoutScheduler,
            Options.Create(new SagaRuntimeOptions()),
            NullLogger<SagaRuntime>.Instance);
    }

    private static EventEnvelope Wrap(IMessage payload, string correlationId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(payload),
        PublisherId = "test.publisher",
        Direction = EventDirection.Self,
        CorrelationId = correlationId,
    };

    private sealed class InMemorySagaRepository : ISagaRepository
    {
        private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);
        private readonly JsonSerializerOptions _jsonOptions = new();

        public async Task<ISagaState?> LoadAsync(string sagaName, string correlationId, Type stateType, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_states.TryGetValue(Key(sagaName, correlationId), out var json))
                return null;

            return (ISagaState?)JsonSerializer.Deserialize(json, stateType, _jsonOptions);
        }

        public Task SaveAsync(
            string sagaName,
            ISagaState state,
            Type stateType,
            int? expectedVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (expectedVersion.HasValue)
            {
                if (_states.TryGetValue(Key(sagaName, state.CorrelationId), out var existingJson))
                {
                    var existingState = (ISagaState?)JsonSerializer.Deserialize(existingJson, stateType, _jsonOptions);
                    var existingVersion = existingState?.Version ?? -1;
                    if (existingVersion != expectedVersion.Value)
                    {
                        throw new SagaConcurrencyException(
                            $"Version mismatch. expected={expectedVersion.Value}, actual={existingVersion}");
                    }
                }
                else if (expectedVersion.Value != -1)
                {
                    throw new SagaConcurrencyException(
                        $"Version mismatch. expected={expectedVersion.Value}, actual=-1");
                }
            }

            var json = JsonSerializer.Serialize(state, stateType, _jsonOptions);
            _states[Key(sagaName, state.CorrelationId)] = json;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ISagaState>> ListAsync(string sagaName, Type stateType, int take = 100, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = $"{sagaName}:";
            var boundedTake = Math.Clamp(take, 1, 1000);

            var items = _states
                .Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Take(boundedTake)
                .Select(x => (ISagaState?)JsonSerializer.Deserialize(x.Value, stateType, _jsonOptions))
                .Where(x => x != null)
                .Cast<ISagaState>()
                .ToList();

            return Task.FromResult<IReadOnlyList<ISagaState>>(items);
        }

        public async Task<TState?> LoadAsync<TState>(string sagaName, string correlationId)
            where TState : class, ISagaState
        {
            return await LoadAsync(sagaName, correlationId, typeof(TState)) as TState;
        }

        private static string Key(string sagaName, string correlationId) => $"{sagaName}:{correlationId}";
    }

    private sealed class CapturingCommandEmitter : ISagaCommandEmitter
    {
        public List<SagaEnqueueCommandAction> EnqueueActions { get; } = [];

        public List<SagaScheduleCommandAction> ScheduleActions { get; } = [];

        public Task EnqueueAsync(SagaEnqueueCommandAction action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnqueueActions.Add(action);
            return Task.CompletedTask;
        }

        public Task ScheduleAsync(SagaScheduleCommandAction action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduleActions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingTimeoutScheduler : ISagaTimeoutScheduler
    {
        public List<(string SagaName, string CorrelationId, string ActorId, SagaScheduleTimeoutAction Action)> TimeoutActions { get; } = [];

        public Task ScheduleAsync(
            string sagaName,
            string correlationId,
            string actorId,
            SagaScheduleTimeoutAction action,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutActions.Add((sagaName, correlationId, actorId, action));
            return Task.CompletedTask;
        }
    }

    private sealed class ActionSagaState : SagaStateBase
    {
    }

    private sealed class LifecycleSagaState : SagaStateBase
    {
        public string WorkflowName { get; set; } = string.Empty;
        public int RequestedSteps { get; set; }
        public int CompletedSteps { get; set; }
        public int FailedSteps { get; set; }
        public bool? Success { get; set; }
    }

    private sealed class LifecycleSaga : SagaBase<LifecycleSagaState>
    {
        private static readonly string StartWorkflowTypeUrl = Any.Pack(new StartWorkflowEvent()).TypeUrl;
        private static readonly string StepRequestTypeUrl = Any.Pack(new StepRequestEvent()).TypeUrl;
        private static readonly string StepCompletedTypeUrl = Any.Pack(new StepCompletedEvent()).TypeUrl;
        private static readonly string WorkflowCompletedTypeUrl = Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

        public const string NameValue = "lifecycle_saga";
        public override string Name => NameValue;

        public override ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            var typeUrl = envelope.Payload?.TypeUrl;
            var canHandle = string.Equals(typeUrl, StartWorkflowTypeUrl, StringComparison.Ordinal) ||
                            string.Equals(typeUrl, StepRequestTypeUrl, StringComparison.Ordinal) ||
                            string.Equals(typeUrl, StepCompletedTypeUrl, StringComparison.Ordinal) ||
                            string.Equals(typeUrl, WorkflowCompletedTypeUrl, StringComparison.Ordinal);
            return ValueTask.FromResult(canHandle);
        }

        public override ValueTask<bool> CanStartAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult(
                string.Equals(envelope.Payload?.TypeUrl, StartWorkflowTypeUrl, StringComparison.Ordinal));
        }

        protected override ValueTask HandleAsync(
            LifecycleSagaState state,
            EventEnvelope envelope,
            ISagaActionSink actions,
            CancellationToken ct = default)
        {
            _ = ct;
            var payload = envelope.Payload;
            if (payload == null)
                return ValueTask.CompletedTask;

            if (payload.Is(StartWorkflowEvent.Descriptor))
            {
                var evt = payload.Unpack<StartWorkflowEvent>();
                state.WorkflowName = evt.WorkflowName;
                return ValueTask.CompletedTask;
            }

            if (payload.Is(StepRequestEvent.Descriptor))
            {
                state.RequestedSteps++;
                return ValueTask.CompletedTask;
            }

            if (payload.Is(StepCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<StepCompletedEvent>();
                state.CompletedSteps++;
                if (!evt.Success)
                    state.FailedSteps++;
                return ValueTask.CompletedTask;
            }

            if (payload.Is(WorkflowCompletedEvent.Descriptor))
            {
                var evt = payload.Unpack<WorkflowCompletedEvent>();
                state.Success = evt.Success;
                actions.MarkCompleted();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ActionSaga : SagaBase<ActionSagaState>
    {
        private static readonly string StartWorkflowTypeUrl = Any.Pack(new StartWorkflowEvent()).TypeUrl;

        public override string Name => "action_saga";

        public override ValueTask<bool> CanHandleAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult(string.Equals(envelope.Payload?.TypeUrl, StartWorkflowTypeUrl, StringComparison.Ordinal));
        }

        protected override ValueTask HandleAsync(
            ActionSagaState state,
            EventEnvelope envelope,
            ISagaActionSink actions,
            CancellationToken ct = default)
        {
            _ = state;
            _ = envelope;
            _ = ct;

            actions.EnqueueCommand("workflow", new DummyCommand("enqueue"));
            actions.ScheduleCommand("workflow", new DummyCommand("schedule"), TimeSpan.FromSeconds(1));
            actions.ScheduleTimeout("step-timeout", TimeSpan.FromSeconds(3));
            actions.MarkCompleted();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record DummyCommand(string Name);
}
