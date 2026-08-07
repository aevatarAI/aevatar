using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Race / select module: dispatches N parallel sub-steps and completes with the first
/// successful result, discarding the rest. If all fail, the step fails.
/// Unlike <see cref="ParallelFanOutModule"/>, race does not wait for all branches.
/// </summary>
public sealed class RaceModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "race";
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();

    public string Name => "race";
    public int Priority => 5;

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
            if (request.StepType != "race") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var raceKey = BuildRaceKey(runId, request.StepId);

            var workers = WorkflowParameterValueParser.GetStringList(request.Parameters, "workers", "worker_roles");
            var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
                WorkflowParameterValueParser.GetString(
                    request.Parameters,
                    "llm_call",
                    "sub_step_type",
                    "child_step_type"));
            var subTargetRole = WorkflowParameterValueParser.GetString(
                request.Parameters,
                request.TargetRole,
                "sub_target_role",
                "child_target_role");

            var count = workers.Count > 0 ? workers.Count
                : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 2, 1, 10, "count", "race_count");

            if (workers.Count == 0 &&
                string.IsNullOrWhiteSpace(subTargetRole) &&
                string.Equals(subStepType, "llm_call", StringComparison.Ordinal))
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = false,
                    Error = "race requires parameters.workers (CSV/JSON list) or target_role for llm_call workers",
                }, TopologyAudience.Self, ct);
                return;
            }

            var state = WorkflowExecutionStateAccess.Load<RaceModuleState>(ctx, ModuleStateKey);
            state.Races[raceKey] = new RaceState
            {
                Total = count,
                Received = 0,
                Resolved = false,
            };
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation("Race {StepId}: dispatching {Count} branches", request.StepId, count);

            for (var i = 0; i < count; i++)
            {
                var role = i < workers.Count ? workers[i] : subTargetRole;
                var child = new StepRequestEvent
                {
                    StepId = $"{request.StepId}_race_{i}",
                    StepType = subStepType,
                    RunId = runId,
                    Input = request.Input,
                    TargetRole = role ?? "",
                    ExternalInvocation = request.ExternalInvocation?.Clone(),
                };
                foreach (var (key, value) in ResolveSubStepParameters(request.Parameters, request.Input, role, i))
                    child.Parameters[key] = value;

                await ctx.PublishAsync(child, TopologyAudience.Self, ct);
            }
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var parent = ExtractParent(evt.StepId);
            if (parent == null) return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var raceKey = BuildRaceKey(runId, parent);
            var stateContainer = WorkflowExecutionStateAccess.Load<RaceModuleState>(ctx, ModuleStateKey);
            if (!stateContainer.Races.TryGetValue(raceKey, out var state)) return;

            state.Received++;
            stateContainer.Races[raceKey] = state;

            if (evt.Success && !state.Resolved)
            {
                state.Resolved = true;
                stateContainer.Races[raceKey] = state;
                await SaveStateAsync(stateContainer, ctx, ct);
                ctx.Logger.LogInformation("Race {StepId}: winner={Winner}", parent, evt.StepId);

                var completed = new StepCompletedEvent
                {
                    StepId = parent, RunId = runId, Success = true, Output = evt.Output, WorkerId = evt.WorkerId,
                };
                completed.Annotations["race.winner"] = evt.StepId;
                await ctx.PublishAsync(completed, TopologyAudience.Self, ct);

                if (state.Received >= state.Total)
                {
                    stateContainer.Races.Remove(raceKey);
                    await SaveStateAsync(stateContainer, ctx, CancellationToken.None);
                }
                return;
            }

            if (state.Received >= state.Total)
            {
                stateContainer.Races.Remove(raceKey);
                await SaveStateAsync(stateContainer, ctx, ct);
                if (!state.Resolved)
                {
                    ctx.Logger.LogWarning("Race {StepId}: all {Count} branches failed", parent, state.Total);
                    await ctx.PublishAsync(new StepCompletedEvent
                    {
                        StepId = parent, RunId = runId, Success = false, Error = "all race branches failed",
                    }, TopologyAudience.Self, ct);
                }
                return;
            }

            await SaveStateAsync(stateContainer, ctx, ct);
        }
    }

    private static string? ExtractParent(string stepId)
    {
        var idx = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return idx > 0 ? stepId[..idx] : null;
    }

    private static string BuildRaceKey(string runId, string stepId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}";

    private Dictionary<string, string> ResolveSubStepParameters(
        IReadOnlyDictionary<string, string> parameters,
        string input,
        string? worker,
        int index)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = input,
            ["index"] = index.ToString(CultureInfo.InvariantCulture),
            ["worker"] = worker ?? string.Empty,
        };
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            if (key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase))
                resolved[key["sub_param_".Length..]] = _expressionEvaluator.Evaluate(value, variables);
        }

        return resolved;
    }

    private static Task SaveStateAsync(
        RaceModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Races.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
