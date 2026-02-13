// ─────────────────────────────────────────────────────────────
// ForEachModule - iterates over a delimited list of items,
// dispatching a configurable sub-step for each item and
// collecting all results before publishing completion.
//
// Used by MAKER workflows for per-subtask parallel+vote,
// but is a general-purpose primitive.
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>
/// ForEach iteration module. Handles step_type == "foreach".
/// Splits input by delimiter, dispatches a sub-step per item,
/// collects results, and publishes merged output.
/// </summary>
public sealed class ForEachModule : IEventModule
{
    private readonly Dictionary<string, int> _expected = [];
    private readonly Dictionary<string, List<StepCompletedEvent>> _collected = [];

    /// <summary>Module name.</summary>
    public string Name => "foreach";

    /// <summary>Priority.</summary>
    public int Priority => 4;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true ||
        envelope.Payload?.TypeUrl?.Contains("StepCompletedEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload!.TypeUrl.Contains("StepRequestEvent"))
        {
            var evt = envelope.Payload.Unpack<StepRequestEvent>();
            if (evt.StepType != "foreach") return;

            // ─── Parameters ───
            var delimiter = evt.Parameters.TryGetValue("delimiter", out var d) ? d : "\n---\n";
            var subStepType = evt.Parameters.TryGetValue("sub_step_type", out var sst) ? sst : "parallel";
            var subTargetRole = evt.Parameters.TryGetValue("sub_target_role", out var str) ? str : evt.TargetRole;

            // ─── Split input into items ───
            var items = evt.Input.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0)
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = evt.StepId, RunId = evt.RunId,
                    Success = true, Output = "",
                }, EventDirection.Self, ct);
                return;
            }

            _expected[evt.StepId] = items.Length;
            _collected[evt.StepId] = [];

            ctx.Logger.LogInformation(
                "ForEach {StepId}: {Count} items, sub_step_type={SubType}",
                evt.StepId, items.Length, subStepType);

            // ─── Dispatch sub-step for each item ───
            for (var i = 0; i < items.Length; i++)
            {
                var subReq = new StepRequestEvent
                {
                    StepId = $"{evt.StepId}_item_{i}",
                    StepType = subStepType,
                    RunId = evt.RunId,
                    Input = items[i].Trim(),
                    TargetRole = subTargetRole ?? "",
                };

                // Forward parent parameters to sub-steps (prefixed ones)
                foreach (var (key, value) in evt.Parameters)
                {
                    if (key.StartsWith("sub_param_"))
                        subReq.Parameters[key["sub_param_".Length..]] = value;
                }

                await ctx.PublishAsync(subReq, EventDirection.Self, ct);
            }
        }
        else
        {
            // ─── Collect sub-step completions ───
            var evt = envelope.Payload.Unpack<StepCompletedEvent>();
            // Only collect direct foreach item completions: "<parent>_item_<index>".
            // Ignore nested children like "_item_0_sub_1" or "_item_0_vote".
            var parent = TryGetParentFromDirectItemStepId(evt.StepId);

            if (parent == null || !_collected.ContainsKey(parent)) return;

            _collected[parent].Add(evt);

            if (_collected[parent].Count >= _expected[parent])
            {
                var results = _collected[parent];
                var allSuccess = results.All(r => r.Success);
                var merged = string.Join("\n---\n", results.Select(r => r.Output));

                ctx.Logger.LogInformation(
                    "ForEach {StepId}: all {Count} items completed, success={Success}",
                    parent, results.Count, allSuccess);

                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = parent, RunId = evt.RunId,
                    Success = allSuccess, Output = merged,
                }, EventDirection.Self, ct);

                _collected.Remove(parent);
                _expected.Remove(parent);
            }
        }
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
}
