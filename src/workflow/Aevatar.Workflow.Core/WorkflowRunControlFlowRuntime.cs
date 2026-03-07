using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunControlFlowRuntime
{
    private readonly WorkflowRunRuntimeContext _context;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;

    public WorkflowRunControlFlowRuntime(
        WorkflowRunRuntimeContext context,
        WorkflowRunDispatchRuntime dispatchRuntime)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dispatchRuntime = dispatchRuntime ?? throw new ArgumentNullException(nameof(dispatchRuntime));
    }

    public async Task HandleDelayStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = "delay step requires non-empty run_id and step_id",
            }, EventDirection.Self, ct);
            return;
        }

        var durationMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            1000,
            0,
            300_000,
            "duration_ms",
            "duration",
            "delay_ms");
        if (durationMs <= 0)
        {
            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = true,
                Output = request.Input ?? string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var state = _context.State;
        var next = state.Clone();
        next.PendingDelays[stepId] = new WorkflowPendingDelayState
        {
            StepId = stepId,
            Input = request.Input ?? string.Empty,
            DurationMs = durationMs,
            SemanticGeneration = WorkflowRunSupport.NextSemanticGeneration(
                state.PendingDelays.TryGetValue(stepId, out var existing) ? existing.SemanticGeneration : 0),
        };
        await _context.PersistStateAsync(next, ct);

        await _context.ScheduleWorkflowCallbackAsync(
            WorkflowRunSupport.BuildDelayCallbackId(runId, stepId),
            TimeSpan.FromMilliseconds(durationMs),
            new DelayStepTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                DurationMs = durationMs,
            },
            next.PendingDelays[stepId].SemanticGeneration,
            stepId,
            sessionId: null,
            kind: "delay",
            ct);
    }

    public async Task HandleWaitSignalStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        var signalName = WorkflowRunSupport.NormalizeSignalName(
            WorkflowParameterValueParser.GetString(request.Parameters, "default", "signal_name", "signal"));
        var prompt = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "prompt", "message");

        var timeoutMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            0,
            0,
            3_600_000,
            "timeout_ms");
        if (timeoutMs <= 0 &&
            WorkflowParameterValueParser.TryGetBoundedInt(
                request.Parameters,
                out var timeoutSeconds,
                0,
                3_600,
                "timeout_seconds",
                "timeout"))
        {
            timeoutMs = Math.Clamp(timeoutSeconds * 1000, 0, 3_600_000);
        }

        var state = _context.State;
        var next = state.Clone();
        next.Status = "suspended";
        next.PendingSignalWaits[stepId] = new WorkflowPendingSignalWaitState
        {
            StepId = stepId,
            SignalName = signalName,
            Input = request.Input ?? string.Empty,
            Prompt = prompt,
            TimeoutMs = timeoutMs,
            TimeoutGeneration = timeoutMs > 0
                ? WorkflowRunSupport.NextSemanticGeneration(
                    state.PendingSignalWaits.TryGetValue(stepId, out var existing) ? existing.TimeoutGeneration : 0)
                : 0,
            WaitToken = Guid.NewGuid().ToString("N"),
        };
        await _context.PersistStateAsync(next, ct);

        await _context.PublishAsync(new WaitingForSignalEvent
        {
            StepId = stepId,
            SignalName = signalName,
            Prompt = prompt,
            TimeoutMs = timeoutMs,
            RunId = runId,
            WaitToken = next.PendingSignalWaits[stepId].WaitToken,
        }, EventDirection.Both, ct);

        if (timeoutMs <= 0)
            return;

        await _context.ScheduleWorkflowCallbackAsync(
            WorkflowRunSupport.BuildWaitSignalCallbackId(runId, signalName, stepId),
            TimeSpan.FromMilliseconds(timeoutMs),
            new WaitSignalTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                SignalName = signalName,
                TimeoutMs = timeoutMs,
            },
            next.PendingSignalWaits[stepId].TimeoutGeneration,
            stepId,
            sessionId: null,
            kind: "wait_signal",
            ct);
    }

    public async Task HandleRaceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await _context.EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workers = WorkflowParameterValueParser.GetStringList(request.Parameters, "workers", "worker_roles");
        var count = workers.Count > 0
            ? workers.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 2, 1, 10, "count", "race_count");

        if (workers.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await _context.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "race requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var next = _context.State.Clone();
        next.PendingRaceSteps[request.StepId] = new WorkflowRaceState
        {
            Total = count,
            Received = 0,
            Resolved = false,
        };
        await _context.PersistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workers.Count ? workers[i] : request.TargetRole;
            await _dispatchRuntime.DispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_race_{i}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    public async Task HandleWhileStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var maxIterations = int.TryParse(request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max)
            ? Math.Clamp(max, 1, 1_000_000)
            : 10;
        var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
            subParameters[key["sub_param_".Length..]] = value;

        var next = _context.State.Clone();
        next.PendingWhileSteps[request.StepId] = new WorkflowWhileState
        {
            StepId = request.StepId,
            SubStepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.Parameters.GetValueOrDefault("step", "llm_call")),
            SubTargetRole = request.TargetRole ?? string.Empty,
            Iteration = 0,
            MaxIterations = maxIterations,
            ConditionExpression = string.IsNullOrWhiteSpace(request.Parameters.GetValueOrDefault("condition", "true"))
                ? "true"
                : request.Parameters.GetValueOrDefault("condition", "true"),
        };
        foreach (var (key, value) in subParameters)
            next.PendingWhileSteps[request.StepId].SubParameters[key] = value;
        await _context.PersistStateAsync(next, ct);

        await _dispatchRuntime.DispatchWhileIterationAsync(next.PendingWhileSteps[request.StepId], request.Input ?? string.Empty, ct);
    }
}
