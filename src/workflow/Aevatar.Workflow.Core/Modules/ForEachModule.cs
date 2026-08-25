// ─────────────────────────────────────────────────────────────
// ForEachModule - iterates over a delimited list of items,
// dispatching a configurable sub-step for each item and
// collecting successful results until the first failed item makes the parent terminal.
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
/// collects results, and publishes merged output or a durable fail-fast parent completion.
/// Refactor (iter11/cluster-021): Old backpressure reporting read raw Queue.Count after head removals.
/// Refactor (iter11/cluster-021): New reporting uses cursor-aware queued count while helper preserves FIFO.
/// </summary>
public sealed class ForEachModule : IEventModule<IWorkflowExecutionContext>
{
    internal const string ModuleStateKey = "foreach";
    internal const string FailedItemsError = "one or more foreach items failed";
    private const string InputFileRefsItemsSource = "input_file_refs";
    private const int MaxConcurrentChildPublications = BackpressureHelper.DefaultMaxConcurrentWorkers;
    internal static TimeSpan DurablePublicationRetryDelay => TimeSpan.FromSeconds(1);
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly HashSet<AcceptedChildPublicationKey> _acceptedChildPublications = [];
    private readonly HashSet<AcceptedChildPublicationKey> _preparedChildPublicationAttempts = [];

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
            var state = LoadState(ctx);
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
            if (!WorkflowExecutionStateAccess.MatchesAuthoritativeRun(ctx.RunId, evt.RunId))
            {
                ctx.Logger.LogWarning(
                    "ForEach: ignore fenced request currentRun={CurrentRunId} requestedRun={RequestedRunId} step={StepId}",
                    ctx.RunId,
                    evt.RunId,
                    evt.StepId);
                return;
            }

            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var parentKey = BuildParentAttemptKey(runId, evt.StepId, evt.ExecutionId);
            var state = LoadState(ctx);
            if (!IsNormalizedRun(ctx))
                FenceCompletedChildOutputsToRun(state, runId);

            if (state.CompletionTombstones.ContainsKey(parentKey))
                return;

            // A redelivered parent request is a recovery signal. Its durable state is authoritative;
            // rebuilding it from the request would erase collected results and re-fan out every item.
            if (state.Parents.TryGetValue(parentKey, out var existingParent))
            {
                MigrateLegacyParentState(existingParent, runId, evt.StepId);
                await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);
                if (existingParent.PendingCompletion != null)
                    await PublishPendingCompletionAsync(state, parentKey, existingParent, ctx, ct);
                await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(evt.ExecutionId) &&
                TryAdoptLegacyParentAttempt(state, parentKey, runId, evt, out existingParent))
            {
                // Moving the map entry and enriching its identity are persisted together. The
                // redelivery may only recover already-durable publication intents after that point.
                await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);
                if (existingParent.PendingCompletion != null)
                    await PublishPendingCompletionAsync(state, parentKey, existingParent, ctx, ct);
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
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
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
            ResetBackpressureIfIdle(state);
            state.Parents[parentKey] = parentState;

            var maxConcurrent = BackpressureHelper.ResolveMaxConcurrent(evt.Parameters);
            var minConcurrent = BackpressureHelper.ResolveMinConcurrent(evt.Parameters, maxConcurrent);
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                maxConcurrent,
                minConcurrent);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);

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
                if (WorkflowExecutionValueStore.IsNormalized(kernelState))
                {
                    var childRequest = BackpressureHelper.ToStepRequest(entry, kernelState);
                    WorkflowExecutionValueStore.PrepareInternalDispatch(
                        kernelState,
                        childRequest,
                        originEnvelopeId: null);
                    entry.ExecutionId = childRequest.ExecutionId;
                    entry.InputValueId = childRequest.InputValueId;
                    BackpressureHelper.ClearInlineInputAfterCanonicalAdmission(entry, kernelState);
                }
                parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;

                if (BackpressureHelper.TryAdmit(state.Backpressure, entry))
                {
                    MarkPublicationAttemptPrepared(parentKey, entry);
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
            var state = LoadState(ctx);

            if (parsedParent == null)
                return;

            if (!TryResolveParentState(
                    state,
                    runId,
                    parsedParent,
                    evt.StepId,
                    evt.ExecutionId,
                    out var parentKey,
                    out var parentState))
            {
                if (TrySettleCompletedChildAttempt(
                        state,
                        runId,
                        evt.StepId,
                        evt.ExecutionId,
                        out var completedAttemptChanged))
                {
                    if (completedAttemptChanged)
                        await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);
                    await PublishPendingDispatchesAsync(state, ctx, ct);
                }

                return;
            }
            var parent = string.IsNullOrWhiteSpace(parentState.ParentStepId)
                ? parsedParent
                : parentState.ParentStepId;
            MigrateLegacyParentState(parentState, runId, parent);
            StageAcceptedChildPublicationAcknowledgements(state);

            // A duplicate completion also drives recovery if the terminal publication previously failed.
            if (parentState.PendingCompletion != null)
            {
                SettleChildAfterParentTerminal(state, parentState, evt.StepId);
                await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);
                await PublishPendingCompletionAsync(state, parentKey, parentState, ctx, ct);
                await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            // A completion settles the retained durable intent even if this activation did not
            // observe the original publication acceptance.
            RemovePendingDispatch(parentState, evt.StepId);
            AddIfMissing(parentState.DispatchedStepIds, evt.StepId);

            // A worker is settled exactly once. Replays may still recover a durable top-up intent.
            if (parentState.CollectedStepIds.Contains(evt.StepId))
            {
                if (parentState.Collected.Any(static result => !result.Success))
                {
                    AbandonRemainingWork(state, parentState);
                    parentState.PendingCompletion = BuildParentCompletion(
                        parentState,
                        runId,
                        parent,
                        forceOutcomeUncertain: parentState.Collected.Count < parentState.Expected,
                        CreateItemOutputResolver(ctx));
                }

                await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);
                if (parentState.PendingCompletion != null)
                    await PublishPendingCompletionAsync(state, parentKey, parentState, ctx, ct);
                await PublishPendingDispatchesAsync(state, ctx, ct);
                return;
            }

            parentState.CollectedStepIds.Add(evt.StepId);
            var itemIndex = ExtractDirectItemIndex(evt.StepId);
            var fileRef = itemIndex >= 0 && itemIndex < parentState.ItemFileRefs.Count
                ? parentState.ItemFileRefs[itemIndex]
                : null;
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var itemResult = evt.ToForEachItemResult(itemIndex, fileRef);
            if (normalizedValues)
                itemResult.Output = string.Empty;
            parentState.Collected.Add(itemResult);
            state.Parents[parentKey] = parentState;
            state.Backpressure = BackpressureHelper.EnsureInitialized(
                state.Backpressure,
                BackpressureHelper.DefaultMaxConcurrentWorkers);
            IReadOnlyList<BackpressureQueueEntry> drained = [];
            if (!parentState.SettledWorkerStepIds.Contains(evt.StepId))
            {
                parentState.SettledWorkerStepIds.Add(evt.StepId);
                if (parentState.Collected.Any(static result => !result.Success))
                {
                    BackpressureHelper.CompleteWithoutTopUp(state.Backpressure);
                }
                else
                {
                    drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);
                    StagePendingDispatches(state, drained);
                }
            }

            if (parentState.Collected.Any(static result => !result.Success))
            {
                AbandonRemainingWork(state, parentState);
                parentState.PendingCompletion = BuildParentCompletion(
                    parentState,
                    runId,
                    parent,
                    forceOutcomeUncertain: parentState.Collected.Count < parentState.Expected,
                    CreateItemOutputResolver(kernelState));
                ctx.Logger.LogWarning(
                    "ForEach {StepId}: stop after failed item collected={CollectedCount} expected={ExpectedCount}",
                    parent,
                    parentState.Collected.Count,
                    parentState.Expected);
            }
            else if (parentState.Collected.Count >= parentState.Expected)
            {
                parentState.PendingCompletion = BuildParentCompletion(
                    parentState,
                    runId,
                    parent,
                    forceOutcomeUncertain: false,
                    CreateItemOutputResolver(kernelState));
                if (normalizedValues && parentState.Expected == 1)
                {
                    parentState.PendingCompletion.OutputProvenance =
                        WorkflowStepOutputProvenance.ReferencedStepOutput;
                    await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                        parentState.PendingCompletion,
                        evt,
                        ctx,
                        ct);
                }

                ctx.Logger.LogInformation(
                    "ForEach {StepId}: all {Count} items completed, success=true",
                    parent,
                    parentState.Collected.Count);
            }

            // Completion settlement, queue cursor advancement, top-up intents, and terminal outbox
            // are one durable checkpoint before any resulting publication.
            await SaveStateWithAcceptedChildPublicationsAsync(state, ctx, ct);

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

    internal static bool IsDirectChildStepId(string parentStepId, string childStepId)
    {
        if (string.IsNullOrWhiteSpace(parentStepId) || string.IsNullOrWhiteSpace(childStepId) ||
            !childStepId.StartsWith(parentStepId, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = childStepId[parentStepId.Length..];
        if (IsDirectItemSuffix(suffix))
            return true;

        const string executionMarker = "_execution_";
        const int attemptHashLength = 16;
        if (!suffix.StartsWith(executionMarker, StringComparison.Ordinal) ||
            suffix.Length <= executionMarker.Length + attemptHashLength)
        {
            return false;
        }

        var attemptHash = suffix.AsSpan(executionMarker.Length, attemptHashLength);
        foreach (var value in attemptHash)
        {
            if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return IsDirectItemSuffix(suffix[(executionMarker.Length + attemptHashLength)..]);
    }

    private static bool IsDirectItemSuffix(string suffix)
    {
        const string itemMarker = "_item_";
        if (!suffix.StartsWith(itemMarker, StringComparison.Ordinal))
            return false;

        var itemIndexText = suffix.AsSpan(itemMarker.Length);
        return itemIndexText.Length > 0 &&
               (itemIndexText.Length == 1 || itemIndexText[0] != '0') &&
               int.TryParse(
                   itemIndexText,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var itemIndex) &&
               itemIndex >= 0;
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

    internal static bool TryPrepareFailedParentCompletion(
        ForEachModuleState state,
        WorkflowExecutionKernelState kernelState,
        string runId,
        string parentStepId,
        string parentExecutionId,
        int parentRetryAttempt,
        out string parentKey,
        out bool stateChanged,
        out int collectedCount,
        out int expectedCount)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(kernelState);
        parentKey = string.Empty;
        stateChanged = false;
        collectedCount = 0;
        expectedCount = 0;

        var resolveItemOutput = CreateItemOutputResolver(kernelState);
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var matches = state.Parents
            .Where(candidate =>
                string.Equals(
                    WorkflowRunIdNormalizer.Normalize(candidate.Value.ParentRunId),
                    normalizedRunId,
                    StringComparison.Ordinal) &&
                string.Equals(candidate.Value.ParentStepId, parentStepId, StringComparison.Ordinal) &&
                string.Equals(candidate.Value.ParentExecutionId, parentExecutionId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        string matchedParentKey;
        ForEachParentState parentState;
        if (matches.Length == 1)
        {
            matchedParentKey = matches[0].Key;
            parentState = matches[0].Value;
        }
        else if (matches.Length == 0 &&
                 TryAdoptLegacyParentForReconciliation(
                     state,
                     normalizedRunId,
                     parentStepId,
                     parentExecutionId,
                     parentRetryAttempt,
                     out matchedParentKey,
                     out parentState))
        {
            stateChanged = true;
        }
        else
        {
            return false;
        }

        if (parentState.PendingCompletion != null)
        {
            if (parentState.PendingCompletion.Success)
                return false;

            var remainingWorkChanged = AbandonRemainingWork(state, parentState);
            var normalizedCompletion = BuildRecoveredParentFailureCompletion(
                parentState,
                normalizedRunId,
                parentStepId,
                parentExecutionId,
                resolveItemOutput);
            if (!parentState.PendingCompletion.Equals(normalizedCompletion))
            {
                parentState.PendingCompletion = normalizedCompletion;
                stateChanged = true;
            }

            stateChanged |= remainingWorkChanged;
            parentKey = matchedParentKey;
            collectedCount = parentState.Collected.Count;
            expectedCount = parentState.Expected;
            return true;
        }

        if (parentState.Expected <= 0 ||
            parentState.Collected.Count == 0 ||
            !parentState.Collected.Any(static result => !result.Success))
        {
            return false;
        }

        AbandonRemainingWork(state, parentState);
        parentState.PendingCompletion = BuildParentCompletion(
            parentState,
            normalizedRunId,
            parentStepId,
            forceOutcomeUncertain: parentState.Collected.Count < parentState.Expected,
            resolveItemOutput);
        parentKey = matchedParentKey;
        stateChanged = true;
        collectedCount = parentState.Collected.Count;
        expectedCount = parentState.Expected;
        return true;
    }

    internal static bool IsCompletedChildAttempt(
        ForEachModuleState state,
        string runId,
        string childStepId,
        string? childExecutionId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(childStepId))
            return false;

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        return state.CompletedParentAttempts.Values
            .Where(attempt =>
                string.Equals(
                    WorkflowRunIdNormalizer.Normalize(attempt.ParentRunId),
                    normalizedRunId,
                    StringComparison.Ordinal) &&
                attempt.ChildExecutionIds.TryGetValue(childStepId, out var expectedExecutionId) &&
                (string.IsNullOrWhiteSpace(childExecutionId) ||
                 string.Equals(expectedExecutionId, childExecutionId, StringComparison.Ordinal)))
            .Take(2)
            .Count() == 1;
    }

    private static bool TrySettleCompletedChildAttempt(
        ForEachModuleState state,
        string runId,
        string childStepId,
        string? childExecutionId,
        out bool stateChanged)
    {
        stateChanged = false;
        if (string.IsNullOrWhiteSpace(childStepId))
            return false;

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var matches = state.CompletedParentAttempts.Values
            .Where(attempt =>
                string.Equals(
                    WorkflowRunIdNormalizer.Normalize(attempt.ParentRunId),
                    normalizedRunId,
                    StringComparison.Ordinal) &&
                attempt.ChildExecutionIds.TryGetValue(childStepId, out var expectedExecutionId) &&
                (string.IsNullOrWhiteSpace(childExecutionId) ||
                 string.Equals(expectedExecutionId, childExecutionId, StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            return false;

        var attempt = matches[0];
        if (!attempt.OutstandingChildExecutionIds.TryGetValue(childStepId, out var outstandingExecutionId) ||
            (!string.IsNullOrWhiteSpace(childExecutionId) &&
             !string.Equals(outstandingExecutionId, childExecutionId, StringComparison.Ordinal)))
        {
            return true;
        }

        attempt.OutstandingChildExecutionIds.Remove(childStepId);
        stateChanged = true;
        if (attempt.OutstandingWorkersAccounted && state.Backpressure != null)
        {
            var drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);
            StagePendingDispatches(state, drained);
        }

        if (attempt.OutstandingChildExecutionIds.Count == 0)
            attempt.OutstandingWorkersAccounted = false;
        ResetBackpressureIfIdle(state);
        return true;
    }

    private static StepCompletedEvent BuildParentCompletion(
        ForEachParentState parentState,
        string runId,
        string parentStepId,
        bool forceOutcomeUncertain,
        Func<ForEachItemResult, string> resolveItemOutput)
    {
        ArgumentNullException.ThrowIfNull(resolveItemOutput);
        var results = parentState.Collected;
        var allSuccess = results.All(static result => result.Success);
        var failedResults = results.Where(static result => !result.Success).ToArray();
        var useFileItemResults = IsInputFileRefsSource(parentState);
        IEnumerable<ForEachItemResult> orderedResults = useFileItemResults
            ? results.OrderBy(static result => result.Index)
            : results;
        // Normalized runs keep item payloads in the canonical value store, so the parent
        // completion resolves each item's output through the kernel instead of the
        // (intentionally empty) collected copy.
        var materializedResults = orderedResults
            .Select(result => (Result: result, Output: resolveItemOutput(result)))
            .ToArray();
        var failureOutcome = allSuccess
            ? WorkflowStepFailureOutcome.Unspecified
            : forceOutcomeUncertain || failedResults.Any(static result =>
                result.FailureOutcome == WorkflowStepFailureOutcome.OutcomeUncertain)
                ? WorkflowStepFailureOutcome.OutcomeUncertain
                : WorkflowStepFailureOutcome.CalleeConfirmed;
        var recoveryFailureKind = failedResults
            .Select(static result => result.RecoveryFailureKind)
            .FirstOrDefault(static kind => kind != WorkflowRecoveryFailureKind.Unspecified);

        return new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = runId,
            ExecutionId = parentState.ParentExecutionId,
            Success = allSuccess,
            Output = string.Join("\n---\n", materializedResults.Select(static result => result.Output)),
            Error = allSuccess ? string.Empty : FailedItemsError,
            FileItemResults = useFileItemResults ? BuildFileItemResults(materializedResults) : null,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
            FailureOutcome = failureOutcome,
            RecoveryFailureKind = recoveryFailureKind,
            Outcome = allSuccess
                ? WorkflowStepCompletionOutcome.Succeeded
                : WorkflowStepCompletionOutcome.Failed,
            RetryDisposition = failureOutcome == WorkflowStepFailureOutcome.OutcomeUncertain ||
                               failedResults.Any(static result =>
                                   result.RetryDisposition == WorkflowStepRetryDisposition.Forbidden)
                ? WorkflowStepRetryDisposition.Forbidden
                : WorkflowStepRetryDisposition.Unspecified,
        };
    }

    private static Func<ForEachItemResult, string> CreateItemOutputResolver(IWorkflowExecutionContext ctx) =>
        CreateItemOutputResolver(WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
            ctx,
            WorkflowExecutionKernel.ModuleStateKey));

    private static Func<ForEachItemResult, string> CreateItemOutputResolver(
        WorkflowExecutionKernelState kernelState) =>
        WorkflowExecutionValueStore.IsNormalized(kernelState)
            ? result => WorkflowExecutionValueStore.ResolveCompletedStepOutput(kernelState, result.StepId)
            : static result => result.Output ?? string.Empty;

    private static StepCompletedEvent BuildRecoveredParentFailureCompletion(
        ForEachParentState parentState,
        string runId,
        string parentStepId,
        string parentExecutionId,
        Func<ForEachItemResult, string> resolveItemOutput)
    {
        StepCompletedEvent completion;
        if (parentState.Collected.Any(static result => !result.Success))
        {
            completion = BuildParentCompletion(
                parentState,
                runId,
                parentStepId,
                forceOutcomeUncertain: parentState.Collected.Count < parentState.Expected,
                resolveItemOutput);
        }
        else
        {
            completion = parentState.PendingCompletion?.Clone() ?? new StepCompletedEvent();
            completion.StepId = parentStepId;
            completion.RunId = runId;
            completion.ExecutionId = parentExecutionId;
            completion.Success = false;
            completion.Outcome = WorkflowStepCompletionOutcome.Failed;
            completion.RetryDisposition = WorkflowStepRetryDisposition.Forbidden;
            if (completion.FailureOutcome == WorkflowStepFailureOutcome.Unspecified ||
                parentState.Collected.Count < parentState.Expected)
            {
                completion.FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain;
            }

            if (string.IsNullOrWhiteSpace(completion.Error))
                completion.Error = FailedItemsError;
        }

        return completion;
    }

    private static void SettleChildAfterParentTerminal(
        ForEachModuleState state,
        ForEachParentState parentState,
        string childStepId)
    {
        var wasPending = parentState.PendingDispatches.Any(pending =>
            string.Equals(pending.StepId, childStepId, StringComparison.Ordinal));
        var wasDispatched = parentState.DispatchedStepIds.Contains(childStepId);
        var wasOutstanding = parentState.OutstandingWorkerStepIds.Remove(childStepId);
        RemovePendingDispatch(parentState, childStepId);
        AddIfMissing(parentState.DispatchedStepIds, childStepId);
        if (parentState.SettledWorkerStepIds.Contains(childStepId))
            return;

        parentState.SettledWorkerStepIds.Add(childStepId);
        if (!wasPending && !wasDispatched && !wasOutstanding)
            return;

        state.Backpressure = BackpressureHelper.EnsureInitialized(
            state.Backpressure,
            BackpressureHelper.DefaultMaxConcurrentWorkers);
        var drained = BackpressureHelper.CompleteAndTopUp(state.Backpressure);
        StagePendingDispatches(state, drained);
    }

    private static bool AbandonRemainingWork(
        ForEachModuleState state,
        ForEachParentState parentState)
    {
        var settledStepIds = parentState.SettledWorkerStepIds.ToHashSet(StringComparer.Ordinal);
        var unresolvedDispatches = parentState.PendingDispatches
            .Where(pending => !settledStepIds.Contains(pending.StepId))
            .GroupBy(static pending => pending.StepId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
        var stateChanged = parentState.PendingDispatches.Count > 0;
        var backpressure = state.Backpressure;
        foreach (var pending in unresolvedDispatches)
        {
            if (pending.PublicationFailureOutcome is
                WorkflowChildPublicationFailureOutcome.Unspecified or
                WorkflowChildPublicationFailureOutcome.OutcomeUncertain)
            {
                if (!parentState.OutstandingWorkerStepIds.Contains(pending.StepId))
                {
                    parentState.OutstandingWorkerStepIds.Add(pending.StepId);
                    stateChanged = true;
                }
                continue;
            }

            // Only a typed NotAdmitted receipt proves that the child never entered an inbox.
            // Legacy/unspecified intents remain conservative because they may have escaped before
            // the publication result was checkpointed.
            AddIfMissing(parentState.SettledWorkerStepIds, pending.StepId);
            parentState.OutstandingWorkerStepIds.Remove(pending.StepId);
            if (backpressure != null)
                BackpressureHelper.CompleteWithoutTopUp(backpressure);
            stateChanged = true;
        }
        parentState.PendingDispatches.Clear();
        if (backpressure == null)
            return stateChanged;

        var headIndex = backpressure.HeadIndex >= 0 && backpressure.HeadIndex <= backpressure.Queue.Count
            ? backpressure.HeadIndex
            : 0;
        var retained = backpressure.Queue
            .Skip(headIndex)
            .Where(entry => !parentState.ChildExecutionIds.ContainsKey(entry.StepId))
            .Select(static entry => entry.Clone())
            .ToArray();
        stateChanged |= headIndex != 0 || retained.Length != backpressure.Queue.Count;
        backpressure.Queue.Clear();
        backpressure.Queue.Add(retained);
        backpressure.HeadIndex = 0;
        var activeWorkersBeforeTopUp = backpressure.ActiveWorkers;
        var drained = BackpressureHelper.TopUpToTarget(backpressure);
        StagePendingDispatches(state, drained);
        return stateChanged ||
               activeWorkersBeforeTopUp != backpressure.ActiveWorkers ||
               drained.Count > 0;
    }

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
            MarkPublicationAttemptQueued(entry);
            parentState.ChildExecutionIds[entry.StepId] = entry.ExecutionId;
            parentState.PendingDispatches.Add(entry);
        }
    }

    private async Task PublishPendingDispatchesAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var checkpointRequired = false;
        var prePublicationCheckpointRequired = false;
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

                prePublicationCheckpointRequired |= EnsureStableChildIdentity(entry, parentState);
                prePublicationCheckpointRequired |= NormalizePublicationAttemptState(parentKey, entry);
                if (_acceptedChildPublications.Contains(
                        BuildAcceptedChildPublicationKey(parentKey, entry)))
                {
                    continue;
                }
                pendingPublications.Add(new PendingDispatchPublication(parentKey, entry));
            }
        }

        // Fence stable child identities and the monotonic "publication may begin" fact before
        // transport admission. Crash recovery can then never downgrade uncertainty to rejection.
        if (prePublicationCheckpointRequired)
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
                result.Entry.PublicationFailureOutcome = MergePublicationFailureOutcome(
                    result.Entry.PublicationFailureOutcome,
                    result.FailureOutcome);
                checkpointRequired = true;
                ctx.Logger.LogWarning(
                    result.Error,
                    "ForEach child publication remains pending run={RunId} step={StepId}",
                    result.Entry.RunId,
                    result.Entry.StepId);
                retryParentKeys.Add(result.ParentKey);
                continue;
            }

            _acceptedChildPublications.Add(
                BuildAcceptedChildPublicationKey(result.ParentKey, result.Entry));
        }

        // Successful publication acceptance is an actor-local optimization, not authoritative
        // state. The next required state checkpoint folds accepted intents into the dispatch
        // ledger. A crash before then replays the durable intent with the same stable identity.
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
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            await ctx.PublishAsync(
                BackpressureHelper.ToStepRequest(
                    publication.Entry,
                    WorkflowExecutionValueStore.IsNormalized(kernelState) ? kernelState : null),
                TopologyAudience.Self,
                ct,
                BuildChildPublishOptions(publication.Entry));
            return new PendingDispatchPublicationResult(
                publication,
                Error: null,
                WorkflowChildPublicationFailureOutcome.Unspecified);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureOutcome = ex is EventPublicationException
                {
                    Outcome: EventPublicationFailureOutcome.NotAdmitted,
                }
                ? WorkflowChildPublicationFailureOutcome.NotAdmitted
                : WorkflowChildPublicationFailureOutcome.OutcomeUncertain;
            return new PendingDispatchPublicationResult(publication, ex, failureOutcome);
        }
    }

    private sealed record PendingDispatchPublication(
        string ParentKey,
        BackpressureQueueEntry Entry);

    private sealed record PendingDispatchPublicationResult(
        PendingDispatchPublication Publication,
        Exception? Error,
        WorkflowChildPublicationFailureOutcome FailureOutcome)
    {
        public string ParentKey => Publication.ParentKey;

        public BackpressureQueueEntry Entry => Publication.Entry;
    }

    private void MarkPublicationAttemptPrepared(
        string parentKey,
        BackpressureQueueEntry entry)
    {
        entry.PublicationAttemptStateKnown = true;
        entry.PublicationAttempted = true;
        _preparedChildPublicationAttempts.Add(
            BuildAcceptedChildPublicationKey(parentKey, entry));
    }

    private static void MarkPublicationAttemptQueued(BackpressureQueueEntry entry)
    {
        entry.PublicationAttemptStateKnown = true;
        entry.PublicationAttempted = false;
    }

    private bool NormalizePublicationAttemptState(
        string parentKey,
        BackpressureQueueEntry entry)
    {
        if (!entry.PublicationAttemptStateKnown)
        {
            entry.PublicationAttemptStateKnown = true;
            entry.PublicationAttempted = true;
            entry.PublicationFailureOutcome = WorkflowChildPublicationFailureOutcome.OutcomeUncertain;
            return true;
        }

        if (!entry.PublicationAttempted)
        {
            entry.PublicationAttempted = true;
            return true;
        }

        if (entry.PublicationFailureOutcome ==
            WorkflowChildPublicationFailureOutcome.OutcomeUncertain)
            return false;

        if (entry.PublicationFailureOutcome ==
            WorkflowChildPublicationFailureOutcome.NotAdmitted)
        {
            entry.PublicationFailureOutcome =
                WorkflowChildPublicationFailureOutcome.OutcomeUncertain;
            return true;
        }

        if (_preparedChildPublicationAttempts.Remove(
                BuildAcceptedChildPublicationKey(parentKey, entry)))
        {
            return false;
        }

        entry.PublicationFailureOutcome = WorkflowChildPublicationFailureOutcome.OutcomeUncertain;
        return true;
    }

    private static WorkflowChildPublicationFailureOutcome MergePublicationFailureOutcome(
        WorkflowChildPublicationFailureOutcome current,
        WorkflowChildPublicationFailureOutcome observed) =>
        current == WorkflowChildPublicationFailureOutcome.OutcomeUncertain ||
        observed == WorkflowChildPublicationFailureOutcome.OutcomeUncertain
            ? WorkflowChildPublicationFailureOutcome.OutcomeUncertain
            : observed == WorkflowChildPublicationFailureOutcome.NotAdmitted ||
              current == WorkflowChildPublicationFailureOutcome.NotAdmitted
                ? WorkflowChildPublicationFailureOutcome.NotAdmitted
                : WorkflowChildPublicationFailureOutcome.Unspecified;

    private ForEachModuleState LoadState(IWorkflowExecutionContext ctx)
    {
        var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);
        if (IsNormalizedRun(ctx))
        {
            state.CompletedChildOutputs.Clear();
            state.CompletedChildOutputsRunId = string.Empty;
        }
        PruneAcceptedChildPublications(state);
        return state;
    }

    private async Task SaveStateWithAcceptedChildPublicationsAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        StageAcceptedChildPublicationAcknowledgements(state);
        await SaveStateAsync(state, ctx, ct);
        PruneAcceptedChildPublications(state);
    }

    private void StageAcceptedChildPublicationAcknowledgements(ForEachModuleState state)
    {
        foreach (var (parentKey, parentState) in state.Parents)
        {
            foreach (var entry in parentState.PendingDispatches.ToArray())
            {
                if (!_acceptedChildPublications.Contains(
                        BuildAcceptedChildPublicationKey(parentKey, entry)))
                {
                    continue;
                }

                AddIfMissing(parentState.DispatchedStepIds, entry.StepId);
                RemovePendingDispatch(parentState, entry.StepId);
            }
        }
    }

    private void PruneAcceptedChildPublications(ForEachModuleState state)
    {
        if (_acceptedChildPublications.Count == 0)
            return;

        var durablePending = state.Parents
            .SelectMany(parent => parent.Value.PendingDispatches.Select(entry =>
                BuildAcceptedChildPublicationKey(parent.Key, entry)))
            .ToHashSet();
        _acceptedChildPublications.RemoveWhere(key => !durablePending.Contains(key));
    }

    private static AcceptedChildPublicationKey BuildAcceptedChildPublicationKey(
        string parentKey,
        BackpressureQueueEntry entry) =>
        new(
            parentKey,
            entry.StepId,
            entry.ExecutionId,
            entry.IdempotencyKey);

    private readonly record struct AcceptedChildPublicationKey(
        string ParentKey,
        string StepId,
        string ExecutionId,
        string IdempotencyKey);

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
                WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completion, ctx),
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

        if (!IsNormalizedRun(ctx))
            PreserveCompletedChildOutputs(state, parentState);
        state.Parents.Remove(parentKey);
        AddCompletionTombstone(state, parentKey, parentState);
        ResetBackpressureIfIdle(state);
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
        EnsureLegacyParentChildIdentities(
            state,
            parentState,
            runId,
            request.StepId,
            request.ExecutionId);

        state.Parents.Remove(legacyParentKey);
        state.Parents[typedParentKey] = parentState;
        return true;
    }

    private static bool TryAdoptLegacyParentForReconciliation(
        ForEachModuleState state,
        string runId,
        string parentStepId,
        string parentExecutionId,
        int parentRetryAttempt,
        out string parentKey,
        out ForEachParentState parentState)
    {
        var legacyParentKey = BuildRunStepKey(runId, parentStepId);
        if (!state.Parents.TryGetValue(legacyParentKey, out parentState!))
        {
            parentKey = string.Empty;
            parentState = null!;
            return false;
        }

        if (parentRetryAttempt != 0 ||
            HasConflictingLegacyAttemptIdentity(
                state,
                parentState,
                runId,
                parentStepId,
                parentExecutionId))
        {
            parentKey = string.Empty;
            parentState = null!;
            return false;
        }

        var competingMatches = state.Parents.Count(candidate =>
            string.Equals(candidate.Key, legacyParentKey, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(candidate.Value.ParentStepId) &&
             string.Equals(candidate.Value.ParentStepId, parentStepId, StringComparison.Ordinal) &&
             (string.IsNullOrWhiteSpace(candidate.Value.ParentRunId) ||
              string.Equals(
                  WorkflowRunIdNormalizer.Normalize(candidate.Value.ParentRunId),
                  runId,
                  StringComparison.Ordinal))));
        if (competingMatches != 1)
        {
            parentKey = string.Empty;
            parentState = null!;
            return false;
        }

        var typedParentKey = BuildParentAttemptKey(runId, parentStepId, parentExecutionId);
        if (!string.Equals(legacyParentKey, typedParentKey, StringComparison.Ordinal) &&
            state.Parents.ContainsKey(typedParentKey))
        {
            parentKey = string.Empty;
            parentState = null!;
            return false;
        }

        MigrateLegacyParentState(parentState, runId, parentStepId);
        parentState.ParentExecutionId = parentExecutionId;
        EnsureLegacyParentChildIdentities(
            state,
            parentState,
            runId,
            parentStepId,
            parentExecutionId);
        state.Parents.Remove(legacyParentKey);
        state.Parents[typedParentKey] = parentState;
        parentKey = typedParentKey;
        return true;
    }

    private static bool HasConflictingLegacyAttemptIdentity(
        ForEachModuleState state,
        ForEachParentState parentState,
        string runId,
        string parentStepId,
        string parentExecutionId)
    {
        if (!MatchesOptionalRunId(parentState.ParentRunId, runId) ||
            !MatchesOptionalIdentity(parentState.ParentStepId, parentStepId) ||
            !MatchesOptionalIdentity(parentState.ParentExecutionId, parentExecutionId))
        {
            return true;
        }

        var pendingCompletion = parentState.PendingCompletion;
        if (pendingCompletion != null &&
            (!MatchesOptionalRunId(pendingCompletion.RunId, runId) ||
             !MatchesOptionalIdentity(pendingCompletion.StepId, parentStepId) ||
             !MatchesOptionalIdentity(pendingCompletion.ExecutionId, parentExecutionId)))
        {
            return true;
        }

        if (parentState.ChildExecutionIds.Any(child =>
                !IsCompatibleLegacyChildIdentity(
                    runId,
                    parentStepId,
                    parentExecutionId,
                    child.Key,
                    child.Value)) ||
            parentState.PendingDispatches.Any(pending =>
                !MatchesOptionalRunId(pending.RunId, runId) ||
                !IsCompatibleLegacyChildIdentity(
                    runId,
                    parentStepId,
                    parentExecutionId,
                    pending.StepId,
                    pending.ExecutionId)) ||
            parentState.Collected.Any(result =>
                !IsCompatibleLegacyChildIdentity(
                    runId,
                    parentStepId,
                    parentExecutionId,
                    result.StepId,
                    null)) ||
            parentState.DispatchedStepIds
                .Concat(parentState.SettledWorkerStepIds)
                .Concat(parentState.OutstandingWorkerStepIds)
                .Any(stepId =>
                    !IsCompatibleLegacyChildIdentity(
                        runId,
                        parentStepId,
                        parentExecutionId,
                        stepId,
                        null)))
        {
            return true;
        }

        if (state.Backpressure == null)
            return false;

        return state.Backpressure.Queue.Any(entry =>
            IsDirectChildStepId(parentStepId, entry.StepId) &&
            (!MatchesOptionalRunId(entry.RunId, runId) ||
             !IsCompatibleLegacyChildIdentity(
                 runId,
                 parentStepId,
                 parentExecutionId,
                 entry.StepId,
                 entry.ExecutionId)));
    }

    private static bool IsCompatibleLegacyChildIdentity(
        string runId,
        string parentStepId,
        string parentExecutionId,
        string? childStepId,
        string? childExecutionId)
    {
        if (string.IsNullOrWhiteSpace(childStepId))
            return string.IsNullOrWhiteSpace(childExecutionId);

        var itemIndex = ExtractDirectItemIndex(childStepId);
        if (itemIndex < 0 ||
            (!string.Equals(
                 childStepId,
                 BuildChildStepId(parentStepId, itemIndex, null),
                 StringComparison.Ordinal) &&
             !string.Equals(
                 childStepId,
                 BuildChildStepId(parentStepId, itemIndex, parentExecutionId),
                 StringComparison.Ordinal)))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(childExecutionId) ||
               string.Equals(
                   childExecutionId,
                   BuildChildExecutionId(runId, parentStepId, itemIndex, null),
                   StringComparison.Ordinal) ||
               string.Equals(
                   childExecutionId,
                   BuildChildExecutionId(runId, parentStepId, itemIndex, parentExecutionId),
                   StringComparison.Ordinal);
    }

    private static bool MatchesOptionalRunId(string? candidate, string expected) =>
        string.IsNullOrWhiteSpace(candidate) ||
        string.Equals(
            WorkflowRunIdNormalizer.Normalize(candidate),
            expected,
            StringComparison.Ordinal);

    private static bool MatchesOptionalIdentity(string? candidate, string expected) =>
        string.IsNullOrWhiteSpace(candidate) ||
        string.Equals(candidate, expected, StringComparison.Ordinal);

    private static void EnsureLegacyParentChildIdentities(
        ForEachModuleState state,
        ForEachParentState parentState,
        string runId,
        string parentStepId,
        string parentExecutionId)
    {
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
                        parentStepId,
                        StringComparison.Ordinal))
                {
                    EnsureStableChildIdentity(queued, parentState);
                }
            }
        }

        for (var itemIndex = 0; itemIndex < parentState.Expected; itemIndex++)
        {
            if (parentState.ChildExecutionIds.Keys.Any(childStepId =>
                    IsDirectChildStepId(parentStepId, childStepId) &&
                    ExtractDirectItemIndex(childStepId) == itemIndex))
            {
                continue;
            }

            var childStepId = $"{parentStepId}_item_{itemIndex}";
            parentState.ChildExecutionIds[childStepId] = BuildChildExecutionId(
                runId,
                parentStepId,
                itemIndex,
                parentExecutionId);
        }
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
        var matches = state.Parents
            .Where(candidate =>
                string.Equals(candidate.Value.ParentRunId, runId, StringComparison.Ordinal) &&
                candidate.Value.ChildExecutionIds.TryGetValue(childStepId, out var expectedExecutionId) &&
                (string.IsNullOrWhiteSpace(childExecutionId) ||
                 string.IsNullOrWhiteSpace(expectedExecutionId) ||
                 string.Equals(expectedExecutionId, childExecutionId, StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
        {
            parentKey = matches[0].Key;
            parentState = matches[0].Value;
            return true;
        }

        if (matches.Length > 1)
        {
            parentKey = string.Empty;
            parentState = null!;
            return false;
        }

        var legacyKey = BuildRunStepKey(runId, parentStepId);
        if (state.Parents.TryGetValue(legacyKey, out var legacyParent) &&
            (legacyParent.ChildExecutionIds.Count == 0 ||
             (legacyParent.ChildExecutionIds.TryGetValue(childStepId, out var legacyExecutionId) &&
              (string.IsNullOrWhiteSpace(childExecutionId) ||
               string.IsNullOrWhiteSpace(legacyExecutionId) ||
               string.Equals(legacyExecutionId, childExecutionId, StringComparison.Ordinal)))))
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

    private static void AddCompletionTombstone(
        ForEachModuleState state,
        string parentKey,
        ForEachParentState parentState)
    {
        state.CompletionTombstones[parentKey] = true;
        var completedAttempt = new ForEachCompletedParentAttemptState
        {
            ParentRunId = parentState.ParentRunId,
            ParentStepId = parentState.ParentStepId,
            ParentExecutionId = parentState.ParentExecutionId,
            ChildExecutionIds = { parentState.ChildExecutionIds },
        };
        var settledStepIds = parentState.SettledWorkerStepIds.ToHashSet(StringComparer.Ordinal);
        foreach (var childStepId in parentState.DispatchedStepIds
                     .Concat(parentState.OutstandingWorkerStepIds)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!settledStepIds.Contains(childStepId) &&
                parentState.ChildExecutionIds.TryGetValue(childStepId, out var childExecutionId) &&
                !string.IsNullOrWhiteSpace(childExecutionId))
            {
                completedAttempt.OutstandingChildExecutionIds[childStepId] = childExecutionId;
            }
        }

        completedAttempt.OutstandingWorkersAccounted =
            completedAttempt.OutstandingChildExecutionIds.Count > 0;
        state.CompletedParentAttempts[parentKey] = completedAttempt;
    }

    private static void ResetBackpressureIfIdle(ForEachModuleState state)
    {
        if (state.Parents.Count > 0 ||
            state.CompletedParentAttempts.Values.Any(static attempt =>
                attempt.OutstandingWorkersAccounted &&
                attempt.OutstandingChildExecutionIds.Count > 0))
        {
            return;
        }

        state.Backpressure = new BackpressureQueueState();
    }

    private static bool IsNormalizedRun(IWorkflowExecutionContext ctx)
    {
        var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
            ctx,
            WorkflowExecutionKernel.ModuleStateKey);
        return WorkflowExecutionValueStore.IsNormalized(kernelState);
    }

    private static void PreserveCompletedChildOutputs(
        ForEachModuleState state,
        ForEachParentState parentState)
    {
        if (string.IsNullOrWhiteSpace(parentState.ParentRunId))
            return;

        FenceCompletedChildOutputsToRun(state, parentState.ParentRunId);
        state.CompletedChildOutputsRunId = parentState.ParentRunId;
        foreach (var result in parentState.Collected)
        {
            if (!string.IsNullOrWhiteSpace(result.StepId))
                state.CompletedChildOutputs[result.StepId] = result.Output ?? string.Empty;
        }
    }

    private static void FenceCompletedChildOutputsToRun(ForEachModuleState state, string runId)
    {
        if (state.CompletedChildOutputs.Count == 0 ||
            string.Equals(state.CompletedChildOutputsRunId, runId, StringComparison.Ordinal))
        {
            return;
        }

        state.CompletedChildOutputs.Clear();
        state.CompletedChildOutputsRunId = string.Empty;
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

    private static WorkflowFileItemResultSet BuildFileItemResults(
        IEnumerable<(ForEachItemResult Result, string Output)> results)
    {
        var resultSet = new WorkflowFileItemResultSet();
        resultSet.Results.Add(results
            .OrderBy(static item => item.Result.Index)
            .Select(static item => new WorkflowFileItemResult
            {
                Index = item.Result.Index,
                FileRef = item.Result.FileRef?.Clone(),
                Success = item.Result.Success,
                Output = item.Output,
                Error = item.Result.Error,
            }));
        return resultSet;
    }

    private static Task SaveStateAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0 &&
            state.CompletionTombstones.Count == 0 &&
            state.CompletedParentAttempts.Count == 0 &&
            state.CompletedChildOutputs.Count == 0)
        {
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);
        }

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
