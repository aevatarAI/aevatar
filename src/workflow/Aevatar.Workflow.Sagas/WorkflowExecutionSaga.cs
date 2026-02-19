using Aevatar.Workflow.Core;
using Aevatar.Workflow.Sagas.States;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Sagas;

public sealed class WorkflowExecutionSaga : SagaBase<WorkflowExecutionSagaState>
{
    private static readonly string StartWorkflowTypeUrl = Any.Pack(new StartWorkflowEvent()).TypeUrl;
    private static readonly string StepRequestTypeUrl = Any.Pack(new StepRequestEvent()).TypeUrl;
    private static readonly string StepCompletedTypeUrl = Any.Pack(new StepCompletedEvent()).TypeUrl;
    private static readonly string WorkflowCompletedTypeUrl = Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

    public override string Name => WorkflowExecutionSagaNames.Execution;

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
        var canStart = string.Equals(envelope.Payload?.TypeUrl, StartWorkflowTypeUrl, StringComparison.Ordinal);
        return ValueTask.FromResult(canStart);
    }

    protected override WorkflowExecutionSagaState CreateState(string correlationId, EventEnvelope envelope)
    {
        return new WorkflowExecutionSagaState
        {
            SagaId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            RootActorId = envelope.PublisherId ?? string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    protected override ValueTask HandleAsync(
        WorkflowExecutionSagaState state,
        EventEnvelope envelope,
        ISagaActionSink actions,
        CancellationToken ct = default)
    {
        _ = ct;

        var payload = envelope.Payload;
        if (payload == null)
            return ValueTask.CompletedTask;

        if (string.IsNullOrWhiteSpace(state.RootActorId))
            state.RootActorId = envelope.PublisherId ?? string.Empty;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            var evt = payload.Unpack<StartWorkflowEvent>();
            state.WorkflowName = evt.WorkflowName ?? state.WorkflowName;
            state.StartedAt ??= DateTimeOffset.UtcNow;
            return ValueTask.CompletedTask;
        }

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            state.RequestedSteps++;
            TrackStepType(state, evt.StepType);
            AppendRecentStep(state, evt.StepId);
            return ValueTask.CompletedTask;
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            state.CompletedSteps++;
            if (!evt.Success)
                state.FailedSteps++;
            AppendRecentStep(state, evt.StepId);
            return ValueTask.CompletedTask;
        }

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowCompletedEvent>();
            state.WorkflowName = evt.WorkflowName ?? state.WorkflowName;
            state.Success = evt.Success;
            state.CompletionError = evt.Error ?? string.Empty;
            state.CompletedAt = DateTimeOffset.UtcNow;
            actions.MarkCompleted();
        }

        return ValueTask.CompletedTask;
    }

    private static void TrackStepType(WorkflowExecutionSagaState state, string stepType)
    {
        if (string.IsNullOrWhiteSpace(stepType))
            return;

        if (!state.StepTypeCounts.TryGetValue(stepType, out var current))
            current = 0;

        state.StepTypeCounts[stepType] = current + 1;
    }

    private static void AppendRecentStep(WorkflowExecutionSagaState state, string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            return;

        state.RecentStepIds.Add(stepId);
        const int maxRecent = 20;
        if (state.RecentStepIds.Count > maxRecent)
            state.RecentStepIds.RemoveRange(0, state.RecentStepIds.Count - maxRecent);
    }
}
