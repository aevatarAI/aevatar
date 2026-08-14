// ─────────────────────────────────────────────────────────────
// ForEachModule - iterates over a delimited list of items,
// dispatching a configurable sub-step for each item and
// collecting all results before publishing completion.
//
// Used by MAKER workflows for per-subtask parallel+vote,
// but is a general-purpose primitive.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// ForEach iteration module. Handles step_type == "foreach".
/// Splits input by delimiter, dispatches a sub-step per item,
/// collects results, and publishes merged output.
/// Refactor (iter11/cluster-021): Old backpressure reporting read raw Queue.Count after head removals.
/// Refactor (iter11/cluster-021): New reporting uses cursor-aware queued count while helper preserves FIFO.
/// </summary>
public sealed class ForEachModule : IEventModule<IWorkflowExecutionContext>
{
    internal const string ModuleStateKey = "foreach";
    private const string InputFileRefsItemsSource = "input_file_refs";
    private const int MaxConcurrentChildPublications = BackpressureHelper.DefaultMaxConcurrentWorkers;
    internal static TimeSpan DurablePublicationRetryDelay => TimeSpan.FromSeconds(1);
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();

    /// <summary>Module name.</summary>
    public string Name => "foreach";

    /// <summary>Priority.</summary>
    public int Priority => 4;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true ||
        envelope.Payload?.Is(ForEachPublicationRetryFiredEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(ForEachPublicationRetryFiredEvent.Descriptor))
        {
            var retry = payload.Unpack<ForEachPublicationRetryFiredEvent>();
            var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);
            if (state.Parents.TryGetValue(retry.ParentKey, out var retryParent) &&
                retryParent.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(state, retry.ParentKey, retryParent, ctx, ct);
            }

            await PublishPendingDispatchesAsync(state, ctx, ct);
            return;
        }

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "foreach") return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var parentKey = BuildParentAttemptKey(runId, evt.StepId, evt.ExecutionId);
            var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);

            if (state.CompletionTombstones.ContainsKey(parentKey))
                return;

            // A redelivered parent request is a recovery signal. Its durable state is authoritative;
            // rebuilding it from the request would erase collected results and re-fan out every item.
            if (state.Parents.TryGetValue(parentKey, out var existingParent))
            {
                MigrateLegacyParentState(existingParent, runId, evt.StepId);
                await SaveStateAsync(state, ctx, ct);
                if (existingParent.PendingCompletion != null)
                    await PublishPendingCompletionAsync(state, parentKey, existingParent, ctx, ct);
                else
                    await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(evt.ExecutionId) &&
                TryAdoptLegacyParentAttempt(state, parentKey, runId, evt, out existingParent))
            {
                // Moving the map entry and enriching its identity are persisted together. The
                // redelivery may only recover already-durable publication intents after that point.
                await SaveStateAsync(state, ctx, ct);
                if (existingParent.PendingCompletion != null)
                    await PublishPendingCompletionAsync(state, parentKey, existingParent, ctx, ct);
                else
                    await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            // ─── Parameters ───
            var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
                WorkflowParameterValueParser.GetString(evt.Parameters, "\n---\n", "delimiter", "separator"),
                "\n---\n");
            var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
                WorkflowParameterValueParser.GetString(evt.Parameters, "parallel", "sub_step_type", "step"));
            var subTargetRole = WorkflowParameterValueParser.GetString(evt.Parameters, evt.TargetRole, "sub_target_role", "sub_role");
            var itemsSource = WorkflowParameterValueParser.GetString(evt.Parameters, string.Empty, "items_source", "source");
            var useInputFileRefs = string.Equals(itemsSource, InputFileRefsItemsSource, StringComparison.OrdinalIgnoreCase);

            // ─── Split input into items ───
            var fileItems = useInputFileRefs
                ? evt.InputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray()
                : [];
            var items = useInputFileRefs
                ? fileItems.Select(ResolveFileItemInput).ToArray()
                : WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(evt.Input, delimiter);
            if (!useInputFileRefs && items.Length == 0 && evt.Parameters.TryGetValue("items", out var itemListRaw))
                items = WorkflowParameterValueParser.ParseStringList(itemListRaw).ToArray();
            if (items.Length == 0)
            {
                var emptyParent = new ForEachParentState
                {
                    Expected = 0,
                    ParentRunId = runId,
                    ParentStepId = evt.StepId,
                    ParentExecutionId = evt.ExecutionId,
                    ParentIdempotencyKey = evt.IdempotencyKey,
                    PendingCompletion = new StepCompletedEvent
                    {
                        StepId = evt.StepId,
                        RunId = runId,
                        ExecutionId = evt.ExecutionId,
                        Success = true,
                        Output = string.Empty,
                    },
                };
                state.Parents[parentKey] = emptyParent;
                await SaveStateAsync(state, ctx, ct);
                await PublishPendingCompletionAsync(state, parentKey, emptyParent, ctx, ct);
                return;
            }

            var parentState = new ForEachParentState
            {
                Expected = items.Length,
                ItemFileRefs = { fileItems },
                ItemsSource = useInputFileRefs ? InputFileRefsItemsSource : string.Empty,
                ParentRunId = runId,
                ParentStepId = evt.StepId,
                ParentExecutionId = evt.ExecutionId,
                ParentIdempotencyKey = evt.IdempotencyKey,
            };

            // Tombstones keep the module state alive after an attempt completes. Once no parent
            // remains, the retained backpressure state belongs to the previous attempt and must
            // not override this request's concurrency configuration.
            if (state.Parents.Count == 0)
                state.Backpressure = new BackpressureQueueState();
            state.Parents[parentKey] = parentState;

            var maxConcurrent = BackpressureHelper.ResolveMaxConcurrent(evt.Parameters);
            var minConcurrent = BackpressureHelper.ResolveMinConcurrent(evt.Parameters, maxConcurrent);
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                maxConcurrent,
                minConcurrent);

            ctx.Logger.LogInformation(
                "ForEach {StepId}: {Count} items, sub_step_type={SubType}",
                evt.StepId, items.Length, subStepType);

            // ─── Dispatch sub-step for each item (with backpressure) ───
            BackpressureAppliedEvent? backpressureApplied = null;
            for (var i = 0; i < items.Length; i++)
            {
                var itemInput = items[i].Trim();
                var itemVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["input"] = itemInput,
                    ["output"] = itemInput,
                };
                var subParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in evt.Parameters)
                {
                    if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                        subParams[key["sub_param_".Length..]] = _expressionEvaluator.Evaluate(value, itemVariables);
                }

                var entry = BackpressureHelper.ToQueueEntry(
                    BuildChildStepId(evt.StepId, i, evt.ExecutionId),
                    subStepType,
                    runId,
                    itemInput,
                    subTargetRole ?? "",
                    subParams,
                    useInputFileRefs ? [fileItems[i]] : null,
                    evt.ExternalInvocation,
                    BuildChildExecutionId(runId, evt.StepId, i, evt.ExecutionId),
                    BuildChildIdempotencyKey(runId, evt.StepId, i, evt.ExecutionId, evt.IdempotencyKey));
                parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;

                if (BackpressureHelper.TryAdmit(state.Backpressure, entry))
                {
                    parentState.PendingDispatches.Add(entry);
                }
                else if (backpressureApplied == null)
                {
                    backpressureApplied = new BackpressureAppliedEvent
                    {
                        StepId = evt.StepId,
                        RunId = runId,
                        QueuedCount = BackpressureHelper.QueuedCount(state.Backpressure),
                        ActiveCount = state.Backpressure.ActiveWorkers,
                        MaxConcurrent = state.Backpressure.MaxConcurrentWorkers,
                    };
                }
            }

            // Checkpoint the parent, queue, active-worker count, and all immediate dispatch intents
            // before the first child publication can escape this actor turn.
            await SaveStateAsync(state, ctx, ct);
            if (backpressureApplied != null)
                await TryPublishBackpressureAppliedAsync(backpressureApplied, ctx, ct);
            await PublishPendingDispatchesAsync(state, ctx, ct);
        }
        else
        {
            // ─── Collect sub-step completions ───
            var evt = payload.Unpack<StepCompletedEvent>();
            // Only collect direct foreach item completions: "<parent>_item_<index>".
            // Ignore nested children like "_item_0_sub_1" or "_item_0_vote".
            var parsedParent = TryGetParentFromDirectItemStepId(evt.StepId);
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);

            if (parsedParent == null ||
                !TryResolveParentState(
                    state,
                    runId,
                    parsedParent,
                    evt.StepId,
                    evt.ExecutionId,
                    out var parentKey,
                    out var parentState))
            {
                return;
            }
            var parent = string.IsNullOrWhiteSpace(parentState.ParentStepId)
                ? parsedParent
                : parentState.ParentStepId;
            MigrateLegacyParentState(parentState, runId, parent);

            // A duplicate completion also drives recovery if the terminal publication previously failed.
            if (parentState.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(state, parentKey, parentState, ctx, ct);
                await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            // A completion proves that a pending publish escaped even if the post-publish checkpoint failed.
            RemovePendingDispatch(parentState, evt.StepId);
            AddIfMissing(parentState.DispatchedStepIds, evt.StepId);

            // A worker is settled exactly once. Replays may still recover a durable top-up intent.
            if (parentState.CollectedStepIds.Contains(evt.StepId))
            {
                await SaveStateAsync(state, ctx, ct);
                await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            parentState.CollectedStepIds.Add(evt.StepId);
            var itemIndex = ExtractDirectItemIndex(evt.StepId);
            var fileRef = itemIndex >= 0 && itemIndex < parentState.ItemFileRefs.Count
                ? parentState.ItemFileRefs[itemIndex]
                : null;
            parentState.Collected.Add(evt.ToForEachItemResult(itemIndex, fileRef));
            state.Parents[parentKey] = parentState;
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                BackpressureHelper.DefaultMaxConcurrentWorkers);
            IReadOnlyList<BackpressureQueueEntry> drained = [];
            if (!parentState.SettledWorkerStepIds.Contains(evt.StepId))
            {
                parentState.SettledWorkerStepIds.Add(evt.StepId);
                drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);
                StagePendingDispatches(state, drained);
            }

            if (parentState.Collected.Count >= parentState.Expected)
            {
                var results = parentState.Collected;
                var allSuccess = results.All(r => r.Success);
                var useFileItemResults = IsInputFileRefsSource(parentState);
                IEnumerable<ForEachItemResult> mergedResults = useFileItemResults
                    ? results.OrderBy(static result => result.Index)
                    : results.AsEnumerable();
                var merged = string.Join("\n---\n", mergedResults.Select(r => r.Output));
                var error = allSuccess
                    ? string.Empty
                    : "one or more foreach items failed";
                var fileItemResults = useFileItemResults
                    ? BuildFileItemResults(results)
                    : null;

                ctx.Logger.LogInformation(
                    "ForEach {StepId}: all {Count} items completed, success={Success}",
                    parent, results.Count, allSuccess);

                parentState.PendingCompletion = new StepCompletedEvent
                {
                    StepId = parent,
                    RunId = runId,
                    ExecutionId = parentState.ParentExecutionId,
                    Success = allSuccess, Output = merged, Error = error,
                    FileItemResults = fileItemResults,
                };
            }

            // Completion settlement, queue cursor advancement, top-up intents, and terminal outbox
            // are one durable checkpoint before any resulting publication.
            await SaveStateAsync(state, ctx, ct);

            if (parentState.PendingCompletion != null)
                await PublishPendingCompletionAsync(state, parentKey, parentState, ctx, ct);
            await PublishPendingDispatchesAsync(state, ctx, ct);
        }
    }

    private static string BuildRunStepKey(string runId, string stepId) => $"{runId}:{stepId}";

    private static string BuildParentAttemptKey(string runId, string stepId, string? executionId) =>
        string.IsNullOrWhiteSpace(executionId)
            ? BuildRunStepKey(runId, stepId)
            : $"{BuildRunStepKey(runId, stepId)}:execution:{RuntimeCallbackKeyComposer.EncodeSegment(executionId.Trim())}";

    private static string BuildChildStepId(string parentStepId, int itemIndex, string? parentExecutionId)
    {
        if (string.IsNullOrWhiteSpace(parentExecutionId))
            return $"{parentStepId}_item_{itemIndex}";

        var attemptHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parentStepId}_execution_{attemptHash}_item_{itemIndex}";
    }

    private static string? TryGetParentFromDirectItemStepId(string stepId)
    {
        var marker = "_item_";
        var idx = stepId.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx <= 0) return null;

        var suffix = stepId[(idx + marker.Length)..];
        if (suffix.Length == 0 || !suffix.All(char.IsDigit))
            return null;

        return stepId[..idx];
    }

    private static int ExtractDirectItemIndex(string stepId)
    {
        var marker = "_item_";
        var idx = stepId.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx <= 0) return -1;

        var suffix = stepId[(idx + marker.Length)..];
        return int.TryParse(suffix, out var index) ? index : -1;
    }

    private static string BuildChildExecutionId(
        string runId,
        string parentStepId,
        int itemIndex,
        string? parentExecutionId)
    {
        var identity = new List<string> { runId, parentStepId };
        if (!string.IsNullOrWhiteSpace(parentExecutionId))
            identity.Add(parentExecutionId.Trim());
        identity.Add(itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return RuntimeCallbackKeyComposer.BuildCallbackId("foreach-child-execution", identity.ToArray());
    }

    private static string BuildChildIdempotencyKey(
        string runId,
        string parentStepId,
        int itemIndex,
        string? parentExecutionId,
        string? parentIdempotencyKey)
    {
        var identity = new List<string> { runId, parentStepId };
        if (!string.IsNullOrWhiteSpace(parentIdempotencyKey))
            identity.Add(parentIdempotencyKey.Trim());
        if (!string.IsNullOrWhiteSpace(parentExecutionId))
            identity.Add(parentExecutionId.Trim());
        identity.Add(itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return RuntimeCallbackKeyComposer.BuildCallbackId("foreach-child", identity.ToArray());
    }

    private static EventEnvelopePublishOptions BuildChildPublishOptions(BackpressureQueueEntry entry) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = string.IsNullOrWhiteSpace(entry.IdempotencyKey)
                    ? RuntimeCallbackKeyComposer.BuildCallbackId("foreach-child", entry.RunId, entry.StepId)
                    : entry.IdempotencyKey,
            },
        };

    private static EventEnvelopePublishOptions BuildCompletionPublishOptions(string parentKey) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId("foreach-completion", parentKey),
            },
        };

    internal static IReadOnlyList<ForEachPublicationRetryFiredEvent> BuildPendingPublicationRetries(
        ForEachModuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Parents
            .Where(static parent =>
                parent.Value.PendingCompletion != null || parent.Value.PendingDispatches.Count > 0)
            .Select(static parent => new ForEachPublicationRetryFiredEvent { ParentKey = parent.Key })
            .ToArray();
    }

    internal static string BuildPublicationRetryCallbackId(ForEachPublicationRetryFiredEvent retry) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("foreach-publication-retry", retry.ParentKey);

    internal static EventEnvelopePublishOptions BuildPublicationRetryOptions(
        ForEachPublicationRetryFiredEvent retry) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = BuildPublicationRetryCallbackId(retry),
            },
        };

    private static void StagePendingDispatches(
        ForEachModuleState state,
        IEnumerable<BackpressureQueueEntry> entries)
    {
        foreach (var entry in entries)
        {
            var parsedParent = TryGetParentFromDirectItemStepId(entry.StepId);
            if (parsedParent == null)
                continue;

            if (!TryResolveParentState(
                    state,
                    WorkflowRunIdNormalizer.Normalize(entry.RunId),
                    parsedParent,
                    entry.StepId,
                    entry.ExecutionId,
                    out _,
                    out var parentState) ||
                parentState.CollectedStepIds.Contains(entry.StepId) ||
                parentState.DispatchedStepIds.Contains(entry.StepId) ||
                parentState.PendingDispatches.Any(pending =>
                    string.Equals(pending.StepId, entry.StepId, StringComparison.Ordinal)))
            {
                continue;
            }

            EnsureStableChildIdentity(entry, parentState);
            parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;
            parentState.PendingDispatches.Add(entry);
        }
    }

    private static async Task PublishPendingDispatchesAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var checkpointRequired = false;
        var identityCheckpointRequired = false;
        var retryParentKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingPublications = new List<PendingDispatchPublication>();

        foreach (var (parentKey, parentState) in state.Parents.ToArray())
        {
            foreach (var entry in parentState.PendingDispatches.ToArray())
            {
                if (parentState.CollectedStepIds.Contains(entry.StepId))
                {
                    RemovePendingDispatch(parentState, entry.StepId);
                    checkpointRequired = true;
                    continue;
                }

                identityCheckpointRequired |= EnsureStableChildIdentity(entry, parentState);
                pendingPublications.Add(new PendingDispatchPublication(parentKey, parentState, entry));
            }
        }

        // Upgrade-era pending entries may predate stable child identities. Fence those identities
        // before any publication so a crash cannot replay the same child under a different operation.
        if (identityCheckpointRequired)
        {
            await SaveStateAsync(state, ctx, ct);
            checkpointRequired = false;
        }

        var publicationResults = new List<PendingDispatchPublicationResult>(pendingPublications.Count);
        foreach (var batch in pendingPublications.Chunk(MaxConcurrentChildPublications))
        {
            publicationResults.AddRange(await Task.WhenAll(batch.Select(
                publication => TryPublishPendingDispatchAsync(publication, ctx, ct))));
        }

        foreach (var result in publicationResults)
        {
            if (result.Error != null)
            {
                ctx.Logger.LogWarning(
                    result.Error,
                    "ForEach child publication remains pending run={RunId} step={StepId}",
                    result.Entry.RunId,
                    result.Entry.StepId);
                retryParentKeys.Add(result.ParentKey);
                continue;
            }

            AddIfMissing(result.ParentState.DispatchedStepIds, result.Entry.StepId);
            RemovePendingDispatch(result.ParentState, result.Entry.StepId);
            checkpointRequired = true;
        }

        // Every intent was durable before publication. A single acknowledgement checkpoint after
        // the batch removes successful intents; if it fails, stable child identities make replay
        // safe and the original durable intents remain authoritative.
        if (checkpointRequired)
            await SaveStateAsync(state, ctx, ct);

        foreach (var parentKey in retryParentKeys)
            await TrySchedulePublicationRetryAsync(parentKey, ctx, ct);
    }

    private static async Task<PendingDispatchPublicationResult> TryPublishPendingDispatchAsync(
        PendingDispatchPublication publication,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            await ctx.PublishAsync(
                BackpressureHelper.ToStepRequest(publication.Entry),
                TopologyAudience.Self,
                ct,
                BuildChildPublishOptions(publication.Entry));
            return new PendingDispatchPublicationResult(publication, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PendingDispatchPublicationResult(publication, ex);
        }
    }

    private sealed record PendingDispatchPublication(
        string ParentKey,
        ForEachParentState ParentState,
        BackpressureQueueEntry Entry);

    private sealed record PendingDispatchPublicationResult(
        PendingDispatchPublication Publication,
        Exception? Error)
    {
        public string ParentKey => Publication.ParentKey;

        public ForEachParentState ParentState => Publication.ParentState;

        public BackpressureQueueEntry Entry => Publication.Entry;
    }

    private static async Task PublishPendingCompletionAsync(
        ForEachModuleState state,
        string parentKey,
        ForEachParentState parentState,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = parentState.PendingCompletion;
        if (completion == null)
            return;

        try
        {
            await ctx.PublishAsync(
                completion.Clone(),
                TopologyAudience.Self,
                ct,
                BuildCompletionPublishOptions(parentKey));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ForEach terminal completion publication remains pending parent={ParentKey}",
                parentKey);
            await TrySchedulePublicationRetryAsync(parentKey, ctx, ct);
            return;
        }

        state.Parents.Remove(parentKey);
        AddCompletionTombstone(state, parentKey);
        if (state.Parents.Count == 0)
            state.Backpressure = new BackpressureQueueState();
        await SaveStateAsync(state, ctx, ct);
    }

    private static async Task TryPublishBackpressureAppliedAsync(
        BackpressureAppliedEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            await ctx.PublishAsync(evt, TopologyAudience.Self, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ForEach backpressure observation publication failed run={RunId} step={StepId}",
                evt.RunId,
                evt.StepId);
        }
    }

    private static async Task TrySchedulePublicationRetryAsync(
        string parentKey,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var retry = new ForEachPublicationRetryFiredEvent { ParentKey = parentKey };
        var callbackId = BuildPublicationRetryCallbackId(retry);
        try
        {
            await ctx.ScheduleSelfDurableTimeoutAsync(
                callbackId,
                DurablePublicationRetryDelay,
                retry,
                BuildPublicationRetryOptions(retry),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ForEach durable publication retry scheduling failed; using typed self continuation parent={ParentKey}",
                parentKey);
            try
            {
                await ctx.PublishAsync(
                    retry,
                    TopologyAudience.Self,
                    ct,
                    BuildPublicationRetryOptions(retry));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception continuationException)
            {
                throw new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
                    "Durable foreach publication recovery remains pending.",
                    continuationException);
            }
        }
    }

    private static bool EnsureStableChildIdentity(
        BackpressureQueueEntry entry,
        ForEachParentState parentState)
    {
        var parent = string.IsNullOrWhiteSpace(parentState.ParentStepId)
            ? TryGetParentFromDirectItemStepId(entry.StepId)
            : parentState.ParentStepId;
        var index = ExtractDirectItemIndex(entry.StepId);
        if (parent == null || index < 0)
            return false;

        var changed = false;
        var runId = WorkflowRunIdNormalizer.Normalize(entry.RunId);
        if (string.IsNullOrWhiteSpace(entry.ExecutionId))
        {
            entry.ExecutionId = BuildChildExecutionId(runId, parent, index, parentState.ParentExecutionId);
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(entry.IdempotencyKey))
        {
            entry.IdempotencyKey = BuildChildIdempotencyKey(
                runId,
                parent,
                index,
                parentState.ParentExecutionId,
                parentState.ParentIdempotencyKey);
            changed = true;
        }

        if (!parentState.ChildExecutionIds.TryGetValue(entry.StepId, out var executionId) ||
            !string.Equals(executionId, entry.ExecutionId, StringComparison.Ordinal))
        {
            parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;
            changed = true;
        }

        return changed;
    }

    private static void MigrateLegacyParentState(
        ForEachParentState parentState,
        string fallbackRunId,
        string fallbackStepId)
    {
        if (string.IsNullOrWhiteSpace(parentState.ParentRunId))
            parentState.ParentRunId = fallbackRunId;
        if (string.IsNullOrWhiteSpace(parentState.ParentStepId))
            parentState.ParentStepId = fallbackStepId;

        // Before the settlement ledger existed, collected results and the backpressure decrement
        // were saved atomically. Treat every persisted legacy collection as already settled.
        foreach (var stepId in parentState.CollectedStepIds)
            AddIfMissing(parentState.SettledWorkerStepIds, stepId);
    }

    private static bool TryAdoptLegacyParentAttempt(
        ForEachModuleState state,
        string typedParentKey,
        string runId,
        StepRequestEvent request,
        out ForEachParentState parentState)
    {
        var legacyParentKey = BuildRunStepKey(runId, request.StepId);
        if (string.Equals(legacyParentKey, typedParentKey, StringComparison.Ordinal) ||
            !state.Parents.TryGetValue(legacyParentKey, out parentState!))
        {
            parentState = null!;
            return false;
        }

        MigrateLegacyParentState(parentState, runId, request.StepId);
        parentState.ParentExecutionId = request.ExecutionId;
        if (string.IsNullOrWhiteSpace(parentState.ParentIdempotencyKey))
            parentState.ParentIdempotencyKey = request.IdempotencyKey;

        foreach (var pending in parentState.PendingDispatches)
            EnsureStableChildIdentity(pending, parentState);

        if (state.Backpressure != null)
        {
            foreach (var queued in state.Backpressure.Queue)
            {
                if (string.Equals(
                        WorkflowRunIdNormalizer.Normalize(queued.RunId),
                        runId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        TryGetParentFromDirectItemStepId(queued.StepId),
                        request.StepId,
                        StringComparison.Ordinal))
                {
                    EnsureStableChildIdentity(queued, parentState);
                }
            }
        }

        for (var itemIndex = 0; itemIndex < parentState.Expected; itemIndex++)
        {
            var childStepId = $"{request.StepId}_item_{itemIndex}";
            if (!parentState.ChildExecutionIds.ContainsKey(childStepId))
            {
                parentState.ChildExecutionIds[childStepId] = BuildChildExecutionId(
                    runId,
                    request.StepId,
                    itemIndex,
                    request.ExecutionId);
            }
        }

        state.Parents.Remove(legacyParentKey);
        state.Parents[typedParentKey] = parentState;
        return true;
    }

    private static bool TryResolveParentState(
        ForEachModuleState state,
        string runId,
        string parentStepId,
        string childStepId,
        string? childExecutionId,
        out string parentKey,
        out ForEachParentState parentState)
    {
        foreach (var candidate in state.Parents)
        {
            if (!string.Equals(candidate.Value.ParentRunId, runId, StringComparison.Ordinal) ||
                !candidate.Value.ChildExecutionIds.ContainsKey(childStepId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(childExecutionId) &&
                candidate.Value.ChildExecutionIds.TryGetValue(childStepId, out var expectedExecutionId) &&
                !string.IsNullOrWhiteSpace(expectedExecutionId) &&
                !string.Equals(expectedExecutionId, childExecutionId, StringComparison.Ordinal))
            {
                continue;
            }

            parentKey = candidate.Key;
            parentState = candidate.Value;
            return true;
        }

        var legacyKey = BuildRunStepKey(runId, parentStepId);
        if (state.Parents.TryGetValue(legacyKey, out var legacyParent))
        {
            parentKey = legacyKey;
            parentState = legacyParent;
            return true;
        }

        parentKey = string.Empty;
        parentState = null!;
        return false;
    }

    private static void RemovePendingDispatch(ForEachParentState parentState, string stepId)
    {
        for (var i = parentState.PendingDispatches.Count - 1; i >= 0; i--)
        {
            if (string.Equals(parentState.PendingDispatches[i].StepId, stepId, StringComparison.Ordinal))
                parentState.PendingDispatches.RemoveAt(i);
        }
    }

    private static void AddIfMissing(Google.Protobuf.Collections.RepeatedField<string> values, string value)
    {
        if (!values.Contains(value))
            values.Add(value);
    }

    private static void AddCompletionTombstone(ForEachModuleState state, string parentKey)
    {
        state.CompletionTombstones[parentKey] = true;
    }

    private static bool IsInputFileRefsSource(ForEachParentState parentState) =>
        string.Equals(parentState.ItemsSource, InputFileRefsItemsSource, StringComparison.OrdinalIgnoreCase);

    private static string ResolveFileItemInput(WorkflowFileRef fileRef)
    {
        if (!string.IsNullOrWhiteSpace(fileRef.ArtifactId))
            return fileRef.ArtifactId;

        if (!string.IsNullOrWhiteSpace(fileRef.FileId))
            return fileRef.FileId;

        return fileRef.FileName;
    }

    private static WorkflowFileItemResultSet BuildFileItemResults(IEnumerable<ForEachItemResult> results)
    {
        var resultSet = new WorkflowFileItemResultSet();
        resultSet.Results.Add(results
            .OrderBy(static result => result.Index)
            .Select(static result => new WorkflowFileItemResult
            {
                Index = result.Index,
                FileRef = result.FileRef?.Clone(),
                Success = result.Success,
                Output = result.Output,
                Error = result.Error,
            }));
        return resultSet;
    }

    private static Task SaveStateAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0 && state.CompletionTombstones.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
