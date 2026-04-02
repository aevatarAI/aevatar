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
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// ForEach iteration module. Handles step_type == "foreach".
/// Splits input by delimiter, dispatches a sub-step per item,
/// collects results, and publishes merged output.
/// </summary>
public sealed class ForEachModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "foreach";

    /// <summary>Module name.</summary>
    public string Name => "foreach";

    /// <summary>Priority.</summary>
    public int Priority => 4;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "foreach") return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var parentKey = BuildRunStepKey(runId, evt.StepId);

            // ─── Parameters ───
            var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
                WorkflowParameterValueParser.GetString(evt.Parameters, "\n---\n", "delimiter", "separator"),
                "\n---\n");
            var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
                WorkflowParameterValueParser.GetString(evt.Parameters, "parallel", "sub_step_type", "step"));
            var subTargetRole = WorkflowParameterValueParser.GetString(evt.Parameters, evt.TargetRole, "sub_target_role", "sub_role");

            // ─── Split input into items ───
            var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(evt.Input, delimiter);
            if (items.Length == 0 && evt.Parameters.TryGetValue("items", out var itemListRaw))
                items = WorkflowParameterValueParser.ParseStringList(itemListRaw).ToArray();
            if (items.Length == 0)
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = evt.StepId,
                    RunId = runId,
                    Success = true, Output = "",
                }, TopologyAudience.Self, ct);
                return;
            }

            var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);
            state.Parents[parentKey] = new ForEachParentState
            {
                Expected = items.Length,
            };

            if (state.Backpressure.MaxConcurrentWorkers == 0)
                state.Backpressure = BackpressureHelper.Initialize(
                    BackpressureHelper.ResolveMaxConcurrent(evt.Parameters));

            ctx.Logger.LogInformation(
                "ForEach {StepId}: {Count} items, sub_step_type={SubType}",
                evt.StepId, items.Length, subStepType);

            // ─── Dispatch sub-step for each item (with backpressure) ───
            var bpApplied = false;
            for (var i = 0; i < items.Length; i++)
            {
                var subParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in evt.Parameters)
                {
                    if (key.StartsWith("sub_param_"))
                        subParams[key["sub_param_".Length..]] = value;
                }

                var entry = BackpressureHelper.ToQueueEntry(
                    $"{evt.StepId}_item_{i}", subStepType, runId, items[i].Trim(), subTargetRole ?? "", subParams);

                if (BackpressureHelper.TryAdmit(state.Backpressure, entry))
                {
                    await ctx.PublishAsync(BackpressureHelper.ToStepRequest(entry), TopologyAudience.Self, ct);
                }
                else if (!bpApplied)
                {
                    bpApplied = true;
                    await ctx.PublishAsync(new BackpressureAppliedEvent
                    {
                        StepId = evt.StepId,
                        RunId = runId,
                        QueuedCount = state.Backpressure.Queue.Count,
                        ActiveCount = state.Backpressure.ActiveWorkers,
                        MaxConcurrent = state.Backpressure.MaxConcurrentWorkers,
                    }, TopologyAudience.Self, ct);
                }
            }
            // Always save after loop — TryAdmit mutates ActiveWorkers even when no items are queued
            await SaveStateAsync(state, ctx, ct);
        }
        else
        {
            // ─── Collect sub-step completions ───
            var evt = payload.Unpack<StepCompletedEvent>();
            // Only collect direct foreach item completions: "<parent>_item_<index>".
            // Ignore nested children like "_item_0_sub_1" or "_item_0_vote".
            var parent = TryGetParentFromDirectItemStepId(evt.StepId);
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var parentKey = parent == null ? null : BuildRunStepKey(runId, parent);
            var state = WorkflowExecutionStateAccess.Load<ForEachModuleState>(ctx, ModuleStateKey);

            if (parent == null || parentKey == null || !state.Parents.TryGetValue(parentKey, out var parentState)) return;

            // Deduplicate: ignore if this item step was already collected
            if (parentState.CollectedStepIds.Contains(evt.StepId))
                return;
            parentState.CollectedStepIds.Add(evt.StepId);
            parentState.Collected.Add(evt.ToForEachItemResult());
            state.Parents[parentKey] = parentState;
            var drained = BackpressureHelper.TryDrainOne(state.Backpressure);

            if (parentState.Collected.Count >= parentState.Expected)
            {
                var results = parentState.Collected;
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join("\n---\n", results.Select(r => r.Output));

                ctx.Logger.LogInformation(
                    "ForEach {StepId}: all {Count} items completed, success={Success}",
                    parent, results.Count, allSuccess);

                state.Parents.Remove(parentKey);
                await SaveStateAsync(state, ctx, ct);

                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = parent,
                    RunId = runId,
                    Success = allSuccess, Output = merged,
                }, TopologyAudience.Self, ct);
            }
            else
            {
                await SaveStateAsync(state, ctx, ct);
            }

            if (drained != null)
                await ctx.PublishAsync(BackpressureHelper.ToStepRequest(drained), TopologyAudience.Self, ct);
        }
    }

    private static string BuildRunStepKey(string runId, string stepId) => $"{runId}:{stepId}";

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

    private static Task SaveStateAsync(
        ForEachModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Parents.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
