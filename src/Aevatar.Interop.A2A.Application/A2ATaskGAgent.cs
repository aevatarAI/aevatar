using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Interop.A2A.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: process-local IA2ATaskStore ledger owned A2A task lifecycle facts.
//   New principle: task-scoped GAgent owns typed protobuf state/events; readmodel/updates observe committed facts.
[GAgent("interop.a2a-task")]
public sealed class A2ATaskGAgent : GAgentBase<A2ATaskState>
{
    public A2ATaskGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleSubmitAsync(A2ATaskSubmitCommand command)
    {
        // Refactor (iter30/cluster-031-a2a-actor-owned):
        //   Old pattern: tasks/send created/updated lifecycle in IA2ATaskStore.
        //   New principle: submit is a typed command; this task actor commits lifecycle state.
        ArgumentNullException.ThrowIfNull(command);
        if (!string.IsNullOrWhiteSpace(State.TaskId))
            return;

        var now = command.RequestedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var state = new A2ATaskState
        {
            TaskId = command.TaskId,
            SessionId = command.SessionId,
            TargetActorId = command.TargetActorId,
            CommandId = command.CommandId,
            CorrelationId = command.CorrelationId,
            Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Submitted, now),
            StateVersion = State.StateVersion + 1,
            LastEventId = Guid.NewGuid().ToString("N"),
            UpdatedAt = now,
        };
        state.History.Add(command.Message);
        state.Metadata.Add(command.Metadata);

        await PersistDomainEventAsync(new A2ATaskSubmittedEvent
        {
            EventId = state.LastEventId,
            State = state,
        });
    }

    [EventHandler]
    public async Task HandleCancelAsync(A2ATaskCancelCommand command)
    {
        // Refactor (iter30/cluster-031-a2a-actor-owned):
        //   Old pattern: tasks/cancel mutated process-local task state synchronously.
        //   New principle: cancel is a typed command; lifecycle result is observed from actor/readmodel.
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(State.TaskId) || IsFinal(State.Status.State))
            return;

        var now = command.RequestedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var next = State.Clone();
        next.Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Canceled, now);
        next.CommandId = command.CommandId;
        next.CorrelationId = command.CorrelationId;
        next.StateVersion = State.StateVersion + 1;
        next.LastEventId = Guid.NewGuid().ToString("N");
        next.UpdatedAt = now;

        await PersistDomainEventAsync(new A2ATaskCancelSubmittedEvent
        {
            EventId = next.LastEventId,
            State = next,
        });
    }

    protected override A2ATaskState TransitionState(A2ATaskState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<A2ATaskSubmittedEvent>((_, submitted) => submitted.State.Clone())
            .On<A2ATaskCancelSubmittedEvent>((_, canceled) => canceled.State.Clone())
            .OrCurrent();
    }

    protected override async Task OnStateChangedAsync(A2ATaskState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.TaskId))
            return;

        await PublishUpdateAsync(state, ct);
    }

    private Task PublishUpdateAsync(A2ATaskState state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state.TaskId))
            return Task.CompletedTask;

        return PublishAsync(new A2ATaskUpdate
        {
            TaskId = state.TaskId,
            ActorId = Id,
            StateVersion = state.StateVersion,
            LastEventId = state.LastEventId,
            UpdatedAt = state.UpdatedAt,
            Status = state.Status?.Clone(),
            IsFinal = IsFinal(state.Status?.State ?? A2ATaskLifecycleState.Unspecified),
        }, TopologyAudience.Self, ct);
    }

    private static bool IsFinal(A2ATaskLifecycleState state) =>
        state is A2ATaskLifecycleState.Completed
            or A2ATaskLifecycleState.Failed
            or A2ATaskLifecycleState.Canceled;
}
