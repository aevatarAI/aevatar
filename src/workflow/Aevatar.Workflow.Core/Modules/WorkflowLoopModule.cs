using Aevatar.Foundation.Abstractions;
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
    private readonly Dictionary<string, Dictionary<string, string>> _variablesByRunId = new(StringComparer.Ordinal);
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _timeouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stepToRunId = new(StringComparer.Ordinal);

    public string Name => "workflow_loop";
    public int Priority => 0;

    public void SetWorkflow(WorkflowDefinition workflow) => _workflow = workflow;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (_workflow == null) return;
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            var evt = payload.Unpack<StartWorkflowEvent>();
            var runId = string.IsNullOrWhiteSpace(evt.RunId) ? Guid.NewGuid().ToString("N") : evt.RunId;

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
                CleanupRun(runId);
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
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
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

            CancelTimeout(runId, evt.StepId);
            _stepToRunId.Remove(evt.StepId);

            var outputPreview = (evt.Output ?? "").Length > 200 ? evt.Output![..200] + "..." : evt.Output ?? "";
            ctx.Logger.LogInformation("workflow_loop: step={StepId} completed success={Success} output=({Len} chars) {Preview}",
                evt.StepId, evt.Success, (evt.Output ?? "").Length, outputPreview);

            if (_variablesByRunId.TryGetValue(runId, out var varsForRun))
            {
                if (!string.IsNullOrWhiteSpace(evt.StepId))
                    varsForRun[evt.StepId] = evt.Output ?? string.Empty;
                varsForRun["input"] = evt.Output ?? string.Empty;
            }

            if (!evt.Success)
            {
                if (await TryRetryAsync(current, evt, runId, ctx, ct)) return;
                if (await TryOnErrorAsync(current, evt, runId, ctx, ct)) return;

                CleanupRun(runId);
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    RunId = runId,
                    Success = false,
                    Error = evt.Error,
                }, EventDirection.Both, ct);
                return;
            }

            _retryAttempts.Remove(GetStepRunKey(runId, evt.StepId));

            var branchKey = evt.Metadata.TryGetValue("branch", out var bk) ? bk : null;
            var next = _workflow.GetNextStep(current.Id, branchKey);
            if (next == null)
            {
                CleanupRun(runId);
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

        if (delayMs > 0)
            await Task.Delay(delayMs, ct);

        await DispatchStep(step, evt.Output ?? "", runId, ctx, ct);
        return true;
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
                    CleanupRun(runId);
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
        var inputPreview = input.Length > 200 ? input[..200] + "..." : input;
        ctx.Logger.LogInformation("workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars) {Preview}",
            step.Id, step.Type, step.TargetRole ?? "(none)", input.Length, inputPreview);

        var req = new StepRequestEvent { StepId = step.Id, StepType = step.Type, RunId = runId, Input = input, TargetRole = step.TargetRole ?? "" };
        var vars = ResolveVariables(runId);
        vars["input"] = input;

        foreach (var (k, v) in step.Parameters)
            req.Parameters[k] = _expressionEvaluator.Evaluate(v, vars);

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

        _stepToRunId[step.Id] = runId;
        StartTimeout(step, runId, ctx, ct);
        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private void StartTimeout(StepDefinition step, string runId, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (step.TimeoutMs is not > 0) return;

        CancelTimeout(runId, step.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stepRunKey = GetStepRunKey(runId, step.Id);
        _timeouts[stepRunKey] = cts;

        var stepId = step.Id;
        var timeoutMs = Math.Clamp(step.TimeoutMs.Value, 100, 600_000);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeoutMs, cts.Token);
                ctx.Logger.LogWarning("workflow_loop: step={StepId} timed out after {Ms}ms", stepId, timeoutMs);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = false,
                    Error = $"TIMEOUT after {timeoutMs}ms",
                }, EventDirection.Self, CancellationToken.None);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private void CancelTimeout(string runId, string stepId)
    {
        if (!_timeouts.Remove(GetStepRunKey(runId, stepId), out var cts)) return;
        cts.Cancel();
        cts.Dispose();
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

    private string ResolveRunId(StepCompletedEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.RunId))
            return evt.RunId;

        if (!string.IsNullOrWhiteSpace(evt.StepId) && _stepToRunId.TryGetValue(evt.StepId, out var runId))
            return runId;

        return _activeRunIds.Count == 1 ? _activeRunIds.First() : string.Empty;
    }

    private void CleanupRun(string runId)
    {
        _activeRunIds.Remove(runId);
        _variablesByRunId.Remove(runId);

        var retryPrefix = $"{runId}:";
        foreach (var key in _retryAttempts.Keys.Where(k => k.StartsWith(retryPrefix, StringComparison.Ordinal)).ToList())
            _retryAttempts.Remove(key);

        foreach (var key in _timeouts.Keys.Where(k => k.StartsWith(retryPrefix, StringComparison.Ordinal)).ToList())
        {
            var cts = _timeouts[key];
            _timeouts.Remove(key);
            cts.Cancel();
            cts.Dispose();
        }

        foreach (var stepId in _stepToRunId.Where(x => string.Equals(x.Value, runId, StringComparison.Ordinal)).Select(x => x.Key).ToList())
            _stepToRunId.Remove(stepId);
    }
}
