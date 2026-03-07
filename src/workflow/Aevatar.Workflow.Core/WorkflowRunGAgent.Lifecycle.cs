using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleDynamicWorkflowInvokeRequested(DynamicWorkflowInvokeRequestedEvent request)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(request.ParentRunId), State.RunId, StringComparison.Ordinal))
            return;

        var parentStepId = request.ParentStepId?.Trim() ?? string.Empty;
        var workflowYaml = request.WorkflowYaml ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.WorkflowName);
        if (string.IsNullOrWhiteSpace(parentStepId) ||
            string.IsNullOrWhiteSpace(workflowYaml) ||
            string.IsNullOrWhiteSpace(workflowName))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = string.IsNullOrWhiteSpace(parentStepId) ? request.ParentStepId ?? string.Empty : parentStepId,
                RunId = State.RunId,
                Success = false,
                Error = "dynamic_workflow requires parent step id, workflow name, and workflow yaml",
            }, EventDirection.Self);
            return;
        }

        var invocationId = string.IsNullOrWhiteSpace(request.InvocationId)
            ? $"{State.RunId}:dynamic:{parentStepId}:{Guid.NewGuid():N}"
            : request.InvocationId.Trim();
        var childRunId = invocationId;
        var childActorId = BuildSubWorkflowRunActorId(workflowName, WorkflowCallLifecycle.Transient, invocationId);
        var next = State.Clone();
        next.PendingSubWorkflows[childRunId] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = invocationId,
            ParentStepId = parentStepId,
            WorkflowName = workflowName,
            Input = request.Input ?? string.Empty,
            Lifecycle = WorkflowCallLifecycle.Transient,
            ChildActorId = childActorId,
            ChildRunId = childRunId,
            ParentRunId = State.RunId,
        };
        await PersistStateAsync(next, CancellationToken.None);

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowRunActorAsync(childActorId, CancellationToken.None);
            await _runtime.LinkAsync(Id, childActor.Id, CancellationToken.None);
            await childActor.HandleEventAsync(CreateWorkflowDefinitionBindEnvelope(
                workflowYaml,
                workflowName), CancellationToken.None);
            await SendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var rollback = State.Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await PersistStateAsync(rollback, CancellationToken.None);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = State.RunId,
                Success = false,
                Error = $"dynamic_workflow invocation failed: {ex.Message}",
            }, EventDirection.Self, CancellationToken.None);
        }
    }

    private async Task FinalizeRunAsync(bool success, string output, string error, CancellationToken ct)
    {
        var next = State.Clone();
        next.ActiveStepId = string.Empty;
        next.Status = success ? StatusCompleted : StatusFailed;
        next.FinalOutput = success ? output : string.Empty;
        next.FinalError = success ? string.Empty : error;
        await PersistStateAsync(next, ct);
        await PublishFinalWorkflowCompletedAsync(success, output, error, ct);
    }

    private async Task PublishFinalWorkflowCompletedAsync(bool success, string output, string error, CancellationToken ct)
    {
        await PublishAsync(new WorkflowCompletedEvent
        {
            WorkflowName = State.WorkflowName,
            RunId = State.RunId,
            Success = success,
            Output = output,
            Error = error,
        }, EventDirection.Both, ct);

        await PublishAsync(new TextMessageEndEvent
        {
            SessionId = State.RunId,
            Content = success ? output : $"Workflow execution failed: {error}",
        }, EventDirection.Up, ct);
    }

    private Task PersistStateAsync(WorkflowRunState next, CancellationToken ct)
    {
        var patch = WorkflowRunStatePatchSupport.BuildPatch(State, next);
        return patch == null
            ? Task.CompletedTask
            : PersistDomainEventAsync(patch, ct);
    }

    private async Task RepublishSuspendedFactsAsync(CancellationToken ct)
    {
        foreach (var wait in State.PendingSignalWaits.Values)
        {
            await PublishAsync(new WaitingForSignalEvent
            {
                StepId = wait.StepId,
                SignalName = wait.SignalName,
                Prompt = wait.Prompt,
                TimeoutMs = wait.TimeoutMs,
                RunId = State.RunId,
                WaitToken = wait.WaitToken,
            }, EventDirection.Both, ct);
        }

        foreach (var gate in State.PendingHumanGates.Values)
        {
            var suspended = new WorkflowSuspendedEvent
            {
                RunId = State.RunId,
                StepId = gate.StepId,
                SuspensionType = gate.GateType,
                Prompt = gate.Prompt,
                TimeoutSeconds = gate.TimeoutSeconds,
                ResumeToken = gate.ResumeToken,
            };
            if (!string.IsNullOrWhiteSpace(gate.Variable))
                suspended.Metadata["variable"] = gate.Variable;
            if (!string.IsNullOrWhiteSpace(gate.OnTimeout))
                suspended.Metadata["on_timeout"] = gate.OnTimeout;
            if (!string.IsNullOrWhiteSpace(gate.OnReject))
                suspended.Metadata["on_reject"] = gate.OnReject;
            suspended.Metadata["resume_token"] = gate.ResumeToken;
            await PublishAsync(suspended, EventDirection.Both, ct);
        }
    }

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        var result = _workflowCompilationService.Compile(yaml);
        if (!result.Compiled && !string.IsNullOrWhiteSpace(result.CompilationError))
            Logger.LogWarning("WorkflowRunGAgent compile failed: {Error}", result.CompilationError);

        return result;
    }

    private void RebuildCompiledWorkflowCache()
    {
        if (string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            _compiledWorkflow = null;
            return;
        }

        var result = EvaluateWorkflowCompilation(State.WorkflowYaml);
        _compiledWorkflow = result.Workflow;
    }

    private void EnsureWorkflowNameCanBind(string? workflowName)
    {
        var incomingWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var currentWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(State.WorkflowName);
        if (!string.IsNullOrWhiteSpace(currentWorkflowName) &&
            !string.IsNullOrWhiteSpace(incomingWorkflowName) &&
            !string.Equals(currentWorkflowName, incomingWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkflowRunGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }
    }
}
