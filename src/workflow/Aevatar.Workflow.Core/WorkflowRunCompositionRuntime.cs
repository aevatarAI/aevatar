using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunCompositionRuntime
{
    private readonly Func<string> _actorIdAccessor;
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<Exception?, string, object?[], Task> _logWarningAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;
    private readonly WorkflowInternalStepDispatchHandler _dispatchInternalStepAsync;

    public WorkflowRunCompositionRuntime(
        Func<string> actorIdAccessor,
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<Exception?, string, object?[], Task> logWarningAsync,
        WorkflowRunEffectDispatcher effectDispatcher,
        WorkflowInternalStepDispatchHandler dispatchInternalStepAsync)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _logWarningAsync = logWarningAsync ?? throw new ArgumentNullException(nameof(logWarningAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
        _dispatchInternalStepAsync = dispatchInternalStepAsync ?? throw new ArgumentNullException(nameof(dispatchInternalStepAsync));
    }

    public async Task HandleParallelStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await _effectDispatcher.EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workerRoles = new List<string>();
        if (request.Parameters.TryGetValue("workers", out var workers) && !string.IsNullOrWhiteSpace(workers))
            workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workers));

        var count = workerRoles.Count > 0
            ? workerRoles.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 3, 1, 16, "parallel_count", "count");

        if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "parallel requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var voteStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("vote_step_type", out var voteType) ? voteType : string.Empty);
        var next = _stateAccessor().Clone();
        var parallelState = new WorkflowParallelState
        {
            ExpectedCount = count,
            VoteStepType = voteStepType,
            VoteStepId = string.Empty,
            WorkersSuccess = false,
        };
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase)))
            parallelState.VoteParameters[key["vote_param_".Length..]] = value;
        next.PendingParallelSteps[request.StepId] = parallelState;
        await _persistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workerRoles.Count ? workerRoles[i] : request.TargetRole;
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_sub_{i}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    public async Task HandleForEachStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "parallel", "sub_step_type", "step"));
        var subTargetRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "sub_target_role",
            "sub_role");

        var next = _stateAccessor().Clone();
        next.PendingForeachSteps[request.StepId] = new WorkflowForEachState
        {
            ExpectedCount = items.Length,
        };
        await _persistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
                subParameters[key["sub_param_".Length..]] = value;
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_item_{i}",
                subStepType,
                items[i].Trim(),
                subTargetRole ?? string.Empty,
                subParameters,
                ct);
        }
    }

    public async Task HandleMapReduceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var mapType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "llm_call", "map_step_type", "map_type"));
        var mapRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "map_target_role",
            "map_role");
        var reduceType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("reduce_step_type", out var reduceTypeRaw)
                ? reduceTypeRaw
                : request.Parameters.GetValueOrDefault("reduce_type", "llm_call"));
        var reduceRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "reduce_target_role",
            "reduce_role");
        var reducePrefix = WorkflowParameterValueParser.GetString(
            request.Parameters,
            string.Empty,
            "reduce_prompt_prefix",
            "reduce_prefix");

        var next = _stateAccessor().Clone();
        next.PendingMapReduceSteps[request.StepId] = new WorkflowMapReduceState
        {
            MapCount = items.Length,
            ReduceType = reduceType,
            ReduceRole = reduceRole ?? string.Empty,
            ReducePromptPrefix = reducePrefix,
            ReduceStepId = string.Empty,
        };
        await _persistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_map_{i}",
                mapType,
                items[i],
                mapRole ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
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
