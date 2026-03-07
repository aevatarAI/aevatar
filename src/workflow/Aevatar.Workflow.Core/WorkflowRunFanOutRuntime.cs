using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunFanOutRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;
    private readonly WorkflowInternalStepDispatchHandler _dispatchInternalStepAsync;

    public WorkflowRunFanOutRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        WorkflowRunEffectDispatcher effectDispatcher,
        WorkflowInternalStepDispatchHandler dispatchInternalStepAsync)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _effectDispatcher = effectDispatcher ?? throw new ArgumentNullException(nameof(effectDispatcher));
        _dispatchInternalStepAsync = dispatchInternalStepAsync ?? throw new ArgumentNullException(nameof(dispatchInternalStepAsync));
    }

    public async Task HandleParallelStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await _effectDispatcher.EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workerRoles = new List<string>();
        if (request.Parameters.TryGetValue("workers", out var workers) && !string.IsNullOrWhiteSpace(workers))
            workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workers));

        var count = workerRoles.Count > 0
            ? workerRoles.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 3, 1, 16, "parallel_count", "count");

        if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "parallel requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var voteStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("vote_step_type", out var voteType) ? voteType : string.Empty);
        var next = _stateAccessor().Clone();
        var parallelState = new WorkflowParallelState
        {
            ExpectedCount = count,
            VoteStepType = voteStepType,
            VoteStepId = string.Empty,
            WorkersSuccess = false,
        };
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase)))
            parallelState.VoteParameters[key["vote_param_".Length..]] = value;
        next.PendingParallelSteps[request.StepId] = parallelState;
        await _persistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workerRoles.Count ? workerRoles[i] : request.TargetRole;
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_sub_{i}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    public async Task HandleForEachStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "parallel", "sub_step_type", "step"));
        var subTargetRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "sub_target_role",
            "sub_role");

        var next = _stateAccessor().Clone();
        next.PendingForeachSteps[request.StepId] = new WorkflowForEachState
        {
            ExpectedCount = items.Length,
        };
        await _persistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
                subParameters[key["sub_param_".Length..]] = value;
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_item_{i}",
                subStepType,
                items[i].Trim(),
                subTargetRole ?? string.Empty,
                subParameters,
                ct);
        }
    }

    public async Task HandleMapReduceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await _publishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var mapType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "llm_call", "map_step_type", "map_type"));
        var mapRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "map_target_role",
            "map_role");
        var reduceType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("reduce_step_type", out var reduceTypeRaw)
                ? reduceTypeRaw
                : request.Parameters.GetValueOrDefault("reduce_type", "llm_call"));
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

        var next = _stateAccessor().Clone();
        next.PendingMapReduceSteps[request.StepId] = new WorkflowMapReduceState
        {
            MapCount = items.Length,
            ReduceType = reduceType,
            ReduceRole = reduceRole ?? string.Empty,
            ReducePromptPrefix = reducePrefix,
            ReduceStepId = string.Empty,
        };
        await _persistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            await _dispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_map_{i}",
                mapType,
                items[i],
                mapRole ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }
}
