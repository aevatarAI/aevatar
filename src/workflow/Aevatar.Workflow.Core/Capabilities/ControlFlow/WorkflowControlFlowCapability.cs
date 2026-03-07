using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowControlFlowCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "control_flow",
        SupportedStepTypes: ["delay", "wait_signal", "race", "while"],
        SupportedInternalSignalTypeUrls:
        [
            WorkflowCapabilityRoutes.For<WorkflowStepTimeoutFiredEvent>(),
            WorkflowCapabilityRoutes.For<WorkflowStepRetryBackoffFiredEvent>(),
            WorkflowCapabilityRoutes.For<DelayStepTimeoutFiredEvent>(),
            WorkflowCapabilityRoutes.For<WaitSignalTimeoutFiredEvent>(),
        ]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "delay" => HandleDelayStepAsync(request, read, write, effects, ct),
            "wait_signal" => HandleWaitSignalStepAsync(request, read, write, effects, ct),
            "race" => HandleRaceStepAsync(request, read, write, effects, ct),
            "while" => HandleWhileStepAsync(request, read, write, effects, ct),
            _ => Task.CompletedTask,
        };

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read)
    {
        var state = read.State;
        var raceParent = WorkflowParentStepIds.TryGetRaceParent(evt.StepId);
        if (!string.IsNullOrWhiteSpace(raceParent) && state.PendingRaceSteps.ContainsKey(raceParent))
            return true;

        var whileParent = WorkflowParentStepIds.TryGetWhileParent(evt.StepId);
        return !string.IsNullOrWhiteSpace(whileParent) && state.PendingWhileSteps.ContainsKey(whileParent);
    }

    public async Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (await TryHandleRaceCompletionAsync(evt, read, write, ct))
            return;

        await TryHandleWhileCompletionAsync(evt, read, write, effects, ct);
    }

    public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        var state = read.State;
        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowStepTimeoutFiredEvent>();
            return WorkflowRunIdNormalizer.Normalize(evt.RunId) == state.RunId &&
                   !string.IsNullOrWhiteSpace(evt.StepId) &&
                   state.PendingTimeouts.ContainsKey(evt.StepId);
        }

        if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowStepRetryBackoffFiredEvent>();
            return WorkflowRunIdNormalizer.Normalize(evt.RunId) == state.RunId &&
                   !string.IsNullOrWhiteSpace(evt.StepId) &&
                   state.PendingRetryBackoffs.ContainsKey(evt.StepId);
        }

        if (payload.Is(DelayStepTimeoutFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<DelayStepTimeoutFiredEvent>();
            return WorkflowRunIdNormalizer.Normalize(evt.RunId) == state.RunId &&
                   !string.IsNullOrWhiteSpace(evt.StepId) &&
                   state.PendingDelays.ContainsKey(evt.StepId);
        }

        if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<WaitSignalTimeoutFiredEvent>();
            return WorkflowRunIdNormalizer.Normalize(evt.RunId) == state.RunId &&
                   !string.IsNullOrWhiteSpace(evt.StepId) &&
                   state.PendingSignalWaits.ContainsKey(evt.StepId);
        }

        return false;
    }

    public Task HandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return Task.CompletedTask;

        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            return HandleStepTimeoutAsync(
                payload.Unpack<WorkflowStepTimeoutFiredEvent>(),
                envelope,
                read,
                write,
                ct);
        }

        if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            return HandleRetryBackoffAsync(
                payload.Unpack<WorkflowStepRetryBackoffFiredEvent>(),
                envelope,
                read,
                write,
                effects,
                ct);
        }

        if (payload.Is(DelayStepTimeoutFiredEvent.Descriptor))
        {
            return HandleDelayTimeoutAsync(
                payload.Unpack<DelayStepTimeoutFiredEvent>(),
                envelope,
                read,
                write,
                ct);
        }

        if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            return HandleWaitSignalTimeoutAsync(
                payload.Unpack<WaitSignalTimeoutFiredEvent>(),
                envelope,
                read,
                write,
                ct);
        }

        return Task.CompletedTask;
    }

    public bool CanHandleResponse(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleChildRunCompletion(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read)
    {
        return string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), read.RunId, StringComparison.Ordinal) &&
               WorkflowPendingTokenLookup.TryResolvePendingSignalWait(read.State, evt.WaitToken, out _);
    }

    public async Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (!WorkflowPendingTokenLookup.TryResolvePendingSignalWait(read.State, evt.WaitToken, out var pending))
            return;

        var next = read.State.Clone();
        next.PendingSignalWaits.Remove(pending.StepId);
        next.Status = "active";
        await write.PersistStateAsync(next, ct);

        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = read.RunId,
            Success = true,
            Output = string.IsNullOrEmpty(evt.Payload) ? pending.Input : evt.Payload,
        }, EventDirection.Self, ct);
    }

    private static async Task HandleDelayStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await write.PublishAsync(new StepCompletedEvent
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
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = true,
                Output = request.Input ?? string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var state = read.State;
        var next = state.Clone();
        next.PendingDelays[stepId] = new WorkflowPendingDelayState
        {
            StepId = stepId,
            Input = request.Input ?? string.Empty,
            DurationMs = durationMs,
            SemanticGeneration = WorkflowSemanticGeneration.Next(
                state.PendingDelays.TryGetValue(stepId, out var existing) ? existing.SemanticGeneration : 0),
        };
        await write.PersistStateAsync(next, ct);

        await effects.ScheduleWorkflowCallbackAsync(
            WorkflowCallbackKeys.BuildDelayCallbackId(runId, stepId),
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

    private static async Task HandleWaitSignalStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        var signalName = NormalizeSignalName(
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

        var state = read.State;
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
                ? WorkflowSemanticGeneration.Next(
                    state.PendingSignalWaits.TryGetValue(stepId, out var existing) ? existing.TimeoutGeneration : 0)
                : 0,
            WaitToken = Guid.NewGuid().ToString("N"),
        };
        await write.PersistStateAsync(next, ct);

        await write.PublishAsync(new WaitingForSignalEvent
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

        await effects.ScheduleWorkflowCallbackAsync(
            WorkflowCallbackKeys.BuildWaitSignalCallbackId(runId, signalName, stepId),
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

    private static async Task HandleRaceStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        await effects.EnsureAgentTreeAsync(ct);

        var workers = WorkflowParameterValueParser.GetStringList(request.Parameters, "workers", "worker_roles");
        var count = workers.Count > 0
            ? workers.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 2, 1, 10, "count", "race_count");

        if (workers.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = read.RunId,
                Success = false,
                Error = "race requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var next = read.State.Clone();
        next.PendingRaceSteps[request.StepId] = new WorkflowRaceState
        {
            Total = count,
            Received = 0,
            Resolved = false,
        };
        await write.PersistStateAsync(next, ct);

        for (var index = 0; index < count; index++)
        {
            var role = index < workers.Count ? workers[index] : request.TargetRole;
            await effects.DispatchInternalStepAsync(
                read.RunId,
                request.StepId,
                $"{request.StepId}_race_{index}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private static async Task HandleWhileStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var maxIterations = int.TryParse(request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max)
            ? Math.Clamp(max, 1, 1_000_000)
            : 10;
        var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
            subParameters[key["sub_param_".Length..]] = value;

        var next = read.State.Clone();
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
        await write.PersistStateAsync(next, ct);

        await effects.DispatchWhileIterationAsync(next.PendingWhileSteps[request.StepId], request.Input ?? string.Empty, ct);
    }

    private static async Task<bool> TryHandleRaceCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var state = read.State;
        var parentStepId = WorkflowParentStepIds.TryGetRaceParent(evt.StepId);
        if (string.IsNullOrWhiteSpace(parentStepId) || !state.PendingRaceSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingRaceSteps[parentStepId].Received = pending.Received + 1;
        if (evt.Success && !pending.Resolved)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await write.PersistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = true,
                Output = evt.Output,
                WorkerId = evt.WorkerId,
            };
            completed.Metadata["race.winner"] = evt.StepId;
            await write.PublishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        if (next.PendingRaceSteps[parentStepId].Received >= pending.Total)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await write.PersistStateAsync(next, ct);
            if (!pending.Resolved)
            {
                await write.PublishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = state.RunId,
                    Success = false,
                    Error = "all race branches failed",
                }, EventDirection.Self, ct);
            }

            return true;
        }

        next.PendingRaceSteps[parentStepId].Resolved = pending.Resolved || evt.Success;
        await write.PersistStateAsync(next, ct);
        return true;
    }

    private static async Task<bool> TryHandleWhileCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        var parentStepId = WorkflowParentStepIds.TryGetWhileParent(evt.StepId);
        if (string.IsNullOrWhiteSpace(parentStepId) || !state.PendingWhileSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = state.Clone();
        next.StepExecutions.Remove(evt.StepId);
        if (!evt.Success)
        {
            next.PendingWhileSteps.Remove(parentStepId);
            await write.PersistStateAsync(next, ct);
            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = state.RunId,
                Success = false,
                Output = evt.Output,
                Error = evt.Error,
            }, EventDirection.Self, ct);
            return true;
        }

        var nextIteration = pending.Iteration + 1;
        if (nextIteration < pending.MaxIterations &&
            EvaluateWhileCondition(pending, evt.Output ?? string.Empty, nextIteration))
        {
            next.PendingWhileSteps[parentStepId].Iteration = nextIteration;
            await write.PersistStateAsync(next, ct);
            await effects.DispatchWhileIterationAsync(next.PendingWhileSteps[parentStepId], evt.Output ?? string.Empty, ct);
            return true;
        }

        next.PendingWhileSteps.Remove(parentStepId);
        await write.PersistStateAsync(next, ct);
        var completed = new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = state.RunId,
            Success = true,
            Output = evt.Output,
        };
        completed.Metadata["while.iterations"] = nextIteration.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.max_iterations"] = pending.MaxIterations.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.condition"] = pending.ConditionExpression;
        await write.PublishAsync(completed, EventDirection.Self, ct);
        return true;
    }

    private static async Task HandleStepTimeoutAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var state = read.State;
        if (!TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingTimeouts.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowSemanticGeneration.Matches(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private static async Task HandleRetryBackoffAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        var state = read.State;
        if (!TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingRetryBackoffs.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowSemanticGeneration.Matches(envelope, pending.SemanticGeneration))
            return;

        var step = read.CompiledWorkflow?.GetStep(evt.StepId);
        if (step == null || !state.StepExecutions.TryGetValue(evt.StepId, out var execution))
            return;

        var next = state.Clone();
        next.PendingRetryBackoffs.Remove(evt.StepId);
        await write.PersistStateAsync(next, ct);
        await effects.DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, state.RunId, ct);
    }

    private static async Task HandleDelayTimeoutAsync(
        DelayStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var state = read.State;
        if (!TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingDelays.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowSemanticGeneration.Matches(envelope, pending.SemanticGeneration))
            return;

        var next = state.Clone();
        next.PendingDelays.Remove(evt.StepId);
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    private static async Task HandleWaitSignalTimeoutAsync(
        WaitSignalTimeoutFiredEvent evt,
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
    {
        var state = read.State;
        if (!TryMatchRunAndStep(state.RunId, evt.RunId, evt.StepId))
            return;
        if (!state.PendingSignalWaits.TryGetValue(evt.StepId, out var pending))
            return;
        if (!WorkflowSemanticGeneration.Matches(envelope, pending.TimeoutGeneration))
            return;

        var next = state.Clone();
        next.PendingSignalWaits.Remove(evt.StepId);
        next.Status = "active";
        await write.PersistStateAsync(next, ct);
        await write.PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = state.RunId,
            Success = false,
            Error = $"signal '{pending.SignalName}' timed out after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private static bool EvaluateWhileCondition(
        WorkflowWhileState state,
        string output,
        int nextIteration)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = output,
            ["output"] = output,
            ["iteration"] = nextIteration.ToString(CultureInfo.InvariantCulture),
            ["max_iterations"] = state.MaxIterations.ToString(CultureInfo.InvariantCulture),
        };

        var evaluator = new WorkflowExpressionEvaluator();
        var evaluation = state.ConditionExpression.Contains("${", StringComparison.Ordinal)
            ? evaluator.Evaluate(state.ConditionExpression, variables)
            : evaluator.EvaluateExpression(state.ConditionExpression, variables);

        if (string.IsNullOrWhiteSpace(evaluation))
            return false;
        if (bool.TryParse(evaluation, out var boolValue))
            return boolValue;
        if (double.TryParse(evaluation, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Math.Abs(number) >= 1e-9;
        return true;
    }

    private static bool TryMatchRunAndStep(string activeRunId, string runId, string stepId) =>
        string.Equals(WorkflowRunIdNormalizer.Normalize(runId), activeRunId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(stepId);

    private static string NormalizeSignalName(string signalName) =>
        string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim().ToLowerInvariant();
}
