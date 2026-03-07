using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunSubWorkflowRuntime
{
    private readonly Func<string> _actorIdAccessor;
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<Exception?, string, object?[], Task> _logWarningAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;

    public WorkflowRunSubWorkflowRuntime(
        Func<string> actorIdAccessor,
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<Exception?, string, object?[], Task> logWarningAsync,
        WorkflowRunEffectDispatcher effectDispatcher)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _logWarningAsync = logWarningAsync ?? throw new ArgumentNullException(nameof(logWarningAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
    }

    public async Task HandleWorkflowCallStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var parentStepId = request.StepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.Parameters.GetValueOrDefault("workflow", string.Empty));
        var lifecycle = WorkflowCallLifecycle.Normalize(request.Parameters.GetValueOrDefault("lifecycle", string.Empty));

        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId ?? string.Empty,
                RunId = parentRunId,
                Success = false,
                Error = "workflow_call missing step_id",
            }, EventDirection.Self, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(workflowName))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = "workflow_call missing workflow parameter",
            }, EventDirection.Self, ct);
            return;
        }

        if (!WorkflowCallLifecycle.IsSupported(lifecycle))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call lifecycle must be {WorkflowCallLifecycle.AllowedValuesText}, got '{lifecycle}'",
            }, EventDirection.Self, ct);
            return;
        }

        var invocationId = WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId);
        var childRunId = invocationId;
        var childActorId = WorkflowRunSupport.BuildSubWorkflowRunActorId(_actorIdAccessor(), workflowName, lifecycle, invocationId);
        var next = _stateAccessor().Clone();
        next.PendingSubWorkflows[childRunId] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = invocationId,
            ParentStepId = parentStepId,
            WorkflowName = workflowName,
            Input = request.Input ?? string.Empty,
            Lifecycle = lifecycle,
            ChildActorId = childActorId,
            ChildRunId = childRunId,
        };
        await _persistStateAsync(next, ct);

        try
        {
            var childActor = await _effectDispatcher.ResolveOrCreateSubWorkflowRunActorAsync(childActorId, ct);
            await _effectDispatcher.LinkChildAsync(childActor.Id, ct);
            await childActor.HandleEventAsync(_effectDispatcher.CreateWorkflowDefinitionBindEnvelope(
                await _effectDispatcher.ResolveWorkflowYamlAsync(workflowName, ct),
                workflowName), ct);
            await _sendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, ct);
        }
        catch (Exception ex)
        {
            var rollback = _stateAccessor().Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await _persistStateAsync(rollback, ct);
            await _publishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call invocation failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }

    public async Task<bool> TryHandleSubWorkflowCompletionAsync(
        WorkflowCompletedEvent completed,
        string? publisherActorId,
        CancellationToken ct)
    {
        var state = _stateAccessor();
        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId) || !state.PendingSubWorkflows.TryGetValue(childRunId, out var pending))
            return false;

        if (!string.IsNullOrWhiteSpace(pending.ChildActorId) &&
            !string.Equals(pending.ChildActorId, publisherActorId, StringComparison.Ordinal))
        {
            await _logWarningAsync(
                null,
                "Ignore workflow_call completion due to publisher mismatch childRun={ChildRunId} expected={Expected} actual={Actual}",
                [childRunId, pending.ChildActorId, publisherActorId ?? "(none)"]);
            return true;
        }

        var next = state.Clone();
        next.PendingSubWorkflows.Remove(childRunId);
        await _persistStateAsync(next, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = state.RunId,
            Success = completed.Success,
            Output = completed.Output,
            Error = completed.Error,
        };
        parentCompleted.Metadata["workflow_call.invocation_id"] = pending.InvocationId;
        parentCompleted.Metadata["workflow_call.workflow_name"] = pending.WorkflowName;
        parentCompleted.Metadata["workflow_call.lifecycle"] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Metadata["workflow_call.child_actor_id"] = pending.ChildActorId;
        parentCompleted.Metadata["workflow_call.child_run_id"] = childRunId;
        await _publishAsync(parentCompleted, EventDirection.Self, ct);

        if (!string.Equals(WorkflowCallLifecycle.Normalize(pending.Lifecycle), WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pending.ChildActorId))
        {
            try
            {
                await _effectDispatcher.CleanupChildWorkflowAsync(pending.ChildActorId, ct);
            }
            catch (Exception ex)
            {
                await _logWarningAsync(ex, "Failed to clean up child workflow actor {ChildActorId}", [pending.ChildActorId]);
            }
        }

        return true;
    }
}
