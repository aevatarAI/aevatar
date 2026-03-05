using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// 工作流循环驱动模块。负责接收启动与完成事件，按工作流定义依次调度步骤。
/// 统一处理步骤级 retry / on_error / timeout / branch 逻辑。
/// </summary>
public sealed class WorkflowLoopModule : IEventModule
{
    private WorkflowDefinition? _workflow;
    private readonly HashSet<string> _activeRunIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentStepByRunId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentStepInputByRunId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _variablesByRunId = new(StringComparer.Ordinal);
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _timeouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetryBackoffLease> _retryBackoffs = new(StringComparer.Ordinal);

    public string Name => "workflow_loop";
    public int Priority => 0;

    public void SetWorkflow(WorkflowDefinition workflow) => _workflow = workflow;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor) ||
                payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (_workflow == null) return;
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            var evt = payload.Unpack<StartWorkflowEvent>();
            var runId = string.IsNullOrWhiteSpace(evt.RunId)
                ? Guid.NewGuid().ToString("N")
                : WorkflowRunIdNormalizer.Normalize(evt.RunId);

            if (_activeRunIds.Contains(runId))
            {
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = "workflow run is already active",
                }, EventDirection.Both, ct);
                return;
            }

            _activeRunIds.Add(runId);
            _variablesByRunId[runId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input"] = evt.Input ?? string.Empty,
            };

            var entry = _workflow.Steps.FirstOrDefault();
            if (entry == null)
            {
                await CleanupRunAsync(runId, ctx, ct);
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = "无步骤",
                }, EventDirection.Both, ct);
                return;
            }

            await DispatchStep(entry, evt.Input ?? string.Empty, runId, ctx, ct);
        }
        else if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowStepTimeoutFiredEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
            if (string.IsNullOrWhiteSpace(runId) || !_activeRunIds.Contains(runId))
                return;

            var stepId = evt.StepId?.Trim();
            if (string.IsNullOrWhiteSpace(stepId))
                return;

            var stepRunKey = GetStepRunKey(runId, stepId);
            if (!_timeouts.TryGetValue(stepRunKey, out var expectedGeneration))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore timeout without active lease run={RunId} step={StepId}",
                    runId,
                    stepId);
                return;
            }

            if (TryReadGeneration(envelope, out var firedGeneration) &&
                firedGeneration != expectedGeneration)
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore stale timeout generation run={RunId} step={StepId} fired={FiredGeneration} expected={ExpectedGeneration}",
                    runId,
                    stepId,
                    firedGeneration,
                    expectedGeneration);
                return;
            }

            if (!_currentStepByRunId.TryGetValue(runId, out var expectedStepId) ||
                !string.Equals(expectedStepId, stepId, StringComparison.Ordinal))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore stale timeout run={RunId} step={StepId} expected={ExpectedStepId}",
                    runId,
                    stepId,
                    expectedStepId ?? "(none)");
                return;
            }

            _timeouts.Remove(stepRunKey);
            ctx.Logger.LogWarning("workflow_loop: step={StepId} timed out after {Ms}ms", stepId, evt.TimeoutMs);
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = $"TIMEOUT after {evt.TimeoutMs}ms",
            }, EventDirection.Self, ct);
        }
        else if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowStepRetryBackoffFiredEvent>();
            await HandleRetryBackoffFiredAsync(evt, envelope, ctx, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            if (string.IsNullOrWhiteSpace(evt.RunId))
            {
                ctx.Logger.LogWarning(
                    "workflow_loop: ignore completion without run_id step={StepId}",
                    evt.StepId);
                return;
            }

            var runId = ResolveRunId(evt);
            if (string.IsNullOrWhiteSpace(runId) || !_activeRunIds.Contains(runId))
                return;

            var current = _workflow.GetStep(evt.StepId);

            if (current == null)
            {
                ctx.Logger.LogDebug("workflow_loop: ignore internal completion step={StepId}", evt.StepId);
                if (_variablesByRunId.TryGetValue(runId, out var internalVars) &&
                    !string.IsNullOrWhiteSpace(evt.StepId))
                {
                    internalVars[evt.StepId] = evt.Output ?? string.Empty;
                }
                return;
            }

            if (!_currentStepByRunId.TryGetValue(runId, out var expectedStepId) ||
                !string.Equals(expectedStepId, evt.StepId, StringComparison.Ordinal))
            {
                ctx.Logger.LogWarning(
                    "workflow_loop: ignore stale completion run={RunId} step={StepId} expected={ExpectedStepId}",
                    runId,
                    evt.StepId,
                    expectedStepId ?? "(none)");
                return;
            }

            var stepRunKey = GetStepRunKey(runId, evt.StepId);
            await CancelTimeoutAsync(runId, evt.StepId, ctx, ct);
            if (!evt.Success && _retryBackoffs.ContainsKey(stepRunKey))
            {
                ctx.Logger.LogDebug(
                    "workflow_loop: ignore duplicate failed completion while retry backoff is pending run={RunId} step={StepId}",
                    runId,
                    evt.StepId);
                return;
            }

            if (evt.Success)
                await CancelRetryBackoffAsync(runId, evt.StepId, ctx, CancellationToken.None);

            var outputPreview = (evt.Output ?? "").Length > 200 ? evt.Output![..200] + "..." : evt.Output ?? "";
            if (evt.Success)
            {
                ctx.Logger.LogInformation(
                    "workflow_loop: step={StepId} completed success={Success} output=({Len} chars) {Preview}",
                    evt.StepId,
                    evt.Success,
                    (evt.Output ?? "").Length,
                    outputPreview);
            }
            else
            {
                ctx.Logger.LogError(
                    "workflow_loop: step={StepId} failed run={RunId} error={Error} output=({Len} chars) {Preview}",
                    evt.StepId,
                    runId,
                    string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                    (evt.Output ?? "").Length,
                    outputPreview);
            }

            if (_variablesByRunId.TryGetValue(runId, out var varsForRun))
            {
                if (evt.Metadata.TryGetValue("assign.target", out var assignTarget) &&
                    !string.IsNullOrWhiteSpace(assignTarget))
                {
                    var assignValue = evt.Metadata.TryGetValue("assign.value", out var valueFromMetadata)
                        ? valueFromMetadata
                        : evt.Output ?? string.Empty;
                    varsForRun[assignTarget] = assignValue;
                }

                if (!string.IsNullOrWhiteSpace(evt.StepId))
                    varsForRun[evt.StepId] = evt.Output ?? string.Empty;
                varsForRun["input"] = evt.Output ?? string.Empty;
            }

            if (!evt.Success)
            {
                if (IsTimeoutError(evt.Error))
                {
                    ctx.Logger.LogError(
                        "workflow_loop: run={RunId} step={StepId} timed out and run will fail. error={Error}",
                        runId,
                        evt.StepId,
                        evt.Error);
                    await CleanupRunAsync(runId, ctx, ct);
                    await ctx.PublishAsync(new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = false,
                        Error = evt.Error,
                    }, EventDirection.Both, ct);
                    return;
                }

                if (await TryRetryAsync(current, evt, runId, ctx, ct)) return;
                if (await TryOnErrorAsync(current, evt, runId, ctx, ct)) return;

                ctx.Logger.LogError(
                    "workflow_loop: run={RunId} step={StepId} failed and no retry/on_error resolved. error={Error}",
                    runId,
                    evt.StepId,
                    evt.Error);
                await CleanupRunAsync(runId, ctx, ct);
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = evt.Error,
                }, EventDirection.Both, ct);
                return;
            }

            _retryAttempts.Remove(stepRunKey);
            _retryBackoffs.Remove(stepRunKey);

            StepDefinition? next;
            if (evt.Metadata.TryGetValue("next_step", out var directNextStepId) &&
                !string.IsNullOrWhiteSpace(directNextStepId))
            {
                next = _workflow.GetStep(directNextStepId);
                if (next == null)
                {
                    ctx.Logger.LogError(
                        "workflow_loop: run={RunId} step={StepId} resolved invalid next_step={NextStepId}",
                        runId,
                        current.Id,
                        directNextStepId);
                    await CleanupRunAsync(runId, ctx, ct);
                    await ctx.PublishAsync(new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        RunId = runId,
                        Success = false,
                        Error = $"invalid next_step '{directNextStepId}' from step '{current.Id}'",
                    }, EventDirection.Both, ct);
                    return;
                }
            }
            else
            {
                var branchKey = evt.Metadata.TryGetValue("branch", out var bk) ? bk : null;
                next = _workflow.GetNextStep(current.Id, branchKey);
            }

            if (next == null)
            {
                await CleanupRunAsync(runId, ctx, ct);
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = true,
                    Output = evt.Output,
                }, EventDirection.Both, ct);
                return;
            }
            await DispatchStep(next, evt.Output ?? string.Empty, runId, ctx, ct);
        }
    }

    private async Task<bool> TryRetryAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        string runId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var policy = step.Retry;
        if (policy == null) return false;
        if (IsTimeoutError(evt.Error))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: step={StepId} timeout is not retried to avoid stale completion races",
                step.Id);
            return false;
        }

        var stepRunKey = GetStepRunKey(runId, step.Id);
        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        var attempt = _retryAttempts.GetValueOrDefault(stepRunKey, 0) + 1;
        if (attempt >= maxAttempts) return false;

        _retryAttempts[stepRunKey] = attempt;

        var delayMs = policy.Backoff.Equals("exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (attempt - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        ctx.Logger.LogWarning("workflow_loop: step={StepId} retry attempt={Attempt}/{Max} delay={Delay}ms error={Error}",
            step.Id, attempt + 1, maxAttempts, delayMs, evt.Error);

        if (!_currentStepInputByRunId.TryGetValue(runId, out var retryInput))
        {
            ctx.Logger.LogWarning(
                "workflow_loop: missing retry input run={RunId} step={StepId}, fallback to empty input",
                runId,
                step.Id);
            retryInput = string.Empty;
        }

        if (delayMs <= 0)
        {
            await DispatchStep(step, retryInput, runId, ctx, ct);
            return true;
        }

        await StartRetryBackoffAsync(
            runId,
            step.Id,
            delayMs,
            attempt + 1,
            ctx,
            ct);
        return true;
    }

    private async Task HandleRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (_workflow == null)
            return;

        var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
        var stepId = evt.StepId?.Trim();
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(stepId) ||
            !_activeRunIds.Contains(runId))
        {
            return;
        }

        var stepRunKey = GetStepRunKey(runId, stepId);
        if (!_retryBackoffs.TryGetValue(stepRunKey, out var pending))
            return;

        if (TryReadGeneration(envelope, out var firedGeneration) &&
            firedGeneration != pending.Generation)
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore stale retry backoff run={RunId} step={StepId} fired={FiredGeneration} expected={ExpectedGeneration}",
                runId,
                stepId,
                firedGeneration,
                pending.Generation);
            return;
        }

        if (!_currentStepByRunId.TryGetValue(runId, out var expectedStepId) ||
            !string.Equals(expectedStepId, stepId, StringComparison.Ordinal))
        {
            ctx.Logger.LogDebug(
                "workflow_loop: ignore retry backoff for stale step run={RunId} step={StepId} expected={ExpectedStepId}",
                runId,
                stepId,
                expectedStepId ?? "(none)");
            return;
        }

        _retryBackoffs.Remove(stepRunKey);
        var step = _workflow.GetStep(stepId);
        if (step == null)
        {
            ctx.Logger.LogWarning(
                "workflow_loop: retry backoff fired but step definition not found run={RunId} step={StepId}",
                runId,
                stepId);
            return;
        }

        if (!_currentStepInputByRunId.TryGetValue(runId, out var retryInput))
            retryInput = string.Empty;

        ctx.Logger.LogWarning(
            "workflow_loop: retry backoff fired run={RunId} step={StepId} next_attempt={Attempt} delay_ms={DelayMs}",
            runId,
            stepId,
            pending.NextAttempt,
            evt.DelayMs);
        await DispatchStep(step, retryInput, runId, ctx, ct);
    }

    private async Task StartRetryBackoffAsync(
        string runId,
        string stepId,
        int delayMs,
        int nextAttempt,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var stepRunKey = GetStepRunKey(runId, stepId);
        await CancelRetryBackoffAsync(runId, stepId, ctx, CancellationToken.None);

        var callbackId = BuildStepRetryBackoffCallbackId(runId, stepId);
        var lease = await ctx.ScheduleSelfTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(delayMs),
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                DelayMs = delayMs,
                NextAttempt = nextAttempt,
            },
            ct: ct);

        _retryBackoffs[stepRunKey] = new RetryBackoffLease(
            callbackId,
            lease.Generation,
            nextAttempt,
            delayMs);
    }

    private async Task<bool> TryOnErrorAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        string runId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var policy = step.OnError;
        if (policy == null) return false;

        switch (policy.Strategy.ToLowerInvariant())
        {
            case "skip":
            {
                var output = policy.DefaultOutput ?? evt.Output ?? "";
                ctx.Logger.LogWarning("workflow_loop: step={StepId} failed, on_error=skip output=({Len} chars)",
                    step.Id, output.Length);

                _retryAttempts.Remove(GetStepRunKey(runId, step.Id));
                var next = _workflow!.GetNextStep(step.Id);
                if (next == null)
                {
                await CleanupRunAsync(runId, ctx, ct);
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                        Success = true,
                        Output = output,
                    }, EventDirection.Both, ct);
                }
                else
                {
                    await DispatchStep(next, output, runId, ctx, ct);
                }
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = _workflow!.GetStep(policy.FallbackStep);
                if (fallback == null) return false;

                ctx.Logger.LogWarning("workflow_loop: step={StepId} failed, on_error=fallback → {Fallback}",
                    step.Id, policy.FallbackStep);

                _retryAttempts.Remove(GetStepRunKey(runId, step.Id));
                await DispatchStep(fallback, evt.Output ?? "", runId, ctx, ct);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task DispatchStep(
        StepDefinition step,
        string input,
        string runId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        _currentStepByRunId[runId] = step.Id;
        _currentStepInputByRunId[runId] = input;
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (_workflow?.Configuration.ClosedWorldMode == true &&
            WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonicalStepType))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step type '{canonicalStepType}' is blocked in closed_world_mode",
            }, EventDirection.Self, ct);
            return;
        }

        var inputPreview = input.Length > 200 ? input[..200] + "..." : input;
        ctx.Logger.LogInformation("workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars) {Preview}",
            step.Id, canonicalStepType, step.TargetRole ?? "(none)", input.Length, inputPreview);

        var req = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = canonicalStepType,
            RunId = runId,
            Input = input,
            TargetRole = step.TargetRole ?? "",
        };
        var vars = ResolveVariables(runId);
        vars["input"] = input;

        foreach (var (k, v) in step.Parameters)
        {
            if (ShouldDeferWhileParameterEvaluation(canonicalStepType, k))
            {
                req.Parameters[k] = v;
                continue;
            }

            var evaluated = _expressionEvaluator.Evaluate(v, vars);
            req.Parameters[k] = WorkflowPrimitiveCatalog.IsStepTypeParameterKey(k)
                ? WorkflowPrimitiveCatalog.ToCanonicalType(evaluated)
                : evaluated;
        }

        if (step.Branches is { Count: > 0 })
        {
            foreach (var (bk, bv) in step.Branches)
                req.Parameters[$"branch.{bk}"] = bv;
        }

        if (!string.IsNullOrWhiteSpace(step.TargetRole) && _workflow != null)
        {
            var role = _workflow.Roles.FirstOrDefault(r => string.Equals(r.Id, step.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                req.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        await StartTimeoutAsync(step, runId, ctx, ct);
        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private static bool ShouldDeferWhileParameterEvaluation(string canonicalStepType, string parameterKey) =>
        string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(parameterKey, "condition", StringComparison.OrdinalIgnoreCase) ||
         parameterKey.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase));

    private static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    private async Task StartTimeoutAsync(StepDefinition step, string runId, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (step.TimeoutMs is not > 0) return;

        var stepRunKey = GetStepRunKey(runId, step.Id);
        await CancelTimeoutAsync(runId, step.Id, ctx, ct);

        var stepId = step.Id;
        var timeoutMs = Math.Clamp(step.TimeoutMs.Value, 100, 600_000);
        var lease = await ctx.ScheduleSelfTimeoutAsync(
            BuildStepTimeoutCallbackId(runId, stepId),
            TimeSpan.FromMilliseconds(timeoutMs),
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                TimeoutMs = timeoutMs,
            },
            ct: ct);
        _timeouts[stepRunKey] = lease.Generation;
    }

    private async Task CancelTimeoutAsync(
        string runId,
        string stepId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var stepRunKey = GetStepRunKey(runId, stepId);
        if (!_timeouts.Remove(stepRunKey, out var generation))
            return;

        await ctx.CancelScheduledCallbackAsync(
            BuildStepTimeoutCallbackId(runId, stepId),
            generation,
            ct);
    }

    private async Task CancelRetryBackoffAsync(
        string runId,
        string stepId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var stepRunKey = GetStepRunKey(runId, stepId);
        if (!_retryBackoffs.Remove(stepRunKey, out var pending))
            return;

        await ctx.CancelScheduledCallbackAsync(
            pending.CallbackId,
            pending.Generation,
            ct);
    }

    private Dictionary<string, string> ResolveVariables(string runId)
    {
        if (_variablesByRunId.TryGetValue(runId, out var vars))
            return vars;

        vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _variablesByRunId[runId] = vars;
        return vars;
    }

    private static string GetStepRunKey(string runId, string stepId) => $"{runId}:{stepId}";

    private static string ResolveRunId(StepCompletedEvent evt)
    {
        return WorkflowRunIdNormalizer.Normalize(evt.RunId);
    }

    private static bool TryReadGeneration(EventEnvelope envelope, out long generation)
    {
        generation = 0;
        return envelope.Metadata.TryGetValue(RuntimeCallbackMetadataKeys.CallbackGeneration, out var raw) &&
               long.TryParse(raw, out generation);
    }

    private static string BuildStepTimeoutCallbackId(string runId, string stepId) =>
        string.Concat("workflow-step-timeout:", runId, ":", stepId);

    private static string BuildStepRetryBackoffCallbackId(string runId, string stepId) =>
        string.Concat("workflow-step-retry-backoff:", runId, ":", stepId);

    private async Task CleanupRunAsync(string runId, IEventHandlerContext ctx, CancellationToken ct)
    {
        _activeRunIds.Remove(runId);
        _currentStepByRunId.Remove(runId);
        _currentStepInputByRunId.Remove(runId);
        _variablesByRunId.Remove(runId);

        var retryPrefix = $"{runId}:";
        foreach (var key in _retryAttempts.Keys.Where(k => k.StartsWith(retryPrefix, StringComparison.Ordinal)).ToList())
            _retryAttempts.Remove(key);

        foreach (var key in _timeouts.Keys.Where(k => k.StartsWith(retryPrefix, StringComparison.Ordinal)).ToList())
        {
            var generation = _timeouts[key];
            _timeouts.Remove(key);
            var stepId = key.Length > retryPrefix.Length
                ? key[retryPrefix.Length..]
                : string.Empty;
            if (string.IsNullOrWhiteSpace(stepId))
                continue;

            await ctx.CancelScheduledCallbackAsync(
                BuildStepTimeoutCallbackId(runId, stepId),
                generation,
                ct);
        }

        foreach (var key in _retryBackoffs.Keys.Where(k => k.StartsWith(retryPrefix, StringComparison.Ordinal)).ToList())
        {
            if (!_retryBackoffs.Remove(key, out var pending))
                continue;

            await ctx.CancelScheduledCallbackAsync(
                pending.CallbackId,
                pending.Generation,
                ct);
        }

    }

    private sealed record RetryBackoffLease(
        string CallbackId,
        long Generation,
        int NextAttempt,
        int DelayMs);
}
