using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private async Task HandleParallelStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workerRoles = new List<string>();
        if (request.Parameters.TryGetValue("workers", out var workers) && !string.IsNullOrWhiteSpace(workers))
            workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workers));

        var count = workerRoles.Count > 0
            ? workerRoles.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 3, 1, 16, "parallel_count", "count");

        if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await PublishAsync(new StepCompletedEvent
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
        var next = State.Clone();
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
        await PersistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workerRoles.Count ? workerRoles[i] : request.TargetRole;
            await DispatchInternalStepAsync(
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

    private async Task HandleForEachStepRequestAsync(StepRequestEvent request, CancellationToken ct)
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
            await PublishAsync(new StepCompletedEvent
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

        var next = State.Clone();
        next.PendingForeachSteps[request.StepId] = new WorkflowForEachState
        {
            ExpectedCount = items.Length,
        };
        await PersistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
                subParameters[key["sub_param_".Length..]] = value;
            await DispatchInternalStepAsync(
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

    private async Task HandleMapReduceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
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
            await PublishAsync(new StepCompletedEvent
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

        var next = State.Clone();
        next.PendingMapReduceSteps[request.StepId] = new WorkflowMapReduceState
        {
            MapCount = items.Length,
            ReduceType = reduceType,
            ReduceRole = reduceRole ?? string.Empty,
            ReducePromptPrefix = reducePrefix,
            ReduceStepId = string.Empty,
        };
        await PersistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            await DispatchInternalStepAsync(
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

    private async Task HandleWorkflowCallStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var parentStepId = request.StepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.Parameters.GetValueOrDefault("workflow", string.Empty));
        var lifecycle = WorkflowCallLifecycle.Normalize(request.Parameters.GetValueOrDefault("lifecycle", string.Empty));

        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            await PublishAsync(new StepCompletedEvent
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
            await PublishAsync(new StepCompletedEvent
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
            await PublishAsync(new StepCompletedEvent
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
        var childActorId = BuildSubWorkflowRunActorId(workflowName, lifecycle, invocationId);
        var next = State.Clone();
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
        await PersistStateAsync(next, ct);

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowRunActorAsync(childActorId, ct);
            await _runtime.LinkAsync(Id, childActor.Id, ct);
            await childActor.HandleEventAsync(CreateWorkflowDefinitionBindEnvelope(
                await ResolveWorkflowYamlAsync(workflowName, ct),
                workflowName), ct);
            await SendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, ct);
        }
        catch (Exception ex)
        {
            var rollback = State.Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await PersistStateAsync(rollback, ct);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call invocation failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }
}
