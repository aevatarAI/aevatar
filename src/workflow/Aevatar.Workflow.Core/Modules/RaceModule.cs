using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var raceKey = BuildRaceKey(runId, request.StepId, request.ExecutionId, normalizedValues);

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
                    ExecutionId = request.ExecutionId,
                    Success = false,
                    Error = "race requires parameters.workers (CSV/JSON list) or target_role for llm_call workers",
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
                }, TopologyAudience.Self, ct);
                return;
            }

            var state = WorkflowExecutionStateAccess.Load<RaceModuleState>(ctx, ModuleStateKey);
            RemoveSupersededRaceAttempts(state, runId, request.StepId, raceKey);
            var race = new RaceState
            {
                Total = count,
                Received = 0,
                Resolved = false,
                ParentRunId = runId,
                ParentStepId = request.StepId,
                ParentExecutionId = request.ExecutionId,
            };

            ctx.Logger.LogInformation("Race {StepId}: dispatching {Count} branches", request.StepId, count);

            var children = new List<StepRequestEvent>(count);
            for (var i = 0; i < count; i++)
            {
                var role = i < workers.Count ? workers[i] : subTargetRole;
                var childStepId = BuildChildStepId(request.StepId, i, request.ExecutionId, normalizedValues);
                var child = new StepRequestEvent
                {
                    StepId = childStepId,
                    StepType = subStepType,
                    RunId = runId,
                    Input = request.Input,
                    TargetRole = role ?? "",
                    ExternalInvocation = request.ExternalInvocation?.Clone(),
                    ExecutionId = WorkflowExecutionValueStore.BuildInternalExecutionId(
                        runId,
                        request.ExecutionId,
                        childStepId),
                    InputValueId = request.InputValueId,
                };
                foreach (var (key, value) in ResolveSubStepParameters(request.Parameters, request.Input, role, i))
                    child.Parameters[key] = value;
                race.ChildExecutionIds[childStepId] = child.ExecutionId;
                state.ChildToRaceKey[childStepId] = raceKey;
                children.Add(child);
            }
            state.Races[raceKey] = race;
            await SaveStateAsync(state, ctx, ct);
            foreach (var child in children)
                await ctx.PublishAsync(child, TopologyAudience.Self, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            var parent = ExtractParent(evt.StepId);
            if (parent == null) return;
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            var stateContainer = WorkflowExecutionStateAccess.Load<RaceModuleState>(ctx, ModuleStateKey);
            var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
                ctx,
                WorkflowExecutionKernel.ModuleStateKey);
            var normalizedValues = WorkflowExecutionValueStore.IsNormalized(kernelState);
            var raceKey = stateContainer.ChildToRaceKey.TryGetValue(evt.StepId, out var mappedRaceKey)
                ? mappedRaceKey
                : BuildRaceKey(runId, parent, string.Empty, normalized: false);
            if (!stateContainer.Races.TryGetValue(raceKey, out var state)) return;
            if (!MatchesExpectedChild(state, evt, normalizedValues)) return;

            if (state.PendingCompletion != null)
            {
                await PublishPendingCompletionAsync(stateContainer, raceKey, state, evt, ctx, ct);
                return;
            }

            var settledIdentity = string.IsNullOrWhiteSpace(evt.ExecutionId)
                ? evt.StepId
                : evt.ExecutionId;
            if (state.SettledExecutionIds.Contains(settledIdentity))
                return;
            state.SettledExecutionIds.Add(settledIdentity);

            state.Received++;
            stateContainer.Races[raceKey] = state;

            if (evt.Success && !state.Resolved)
            {
                state.Resolved = true;
                if (!normalizedValues)
                {
                    stateContainer.Races[raceKey] = state;
                    await SaveStateAsync(stateContainer, ctx, ct);
                    ctx.Logger.LogInformation("Race {StepId}: winner={Winner}", parent, evt.StepId);
                    var completed = new StepCompletedEvent
                    {
                        StepId = state.ParentStepId,
                        RunId = state.ParentRunId,
                        ExecutionId = state.ParentExecutionId,
                        Success = true,
                        Output = evt.Output,
                        WorkerId = evt.WorkerId,
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    };
                    completed.Annotations["race.winner"] = evt.StepId;
                    await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
                    if (state.Received >= state.Total)
                    {
                        RemoveRace(stateContainer, raceKey, state);
                        await SaveStateAsync(stateContainer, ctx, CancellationToken.None);
                    }
                    return;
                }

                state.PendingCompletion = new StepCompletedEvent
                {
                    StepId = state.ParentStepId,
                    RunId = state.ParentRunId,
                    ExecutionId = state.ParentExecutionId,
                    Success = true,
                    Output = evt.Output,
                    WorkerId = evt.WorkerId,
                    OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
                };
                state.PendingCompletion.Annotations["race.winner"] = evt.StepId;
                state.WinnerStepId = evt.StepId;
                state.WinnerExecutionId = evt.ExecutionId;
                if (normalizedValues)
                {
                    await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(
                        state.PendingCompletion,
                        evt,
                        ctx,
                        ct);
                    state.WinnerOutputValueId = state.PendingCompletion.OutputSourceValueId;
                }
                stateContainer.Races[raceKey] = state;
                await SaveStateAsync(stateContainer, ctx, ct);
                ctx.Logger.LogInformation("Race {StepId}: winner={Winner}", parent, evt.StepId);

                await PublishPendingCompletionAsync(stateContainer, raceKey, state, evt, ctx, ct);

                if (state.Received >= state.Total)
                {
                    RemoveRace(stateContainer, raceKey, state);
                    await SaveStateAsync(stateContainer, ctx, CancellationToken.None);
                }
                return;
            }

            if (state.Received >= state.Total)
            {
                if (!state.Resolved)
                {
                    ctx.Logger.LogWarning("Race {StepId}: all {Count} branches failed", parent, state.Total);
                    if (!normalizedValues)
                    {
                        RemoveRace(stateContainer, raceKey, state);
                        await SaveStateAsync(stateContainer, ctx, ct);
                        await ctx.PublishAsync(new StepCompletedEvent
                        {
                            StepId = state.ParentStepId,
                            RunId = state.ParentRunId,
                            ExecutionId = state.ParentExecutionId,
                            Success = false,
                            Error = "all race branches failed",
                            OutputProvenance = WorkflowStepOutputProvenance.Produced,
                        }, TopologyAudience.Self, ct);
                        return;
                    }

                    state.PendingCompletion = new StepCompletedEvent
                    {
                        StepId = state.ParentStepId,
                        RunId = state.ParentRunId,
                        ExecutionId = state.ParentExecutionId,
                        Success = false,
                        Error = "all race branches failed",
                        OutputProvenance = WorkflowStepOutputProvenance.Produced,
                    };
                    stateContainer.Races[raceKey] = state;
                    await SaveStateAsync(stateContainer, ctx, ct);
                    await PublishPendingCompletionAsync(stateContainer, raceKey, state, evt, ctx, ct);
                }
                RemoveRace(stateContainer, raceKey, state);
                await SaveStateAsync(stateContainer, ctx, CancellationToken.None);
                return;
            }

            await SaveStateAsync(stateContainer, ctx, ct);
        }
    }

    private static async Task PublishPendingCompletionAsync(
        RaceModuleState stateContainer,
        string raceKey,
        RaceState state,
        StepCompletedEvent source,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = state.PendingCompletion
            ?? throw new InvalidOperationException($"Race '{raceKey}' has no pending completion.");
        if (completion.OutputProvenance == WorkflowStepOutputProvenance.ReferencedStepOutput &&
            string.IsNullOrWhiteSpace(completion.OutputReferenceId))
        {
            if (!string.IsNullOrWhiteSpace(state.WinnerStepId) &&
                (!string.Equals(state.WinnerStepId, source.StepId, StringComparison.Ordinal) ||
                 !string.Equals(state.WinnerExecutionId, source.ExecutionId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Race '{raceKey}' recovery received a non-winner source completion.");
            }
            await WorkflowExecutionValueStore.ReferenceCompletedStepOutputAsync(completion, source, ctx, ct);
            stateContainer.Races[raceKey] = state;
            await SaveStateAsync(stateContainer, ctx, ct);
        }

        await ctx.PublishAsync(
            WorkflowExecutionValueStore.HydrateReferencedCompletionForPublication(completion, ctx),
            TopologyAudience.Self,
            ct);
        state.PendingCompletion = null;
        stateContainer.Races[raceKey] = state;
        await SaveStateAsync(stateContainer, ctx, CancellationToken.None);
    }

    private static string? ExtractParent(string stepId)
    {
        var idx = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return idx > 0 ? stepId[..idx] : null;
    }

    private static string BuildRaceKey(string runId, string stepId) =>
        BuildRaceKey(runId, stepId, string.Empty, normalized: false);

    private static string BuildRaceKey(
        string runId,
        string stepId,
        string executionId,
        bool normalized) =>
        normalized
            ? $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}::execution::{executionId.Trim()}"
            : $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}";

    private static string BuildChildStepId(
        string parentStepId,
        int index,
        string parentExecutionId,
        bool normalized)
    {
        if (!normalized || string.IsNullOrWhiteSpace(parentExecutionId))
            return $"{parentStepId}_race_{index}";
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(parentExecutionId.Trim())))
            .ToLowerInvariant()[..16];
        return $"{parentStepId}_execution_{hash}_race_{index}";
    }

    private static bool MatchesExpectedChild(
        RaceState state,
        StepCompletedEvent completion,
        bool normalized)
    {
        if (!string.Equals(
                WorkflowRunIdNormalizer.Normalize(state.ParentRunId),
                WorkflowRunIdNormalizer.Normalize(completion.RunId),
                StringComparison.Ordinal))
            return false;
        if (!state.ChildExecutionIds.TryGetValue(completion.StepId, out var expected))
            return !normalized;
        return !normalized || string.Equals(expected, completion.ExecutionId, StringComparison.Ordinal);
    }

    private static void RemoveSupersededRaceAttempts(
        RaceModuleState state,
        string runId,
        string stepId,
        string currentKey)
    {
        foreach (var (key, race) in state.Races.ToArray())
        {
            if (string.Equals(key, currentKey, StringComparison.Ordinal) ||
                !string.Equals(race.ParentRunId, runId, StringComparison.Ordinal) ||
                !string.Equals(race.ParentStepId, stepId, StringComparison.Ordinal))
                continue;
            RemoveRace(state, key, race);
        }
    }

    private static void RemoveRace(RaceModuleState state, string raceKey, RaceState race)
    {
        state.Races.Remove(raceKey);
        foreach (var childStepId in race.ChildExecutionIds.Keys)
            state.ChildToRaceKey.Remove(childStepId);
    }

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
