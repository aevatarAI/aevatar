using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Map-Reduce module: splits input into items, maps each through a sub-step in parallel,
/// then reduces all results through a second sub-step.
/// Refactor (iter11/cluster-021): Old backpressure reporting read raw Queue.Count after head removals.
/// Refactor (iter11/cluster-021): New reporting uses cursor-aware queued count while helper preserves FIFO.
/// </summary>
public sealed class MapReduceModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "map_reduce";

    public string Name => "map_reduce";
    public int Priority => 4;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "map_reduce") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var parentKey = BuildParentAttemptKey(runId, request.StepId, request.ExecutionId, normalizedValues);

            var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
                WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
                "\n---\n");
            var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
            if (items.Length == 0 && request.Parameters.TryGetValue("items", out var itemListRaw))
                items = WorkflowParameterValueParser.ParseStringList(itemListRaw).ToArray();
            if (items.Length == 0)
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    ExecutionId = request.ExecutionId,
                    Success = true,
                    Output = "",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                }, TopologyAudience.Self, ct);
                return;
            }

            var mapTypeRaw = WorkflowParameterValueParser.GetString(request.Parameters, "llm_call", "map_step_type", "map_type");
            var mapType = WorkflowPrimitiveCatalog.ToCanonicalType(mapTypeRaw);
            var mapRole = WorkflowParameterValueParser.GetString(
                request.Parameters,
                request.TargetRole,
                "map_target_role",
                "map_role");
            var reduceTypeRaw = request.Parameters.TryGetValue("reduce_step_type", out var reduceStepType)
                ? reduceStepType
                : request.Parameters.GetValueOrDefault("reduce_type", "llm_call");
            var reduceType = string.IsNullOrWhiteSpace(reduceTypeRaw)
                ? string.Empty
                : WorkflowPrimitiveCatalog.ToCanonicalType(reduceTypeRaw);
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

            var state = WorkflowExecutionStateAccess.Load<MapReduceModuleState>(ctx, ModuleStateKey);
            var parentState = new MapReduceParentState
            {
                MapCount = items.Length,
                ReduceType = reduceType,
                ReduceRole = reduceRole ?? string.Empty,
                ReducePromptPrefix = reducePrefix,
                ParentRunId = runId,
                ParentStepId = request.StepId,
                ParentExecutionId = request.ExecutionId,
            };
            RemoveSupersededParentAttempts(state, runId, request.StepId, parentKey);
            state.Parents[parentKey] = parentState;

            var maxConcurrent = BackpressureHelper.ResolveMaxConcurrent(request.Parameters);
            var minConcurrent = BackpressureHelper.ResolveMinConcurrent(request.Parameters, maxConcurrent);
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                maxConcurrent,
                minConcurrent);
            ctx.Logger.LogInformation("MapReduce {StepId}: map {Count} items via {Type}", request.StepId, items.Length, mapType);

            BackpressureAppliedEvent? backpressureApplied = null;
            var immediateDispatches = new List<BackpressureQueueEntry>();
            for (var i = 0; i < items.Length; i++)
            {
                var childStepId = BuildMapChildStepId(
                    request.StepId,
                    i,
                    request.ExecutionId,
                    normalizedValues);
                var entry = BackpressureHelper.ToQueueEntry(
                    childStepId,
                    mapType,
                    runId,
                    items[i],
                    mapRole ?? "",
                    null,
                    executionId: WorkflowExecutionValueStore.BuildInternalExecutionId(
                        runId,
                        request.ExecutionId,
                        childStepId));
                if (WorkflowExecutionValueStore.IsNormalized(kernelState))
                {
                    var mapRequest = BackpressureHelper.ToStepRequest(entry, kernelState);
                    WorkflowExecutionValueStore.PrepareInternalDispatch(
                        kernelState,
                        mapRequest,
                        originEnvelopeId: null);
                    entry.ExecutionId = mapRequest.ExecutionId;
                    entry.InputValueId = mapRequest.InputValueId;
                    BackpressureHelper.ClearInlineInputAfterCanonicalAdmission(entry, kernelState);
                }
                parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;
                state.ChildToParentKey[entry.StepId] = parentKey;
                if (BackpressureHelper.TryAdmit(state.Backpressure, entry))
                {
                    immediateDispatches.Add(entry);
                }
                else if (backpressureApplied == null)
                {
                    backpressureApplied = new BackpressureAppliedEvent
                    {
                        StepId = request.StepId,
                        RunId = runId,
                        QueuedCount = BackpressureHelper.QueuedCount(state.Backpressure),
                        ActiveCount = state.Backpressure.ActiveWorkers,
                        MaxConcurrent = state.Backpressure.MaxConcurrentWorkers,
                    };
                }
            }
            var topUpEntries = BackpressureHelper.TopUpToTarget(state.Backpressure);
            // Always save after loop — TryAdmit mutates ActiveWorkers even when no items are queued
            if (WorkflowExecutionValueStore.IsNormalized(kernelState))
            {
                await WorkflowExecutionStateAccess.SaveAsync(
                    ctx,
                    WorkflowExecutionKernel.ModuleStateKey,
                    kernelState,
                    ct);
            }
            await SaveStateAsync(state, ctx, ct);
            if (backpressureApplied != null)
                await ctx.PublishAsync(backpressureApplied, TopologyAudience.Self, ct);
            foreach (var entry in immediateDispatches)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(entry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
            foreach (var topUpEntry in topUpEntries)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(topUpEntry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);

            var state = WorkflowExecutionStateAccess.Load<MapReduceModuleState>(ctx, ModuleStateKey);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            if (state.ReduceStepToParent.TryGetValue(evt.StepId, out var reduceParent))
            {
                if (!state.Parents.TryGetValue(reduceParent, out var reduceParentState) ||
                    !MatchesExpectedChild(reduceParentState, evt, normalizedValues))
                    return;
                if (!normalizedValues)
                {
                    var completed = new StepCompletedEvent
                    {
                        StepId = reduceParentState.ParentStepId,
                        RunId = reduceParentState.ParentRunId,
                        ExecutionId = reduceParentState.ParentExecutionId,
                        Success = evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    };
                    completed.Annotations["map_reduce.phase"] = "reduce";
                    state.ReduceStepToParent.Remove(evt.StepId);
                    state.Parents.Remove(reduceParent);
                    foreach (var childStepId in reduceParentState.ChildExecutionIds.Keys)
                        state.ChildToParentKey.Remove(childStepId);
                    await SaveStateAsync(state, ctx, ct);
                    await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
                    return;
                }

                if (reduceParentState.PendingCompletion == null)
                {
                    reduceParentState.PendingCompletion = new StepCompletedEvent
                    {
                        StepId = reduceParentState.ParentStepId,
                        RunId = reduceParentState.ParentRunId,
                        ExecutionId = reduceParentState.ParentExecutionId,
                        Success = evt.Success,
                        Output = evt.Output,
                        Error = evt.Error,
                        OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                    };
                    reduceParentState.PendingCompletion.Annotations["map_reduce.phase"] = "reduce";
                    if (normalizedValues)
                    {
                        await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                            reduceParentState.PendingCompletion,
                            evt,
                            ctx,
                            ct);
                    }
                    state.Parents[reduceParent] = reduceParentState;
                    await SaveStateAsync(state, ctx, ct);
                }
                await PublishPendingCompletionAsync(
                    state,
                    reduceParent,
                    reduceParentState,
                    evt,
                    evt.StepId,
                    ctx,
                    ct);
                return;
            }

            var parent = state.ChildToParentKey.TryGetValue(evt.StepId, out var mappedParent)
                ? mappedParent
                : ExtractMapParent(evt.StepId);
            if (parent == null || !state.Parents.TryGetValue(parent, out var parentState)) return;
            if (!MatchesExpectedChild(parentState, evt, normalizedValues)) return;
            if (parentState.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(state, parent, parentState, evt, null, ctx, ct);
                return;
            }

            // Deduplicate: ignore if this map step was already collected
            if (parentState.CollectedStepIds.Contains(evt.StepId))
                return;
            var outputValueId = normalizedValues
                ? WorkflowExecutionValueStore.GetCompletedStepOutputValueId(kernelState, evt.StepId)
                : null;
            if (normalizedValues && string.IsNullOrWhiteSpace(outputValueId))
                return;
            parentState.CollectedStepIds.Add(evt.StepId);
            var itemResult = evt.ToMapReduceItemResult();
            itemResult.OutputValueId = outputValueId ?? string.Empty;
            if (normalizedValues)
                itemResult.Output = string.Empty;
            parentState.Results.Add(itemResult);
            state.Parents[parent] = parentState;
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                BackpressureHelper.DefaultMaxConcurrentWorkers);
            var drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);

            if (parentState.Results.Count < parentState.MapCount)
            {
                await SaveStateAsync(state, ctx, ct);
                foreach (var drainedEntry in drained)
                    await ctx.PublishAsync(
                        BackpressureHelper.ToStepRequest(drainedEntry, normalizedValues ? kernelState : null),
                        TopologyAudience.Self,
                        ct);
                return;
            }

            var allSuccess = parentState.Results.All(r => r.Success);
            var merged = string.Join(
                "\n---\n",
                parentState.Results.Select(result => normalizedValues && !string.IsNullOrWhiteSpace(result.OutputValueId)
                    ? WorkflowExecutionValueStore.ResolveCompletedStepOutput(kernelState, result.StepId)
                    : result.Output));

            if (!allSuccess || string.IsNullOrWhiteSpace(parentState.ReduceType))
            {
                var parentCompletion = new StepCompletedEvent
                {
                    StepId = parentState.ParentStepId,
                    RunId = parentState.ParentRunId,
                    ExecutionId = parentState.ParentExecutionId,
                    Success = allSuccess,
                    Output = merged,
                    Error = allSuccess ? "" : "one or more map steps failed",
                    OutputProvenance = parentState.MapCount == 1
                        ? WorkflowStepOutputProvenance.ReferencedStepOutput
                        : WorkflowStepOutputProvenance.Produced,
                };
                if (!normalizedValues)
                {
                    parentCompletion.OutputProvenance = WorkflowStepOutputProvenance.Produced;
                    state.Parents.Remove(parent);
                    foreach (var childStepId in parentState.ChildExecutionIds.Keys)
                        state.ChildToParentKey.Remove(childStepId);
                    await SaveStateAsync(state, ctx, ct);
                    await ctx.PublishAsync(parentCompletion, TopologyAudience.Self, ct);
                }
                else
                {
                    parentState.PendingCompletion = parentCompletion;
                    if (parentCompletion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput)
                    {
                        await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                            parentCompletion,
                            evt,
                            ctx,
                            ct);
                    }
                    state.Parents[parent] = parentState;
                    await SaveStateAsync(state, ctx, ct);
                    await PublishPendingCompletionAsync(state, parent, parentState, evt, null, ctx, ct);
                }
                foreach (var drainedEntry in drained)
                    await ctx.PublishAsync(
                        BackpressureHelper.ToStepRequest(drainedEntry, normalizedValues ? kernelState : null),
                        TopologyAudience.Self,
                        ct);
                return;
            }

            var reduceInput = string.IsNullOrEmpty(parentState.ReducePromptPrefix)
                ? merged
                : parentState.ReducePromptPrefix.TrimEnd() + "\n\n" + merged;

            var reduceStepId = BuildReduceStepId(parentState, normalizedValues);
            state.ReduceStepToParent[reduceStepId] = parent;
            var reduceExecutionId = WorkflowExecutionValueStore.BuildInternalExecutionId(
                runId,
                parentState.ParentExecutionId,
                reduceStepId);
            parentState.ChildExecutionIds[reduceStepId] = reduceExecutionId;
            state.ChildToParentKey[reduceStepId] = parent;
            state.Parents[parent] = parentState;
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation("MapReduce {StepId}: reduce via {Type}", parentState.ParentStepId, parentState.ReduceType);

            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = reduceStepId,
                StepType = parentState.ReduceType,
                RunId = runId,
                Input = reduceInput,
                TargetRole = parentState.ReduceRole,
                ExecutionId = reduceExecutionId,
            }, TopologyAudience.Self, ct);

            foreach (var drainedEntry in drained)
                await ctx.PublishAsync(
                    BackpressureHelper.ToStepRequest(drainedEntry, normalizedValues ? kernelState : null),
                    TopologyAudience.Self,
                    ct);
        }
    }

    private static async Task PublishPendingCompletionAsync(
        MapReduceModuleState state,
        string parentKey,
        MapReduceParentState parent,
        StepCompletedEvent source,
        string? reduceStepId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = parent.PendingCompletion
            ?? throw new InvalidOperationException($"Map-reduce parent '{parentKey}' has no pending completion.");
        if (completion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput &&
            string.IsNullOrWhiteSpace(completion.OutputReferenceId))
        {
            await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(completion, source, ctx, ct);
            state.Parents[parentKey] = parent;
            await SaveStateAsync(state, ctx, ct);
        }

        await ctx.PublishAsync(
            WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completion, ctx),
            TopologyAudience.Self,
            ct);
        state.Parents.Remove(parentKey);
        if (!string.IsNullOrWhiteSpace(reduceStepId))
            state.ReduceStepToParent.Remove(reduceStepId);
        foreach (var childStepId in parent.ChildExecutionIds.Keys)
            state.ChildToParentKey.Remove(childStepId);
        await SaveStateAsync(state, ctx, CancellationToken.None);
    }

    private static string BuildParentAttemptKey(
        string runId,
        string stepId,
        string executionId,
        bool normalized) =>
        normalized
            ? $"{runId}:{stepId}:execution:{(executionId ?? string.Empty).Trim()}"
            : stepId;

    private static string BuildMapChildStepId(
        string parentStepId,
        int index,
        string parentExecutionId,
        bool normalized)
    {
        if (!normalized || string.IsNullOrWhiteSpace(parentExecutionId))
            return $"{parentStepId}_map_{index}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parentStepId}_execution_{hash}_map_{index}";
    }

    private static string BuildReduceStepId(MapReduceParentState parent, bool normalized)
    {
        if (!normalized || string.IsNullOrWhiteSpace(parent.ParentExecutionId))
            return $"{parent.ParentStepId}_reduce";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parent.ParentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parent.ParentStepId}_execution_{hash}_reduce";
    }

    private static bool MatchesExpectedChild(
        MapReduceParentState parent,
        StepCompletedEvent completion,
        bool normalized)
    {
        if (!string.Equals(
                WorkflowRunIdNormalizer.Normalize(parent.ParentRunId),
                WorkflowRunIdNormalizer.Normalize(completion.RunId),
                StringComparison.Ordinal))
            return false;
        if (!parent.ChildExecutionIds.TryGetValue(completion.StepId, out var expected))
            return !normalized;
        return !normalized || string.Equals(expected, completion.ExecutionId, StringComparison.Ordinal);
    }

    private static void RemoveSupersededParentAttempts(
        MapReduceModuleState state,
        string runId,
        string stepId,
        string currentKey)
    {
        foreach (var (key, parent) in state.Parents.ToArray())
        {
            if (string.Equals(key, currentKey, StringComparison.Ordinal) ||
                !string.Equals(parent.ParentRunId, runId, StringComparison.Ordinal) ||
                !string.Equals(parent.ParentStepId, stepId, StringComparison.Ordinal))
                continue;
            state.Parents.Remove(key);
            foreach (var child in parent.ChildExecutionIds.Keys)
            {
                state.ChildToParentKey.Remove(child);
                state.ReduceStepToParent.Remove(child);
            }
        }
    }

    private static string? ExtractMapParent(string stepId)
    {
        var idx = stepId.LastIndexOf("_map_", StringComparison.Ordinal);
        return idx > 0 ? stepId[..idx] : null;
    }

    private static Task SaveStateAsync(
        MapReduceModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0 &&
            state.ReduceStepToParent.Count == 0 &&
            state.ChildToParentKey.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
