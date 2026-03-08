using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Map-Reduce module: splits input into items, maps each through a sub-step in parallel,
/// then reduces all results through a second sub-step.
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
            var parentKey = (runId, request.StepId);

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
                    StepId = request.StepId, RunId = runId, Success = true, Output = "",
                }, EventDirection.Self, ct);
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
            };
            state.Parents[parentKey.StepId] = parentState;
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation("MapReduce {StepId}: map {Count} items via {Type}", request.StepId, items.Length, mapType);

            for (var i = 0; i < items.Length; i++)
            {
                await ctx.PublishAsync(new StepRequestEvent
                {
                    StepId = $"{request.StepId}_map_{i}",
                    StepType = mapType,
                    RunId = runId,
                    Input = items[i],
                    TargetRole = mapRole ?? "",
                }, EventDirection.Self, ct);
            }
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);

            var state = WorkflowExecutionStateAccess.Load<MapReduceModuleState>(ctx, ModuleStateKey);
            if (state.ReduceStepToParent.TryGetValue(evt.StepId, out var reduceParent))
            {
                state.ReduceStepToParent.Remove(evt.StepId);
                state.Parents.Remove(reduceParent);
                await SaveStateAsync(state, ctx, ct);
                var completed = new StepCompletedEvent
                {
                    StepId = reduceParent, RunId = runId, Success = evt.Success, Output = evt.Output, Error = evt.Error,
                };
                completed.Metadata["map_reduce.phase"] = "reduce";
                await ctx.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var parent = ExtractMapParent(evt.StepId);
            if (parent == null) return;
            if (!state.Parents.TryGetValue(parent, out var parentState)) return;

            parentState.Results.Add(evt.ToMapReduceItemResult());
            state.Parents[parent] = parentState;
            if (parentState.Results.Count < parentState.MapCount)
            {
                await SaveStateAsync(state, ctx, ct);
                return;
            }

            var allSuccess = parentState.Results.All(r => r.Success);
            var merged = string.Join("\n---\n", parentState.Results.Select(r => r.Output));

            if (!allSuccess || string.IsNullOrWhiteSpace(parentState.ReduceType))
            {
                state.Parents.Remove(parent);
                await SaveStateAsync(state, ctx, ct);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = parent, RunId = runId, Success = allSuccess, Output = merged,
                    Error = allSuccess ? "" : "one or more map steps failed",
                }, EventDirection.Self, ct);
                return;
            }

            var reduceInput = string.IsNullOrEmpty(parentState.ReducePromptPrefix)
                ? merged
                : parentState.ReducePromptPrefix.TrimEnd() + "\n\n" + merged;

            var reduceStepId = $"{parent}_reduce";
            state.ReduceStepToParent[reduceStepId] = parent;
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation("MapReduce {StepId}: reduce via {Type}", parent, parentState.ReduceType);

            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = reduceStepId,
                StepType = parentState.ReduceType,
                RunId = runId,
                Input = reduceInput,
                TargetRole = parentState.ReduceRole,
            }, EventDirection.Self, ct);
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
        if (state.Parents.Count == 0 && state.ReduceStepToParent.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
