using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowSubWorkflowCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "sub_workflow",
        SupportedStepTypes: ["workflow_call"]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        HandleWorkflowCallStepAsync(request, read, write, effects, ct);

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read) => false;

    public Task HandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResponse(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleChildRunCompletion(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read)
    {
        var childRunId = evt.RunId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(childRunId) &&
               read.State.PendingSubWorkflows.ContainsKey(childRunId);
    }

    public async Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        var childRunId = evt.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId) || !state.PendingSubWorkflows.TryGetValue(childRunId, out var pending))
            return;

        if (!string.IsNullOrWhiteSpace(pending.ChildActorId) &&
            !string.Equals(pending.ChildActorId, publisherActorId, StringComparison.Ordinal))
        {
            await write.LogWarningAsync(
                null,
                "Ignore workflow_call completion due to publisher mismatch childRun={ChildRunId} expected={Expected} actual={Actual}",
                childRunId,
                pending.ChildActorId,
                publisherActorId ?? "(none)");
            return;
        }

        var next = state.Clone();
        next.PendingSubWorkflows.Remove(childRunId);
        await write.PersistStateAsync(next, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = state.RunId,
            Success = evt.Success,
            Output = evt.Output,
            Error = evt.Error,
        };
        parentCompleted.Metadata["workflow_call.invocation_id"] = pending.InvocationId;
        parentCompleted.Metadata["workflow_call.workflow_name"] = pending.WorkflowName;
        parentCompleted.Metadata["workflow_call.lifecycle"] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Metadata["workflow_call.child_actor_id"] = pending.ChildActorId;
        parentCompleted.Metadata["workflow_call.child_run_id"] = childRunId;
        await write.PublishAsync(parentCompleted, EventDirection.Self, ct);

        if (!string.Equals(WorkflowCallLifecycle.Normalize(pending.Lifecycle), WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pending.ChildActorId))
        {
            try
            {
                await effects.CleanupChildWorkflowAsync(pending.ChildActorId, ct);
            }
            catch (Exception ex)
            {
                await write.LogWarningAsync(ex, "Failed to clean up child workflow actor {ChildActorId}", pending.ChildActorId);
            }
        }
    }

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static async Task HandleWorkflowCallStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var parentStepId = request.StepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.Parameters.GetValueOrDefault("workflow", string.Empty));
        var lifecycle = WorkflowCallLifecycle.Normalize(request.Parameters.GetValueOrDefault("lifecycle", string.Empty));

        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            await write.PublishAsync(new StepCompletedEvent
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
            await write.PublishAsync(new StepCompletedEvent
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
            await write.PublishAsync(new StepCompletedEvent
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
        var childActorId = WorkflowActorIds.BuildSubWorkflowRunActorId(read.ActorId, workflowName, lifecycle, invocationId);
        var next = read.State.Clone();
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
        await write.PersistStateAsync(next, ct);

        try
        {
            var childActor = await effects.ResolveOrCreateSubWorkflowRunActorAsync(childActorId, ct);
            await effects.LinkChildAsync(childActor.Id, ct);
            await childActor.HandleEventAsync(
                effects.CreateWorkflowDefinitionBindEnvelope(
                    await effects.ResolveWorkflowYamlAsync(workflowName, ct),
                    workflowName),
                ct);
            await write.SendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, ct);
        }
        catch (Exception ex)
        {
            var rollback = read.State.Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await write.PersistStateAsync(rollback, ct);
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call invocation failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }
}
