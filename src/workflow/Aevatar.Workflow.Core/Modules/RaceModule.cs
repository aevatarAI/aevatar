using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Race / select module: dispatches N parallel sub-steps and completes with the first
/// successful result, discarding the rest. If all fail, the step fails.
/// Unlike <see cref="ParallelFanOutModule"/>, race does not wait for all branches.
/// </summary>
public sealed class RaceModule : IEventModule
{
    private readonly Dictionary<(string RunId, string StepId), RaceState> _races = [];

    public string Name => "race";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "race") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var parentKey = (runId, request.StepId);

            var workers = WorkflowParameterValueParser.GetStringList(request.Parameters, "workers", "worker_roles");

            var count = workers.Count > 0 ? workers.Count
                : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 2, 1, 10, "count", "race_count");

            if (workers.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = false,
                    Error = "race requires parameters.workers (CSV/JSON list) or target_role",
                }, EventDirection.Self, ct);
                return;
            }

            _races[parentKey] = new RaceState(count, 0, false);

            ctx.Logger.LogInformation("Race {StepId}: dispatching {Count} branches", request.StepId, count);

            for (var i = 0; i < count; i++)
            {
                var role = i < workers.Count ? workers[i] : request.TargetRole;
                await ctx.PublishAsync(new StepRequestEvent
                {
                    StepId = $"{request.StepId}_race_{i}",
                    StepType = "llm_call",
                    RunId = runId,
                    Input = request.Input,
                    TargetRole = role ?? "",
                }, EventDirection.Self, ct);
            }
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var parent = ExtractParent(evt.StepId);
            if (parent == null) return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var parentKey = (runId, parent);
            if (!_races.TryGetValue(parentKey, out var state)) return;

            state = state with { Received = state.Received + 1 };
            _races[parentKey] = state;

            if (evt.Success && !state.Resolved)
            {
                _races[parentKey] = state with { Resolved = true };
                ctx.Logger.LogInformation("Race {StepId}: winner={Winner}", parent, evt.StepId);

                var completed = new StepCompletedEvent
                {
                    StepId = parent, RunId = runId, Success = true, Output = evt.Output, WorkerId = evt.WorkerId,
                };
                completed.Metadata["race.winner"] = evt.StepId;
                await ctx.PublishAsync(completed, EventDirection.Self, ct);

                if (state.Received >= state.Total)
                    _races.Remove(parentKey);
                return;
            }

            if (state.Received >= state.Total)
            {
                _races.Remove(parentKey);
                if (!state.Resolved)
                {
                    ctx.Logger.LogWarning("Race {StepId}: all {Count} branches failed", parent, state.Total);
                    await ctx.PublishAsync(new StepCompletedEvent
                    {
                        StepId = parent, RunId = runId, Success = false, Error = "all race branches failed",
                    }, EventDirection.Self, ct);
                }
            }
        }
    }

    private static string? ExtractParent(string stepId)
    {
        var idx = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return idx > 0 ? stepId[..idx] : null;
    }

    private sealed record RaceState(int Total, int Received, bool Resolved);
}
