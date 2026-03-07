using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowFanOutCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "fan_out",
        SupportedStepTypes: ["parallel", "foreach", "map_reduce"]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "parallel" => HandleParallelStepAsync(request, read, write, effects, ct),
            "foreach" => HandleForEachStepAsync(request, read, write, effects, ct),
            "map_reduce" => HandleMapReduceStepAsync(request, read, write, effects, ct),
            _ => Task.CompletedTask,
        };

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) =>
        CanHandleParallelCompletion(evt, read.State) ||
        CanHandleForEachCompletion(evt, read.State) ||
        CanHandleMapReduceCompletion(evt, read.State);

    public async Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (await TryHandleParallelCompletionAsync(evt, read, write, effects, ct))
            return;
        if (await TryHandleForEachCompletionAsync(evt, read, write, ct))
            return;

        await TryHandleMapReduceCompletionAsync(evt, read, write, effects, ct);
    }

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
        WorkflowRunReadContext read) =>
        false;

    public Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

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

    private static async Task HandleParallelStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        await effects.EnsureAgentTreeAsync(ct);

        var workerRoles = new List<string>();
        if (request.Parameters.TryGetValue("workers", out var workers) && !string.IsNullOrWhiteSpace(workers))
            workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workers));

        var count = workerRoles.Count > 0
            ? workerRoles.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 3, 1, 16, "parallel_count", "count");

        if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = read.RunId,
                Success = false,
                Error = "parallel requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var voteStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("vote_step_type", out var voteType) ? voteType : string.Empty);
        var next = read.State.Clone();
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
        await write.PersistStateAsync(next, ct);

        for (var index = 0; index < count; index++)
        {
            var role = index < workerRoles.Count ? workerRoles[index] : request.TargetRole;
            await effects.DispatchInternalStepAsync(
                read.RunId,
                request.StepId,
                $"{request.StepId}_sub_{index}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private static async Task HandleForEachStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = read.RunId,
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

        var next = read.State.Clone();
        next.PendingForeachSteps[request.StepId] = new WorkflowForEachState
        {
            ExpectedCount = items.Length,
        };
        await write.PersistStateAsync(next, ct);

        for (var index = 0; index < items.Length; index++)
        {
            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
                subParameters[key["sub_param_".Length..]] = value;
            await effects.DispatchInternalStepAsync(
                read.RunId,
                request.StepId,
                $"{request.StepId}_item_{index}",
                subStepType,
                items[index].Trim(),
                subTargetRole ?? string.Empty,
                subParameters,
                ct);
        }
    }

    private static async Task HandleMapReduceStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = read.RunId,
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

        var next = read.State.Clone();
        next.PendingMapReduceSteps[request.StepId] = new WorkflowMapReduceState
        {
            MapCount = items.Length,
            ReduceType = reduceType,
            ReduceRole = reduceRole ?? string.Empty,
            ReducePromptPrefix = reducePrefix,
            ReduceStepId = string.Empty,
        };
        await write.PersistStateAsync(next, ct);

        for (var index = 0; index < items.Length; index++)
        {
            await effects.DispatchInternalStepAsync(
                read.RunId,
                request.StepId,
                $"{request.StepId}_map_{index}",
                mapType,
                items[index],
                mapRole ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private static async Task<bool> TryHandleParallelCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        foreach (var (parentStepId, pending) in state.PendingParallelSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.VoteStepId) &&
                string.Equals(pending.VoteStepId, evt.StepId, StringComparison.Ordinal))
            {
                var voteNextState = state.Clone();
                voteNextState.PendingParallelSteps.Remove(parentStepId);
                voteNextState.StepExecutions.Remove(evt.StepId);
                await write.PersistStateAsync(voteNextState, ct);

                var voteCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = pending.WorkersSuccess && evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                    WorkerId = evt.WorkerId,
                };
                foreach (var (key, value) in evt.Metadata)
                    voteCompleted.Metadata[key] = value;
                voteCompleted.Metadata["parallel.used_vote"] = "true";
                voteCompleted.Metadata["parallel.vote_step_id"] = evt.StepId;
                voteCompleted.Metadata["parallel.workers_success"] = pending.WorkersSuccess.ToString();
                await write.PublishAsync(voteCompleted, EventDirection.Self, ct);
                return true;
            }

            if (!WorkflowParentStepIds.TryGetParallelParent(evt.StepId, out var completionParent) ||
                !string.Equals(completionParent, parentStepId, StringComparison.Ordinal))
            {
                continue;
            }

            var next = state.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingParallelSteps[parentStepId].ChildResults.Add(WorkflowRecordedResults.ToRecordedResult(evt));
            var collected = next.PendingParallelSteps[parentStepId].ChildResults.Count;
            if (collected < next.PendingParallelSteps[parentStepId].ExpectedCount)
            {
                await write.PersistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingParallelSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!string.IsNullOrWhiteSpace(next.PendingParallelSteps[parentStepId].VoteStepType))
            {
                var voteStepId = $"{parentStepId}_vote";
                next.PendingParallelSteps[parentStepId].VoteStepId = voteStepId;
                next.PendingParallelSteps[parentStepId].WorkersSuccess = allSuccess;
                await write.PersistStateAsync(next, ct);
                await effects.DispatchInternalStepAsync(
                    state.RunId,
                    parentStepId,
                    voteStepId,
                    next.PendingParallelSteps[parentStepId].VoteStepType,
                    merged,
                    string.Empty,
                    next.PendingParallelSteps[parentStepId].VoteParameters.ToDictionary(x => x.Key, x => x.Value),
                    ct);
                return true;
            }

            next.PendingParallelSteps.Remove(parentStepId);
            await write.PersistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = allSuccess,
                Output = merged,
            };
            completed.Metadata["parallel.used_vote"] = "false";
            await write.PublishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        return false;
    }

    private static async Task<bool> TryHandleForEachCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var state = read.State;
        var parentStepId = WorkflowParentStepIds.TryGetForEachParent(evt.StepId);
        if (parentStepId == null || !state.PendingForeachSteps.TryGetValue(parentStepId, out _))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingForeachSteps[parentStepId].ChildResults.Add(WorkflowRecordedResults.ToRecordedResult(evt));
        if (next.PendingForeachSteps[parentStepId].ChildResults.Count < next.PendingForeachSteps[parentStepId].ExpectedCount)
        {
            await write.PersistStateAsync(next, ct);
            return true;
        }

        var results = next.PendingForeachSteps[parentStepId].ChildResults.ToList();
        next.PendingForeachSteps.Remove(parentStepId);
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = state.RunId,
            Success = results.All(x => x.Success),
            Output = string.Join("\n---\n", results.Select(x => x.Output)),
        }, EventDirection.Self, ct);
        return true;
    }

    private static async Task<bool> TryHandleMapReduceCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        foreach (var (parentStepId, pending) in state.PendingMapReduceSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.ReduceStepId) &&
                string.Equals(pending.ReduceStepId, evt.StepId, StringComparison.Ordinal))
            {
                var reduceNextState = state.Clone();
                reduceNextState.PendingMapReduceSteps.Remove(parentStepId);
                reduceNextState.StepExecutions.Remove(evt.StepId);
                await write.PersistStateAsync(reduceNextState, ct);

                var reduceCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                reduceCompleted.Metadata["map_reduce.phase"] = "reduce";
                await write.PublishAsync(reduceCompleted, EventDirection.Self, ct);
                return true;
            }

            var mapParent = WorkflowParentStepIds.TryGetMapReduceParent(evt.StepId);
            if (!string.Equals(mapParent, parentStepId, StringComparison.Ordinal))
                continue;

            var next = state.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingMapReduceSteps[parentStepId].ChildResults.Add(WorkflowRecordedResults.ToRecordedResult(evt));
            if (next.PendingMapReduceSteps[parentStepId].ChildResults.Count < next.PendingMapReduceSteps[parentStepId].MapCount)
            {
                await write.PersistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingMapReduceSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!allSuccess || string.IsNullOrWhiteSpace(next.PendingMapReduceSteps[parentStepId].ReduceType))
            {
                next.PendingMapReduceSteps.Remove(parentStepId);
                await write.PersistStateAsync(next, ct);
                await write.PublishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = allSuccess,
                    Output = merged,
                    Error = allSuccess ? string.Empty : "one or more map steps failed",
                }, EventDirection.Self, ct);
                return true;
            }

            var reduceInput = string.IsNullOrEmpty(next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix)
                ? merged
                : next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix.TrimEnd() + "\n\n" + merged;
            var reduceStepId = $"{parentStepId}_reduce";
            next.PendingMapReduceSteps[parentStepId].ReduceStepId = reduceStepId;
            await write.PersistStateAsync(next, ct);
            await effects.DispatchInternalStepAsync(
                state.RunId,
                parentStepId,
                reduceStepId,
                next.PendingMapReduceSteps[parentStepId].ReduceType,
                reduceInput,
                next.PendingMapReduceSteps[parentStepId].ReduceRole,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
            return true;
        }

        return false;
    }

    private static bool CanHandleParallelCompletion(StepCompletedEvent evt, WorkflowRunState state)
    {
        foreach (var (parentStepId, pending) in state.PendingParallelSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.VoteStepId) &&
                string.Equals(pending.VoteStepId, evt.StepId, StringComparison.Ordinal))
            {
                return true;
            }

            if (WorkflowParentStepIds.TryGetParallelParent(evt.StepId, out var completionParent) &&
                string.Equals(completionParent, parentStepId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanHandleForEachCompletion(StepCompletedEvent evt, WorkflowRunState state)
    {
        var parentStepId = WorkflowParentStepIds.TryGetForEachParent(evt.StepId);
        return !string.IsNullOrWhiteSpace(parentStepId) && state.PendingForeachSteps.ContainsKey(parentStepId);
    }

    private static bool CanHandleMapReduceCompletion(StepCompletedEvent evt, WorkflowRunState state)
    {
        foreach (var (parentStepId, pending) in state.PendingMapReduceSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.ReduceStepId) &&
                string.Equals(pending.ReduceStepId, evt.StepId, StringComparison.Ordinal))
            {
                return true;
            }

            var mapParent = WorkflowParentStepIds.TryGetMapReduceParent(evt.StepId);
            if (string.Equals(mapParent, parentStepId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
