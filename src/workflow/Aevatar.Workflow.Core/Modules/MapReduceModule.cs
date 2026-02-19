using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Map-Reduce module: splits input into items, maps each through a sub-step in parallel,
/// then reduces all results through a second sub-step.
/// </summary>
public sealed class MapReduceModule : IEventModule
{
    private readonly Dictionary<string, MapReduceState> _states = [];
    private readonly Dictionary<string, string> _reduceToParent = [];

    public string Name => "map_reduce";
    public int Priority => 4;

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
            if (request.StepType != "map_reduce") return;

            var delimiter = request.Parameters.GetValueOrDefault("delimiter", "\n---\n");
            var items = (request.Input ?? "").Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0)
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId, Success = true, Output = "",
                }, EventDirection.Self, ct);
                return;
            }

            var mapType = request.Parameters.GetValueOrDefault("map_step_type", "llm_call");
            var mapRole = request.Parameters.GetValueOrDefault("map_target_role", request.TargetRole);
            var reduceType = request.Parameters.GetValueOrDefault("reduce_step_type", "llm_call");
            var reduceRole = request.Parameters.GetValueOrDefault("reduce_target_role", request.TargetRole);
            var reducePrefix = request.Parameters.GetValueOrDefault("reduce_prompt_prefix", "");

            _states[request.StepId] = new MapReduceState(
                items.Length, [], reduceType, reduceRole ?? "", reducePrefix);

            ctx.Logger.LogInformation("MapReduce {StepId}: map {Count} items via {Type}", request.StepId, items.Length, mapType);

            for (var i = 0; i < items.Length; i++)
            {
                await ctx.PublishAsync(new StepRequestEvent
                {
                    StepId = $"{request.StepId}_map_{i}",
                    StepType = mapType,
                    Input = items[i],
                    TargetRole = mapRole ?? "",
                }, EventDirection.Self, ct);
            }
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();

            if (_reduceToParent.Remove(evt.StepId, out var reduceParent))
            {
                _states.Remove(reduceParent);
                var completed = new StepCompletedEvent
                {
                    StepId = reduceParent, Success = evt.Success, Output = evt.Output, Error = evt.Error,
                };
                completed.Metadata["map_reduce.phase"] = "reduce";
                await ctx.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var parent = ExtractMapParent(evt.StepId);
            if (parent == null || !_states.TryGetValue(parent, out var state)) return;

            state.Results.Add(evt);
            if (state.Results.Count < state.MapCount) return;

            var allSuccess = state.Results.All(r => r.Success);
            var merged = string.Join("\n---\n", state.Results.Select(r => r.Output));

            if (!allSuccess || string.IsNullOrWhiteSpace(state.ReduceType))
            {
                _states.Remove(parent);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = parent, Success = allSuccess, Output = merged,
                    Error = allSuccess ? "" : "one or more map steps failed",
                }, EventDirection.Self, ct);
                return;
            }

            var reduceInput = string.IsNullOrEmpty(state.ReducePromptPrefix)
                ? merged
                : state.ReducePromptPrefix.TrimEnd() + "\n\n" + merged;

            var reduceStepId = $"{parent}_reduce";
            _reduceToParent[reduceStepId] = parent;

            ctx.Logger.LogInformation("MapReduce {StepId}: reduce via {Type}", parent, state.ReduceType);

            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = reduceStepId,
                StepType = state.ReduceType,
                Input = reduceInput,
                TargetRole = state.ReduceRole,
            }, EventDirection.Self, ct);
        }
    }

    private static string? ExtractMapParent(string stepId)
    {
        var idx = stepId.LastIndexOf("_map_", StringComparison.Ordinal);
        return idx > 0 ? stepId[..idx] : null;
    }

    private sealed class MapReduceState(
        int mapCount,
        List<StepCompletedEvent> results,
        string reduceType,
        string reduceRole,
        string reducePromptPrefix)
    {
        public int MapCount => mapCount;
        public List<StepCompletedEvent> Results { get; } = results;
        public string ReduceType => reduceType;
        public string ReduceRole => reduceRole;
        public string ReducePromptPrefix => reducePromptPrefix;
    }
}
