using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunTimeoutCallbackRuntime
{
    private readonly WorkflowRunRuntimeContext _context;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;

    public WorkflowRunTimeoutCallbackRuntime(
        WorkflowRunRuntimeContext context,
        WorkflowRunDispatchRuntime dispatchRuntime)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatchRuntime = dispatchRuntime ?? throw new ArgumentNullException(nameof(dispatchRuntime));
    }

    public async Task HandleWorkflowStepTimeoutFiredAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingTimeouts.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    public async Task HandleWorkflowStepRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingRetryBackoffs.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var step = _context.CompiledWorkflow?.GetStep(evt.StepId);
        if (step == null || !state.StepExecutions.TryGetValue(evt.StepId, out var execution))
            return;

        var next = state.Clone();
        next.PendingRetryBackoffs.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _dispatchRuntime.DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, state.RunId, ct);
    }

    public async Task HandleDelayStepTimeoutFiredAsync(
        DelayStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingDelays.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingDelays.Remove(evt.StepId);
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    public async Task HandleWaitSignalTimeoutFiredAsync(
        WaitSignalTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!WorkflowRunSupport.TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingSignalWaits.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowRunSupport.MatchesSemanticGeneration(envelope, pending.TimeoutGeneration))
            return;

        var next = state.Clone();
        next.PendingSignalWaits.Remove(evt.StepId);
        next.Status = "active";
        await _context.PersistStateAsync(next, ct);
        await _context.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"signal '{pending.SignalName}' timed out after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }
}
