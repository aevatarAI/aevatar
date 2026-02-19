using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
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
    private bool _executionActive;

    private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _timeouts = new(StringComparer.Ordinal);

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
            if (_executionActive)
            {
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    Success = false,
                    Error = "workflow is already running",
                }, EventDirection.Both, ct);
                return;
            }

            _executionActive = true;
            var entry = _workflow.Steps.FirstOrDefault();
            if (entry == null)
            {
                _executionActive = false;
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    Success = false,
                    Error = "无步骤",
                }, EventDirection.Both, ct);
                return;
            }
            await DispatchStep(entry, evt.Input, ctx, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            if (!_executionActive) return;
            var current = _workflow.GetStep(evt.StepId);

            if (current == null)
            {
                ctx.Logger.LogDebug("workflow_loop: ignore internal completion step={StepId}", evt.StepId);
                return;
            }

            CancelTimeout(evt.StepId);

            var outputPreview = (evt.Output ?? "").Length > 200 ? evt.Output![..200] + "..." : evt.Output ?? "";
            ctx.Logger.LogInformation("workflow_loop: step={StepId} completed success={Success} output=({Len} chars) {Preview}",
                evt.StepId, evt.Success, (evt.Output ?? "").Length, outputPreview);

            if (!evt.Success)
            {
                if (await TryRetryAsync(current, evt, ctx, ct)) return;
                if (await TryOnErrorAsync(current, evt, ctx, ct)) return;

                _executionActive = false;
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    Success = false,
                    Error = evt.Error,
                }, EventDirection.Both, ct);
                return;
            }

            _retryAttempts.Remove(evt.StepId);

            var branchKey = evt.Metadata.TryGetValue("branch", out var bk) ? bk : null;
            var next = _workflow.GetNextStep(current.Id, branchKey);
            if (next == null)
            {
                _executionActive = false;
                await ctx.PublishAsync(new WorkflowCompletedEvent
                {
                    WorkflowName = _workflow.Name,
                    Success = true,
                    Output = evt.Output,
                }, EventDirection.Both, ct);
                return;
            }
            await DispatchStep(next, evt.Output ?? string.Empty, ctx, ct);
        }
    }

    private async Task<bool> TryRetryAsync(StepDefinition step, StepCompletedEvent evt, IEventHandlerContext ctx, CancellationToken ct)
    {
        var policy = step.Retry;
        if (policy == null) return false;

        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        var attempt = _retryAttempts.GetValueOrDefault(step.Id, 0) + 1;
        if (attempt >= maxAttempts) return false;

        _retryAttempts[step.Id] = attempt;

        var delayMs = policy.Backoff.Equals("exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (attempt - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        ctx.Logger.LogWarning("workflow_loop: step={StepId} retry attempt={Attempt}/{Max} delay={Delay}ms error={Error}",
            step.Id, attempt + 1, maxAttempts, delayMs, evt.Error);

        if (delayMs > 0)
            await Task.Delay(delayMs, ct);

        await DispatchStep(step, evt.Output ?? "", ctx, ct);
        return true;
    }

    private async Task<bool> TryOnErrorAsync(StepDefinition step, StepCompletedEvent evt, IEventHandlerContext ctx, CancellationToken ct)
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

                _retryAttempts.Remove(step.Id);
                var next = _workflow!.GetNextStep(step.Id);
                if (next == null)
                {
                    _executionActive = false;
                    await ctx.PublishAsync(new WorkflowCompletedEvent
                    {
                        WorkflowName = _workflow.Name,
                        Success = true,
                        Output = output,
                    }, EventDirection.Both, ct);
                }
                else
                {
                    await DispatchStep(next, output, ctx, ct);
                }
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = _workflow!.GetStep(policy.FallbackStep);
                if (fallback == null) return false;

                ctx.Logger.LogWarning("workflow_loop: step={StepId} failed, on_error=fallback → {Fallback}",
                    step.Id, policy.FallbackStep);

                _retryAttempts.Remove(step.Id);
                await DispatchStep(fallback, evt.Output ?? "", ctx, ct);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task DispatchStep(StepDefinition step, string input, IEventHandlerContext ctx, CancellationToken ct)
    {
        var inputPreview = input.Length > 200 ? input[..200] + "..." : input;
        ctx.Logger.LogInformation("workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars) {Preview}",
            step.Id, step.Type, step.TargetRole ?? "(none)", input.Length, inputPreview);

        var req = new StepRequestEvent { StepId = step.Id, StepType = step.Type, Input = input, TargetRole = step.TargetRole ?? "" };
        foreach (var (k, v) in step.Parameters) req.Parameters[k] = v;

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

        StartTimeout(step, ctx, ct);
        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private void StartTimeout(StepDefinition step, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (step.TimeoutMs is not > 0) return;

        CancelTimeout(step.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _timeouts[step.Id] = cts;

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
                    Success = false,
                    Error = $"TIMEOUT after {timeoutMs}ms",
                }, EventDirection.Self, CancellationToken.None);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private void CancelTimeout(string stepId)
    {
        if (!_timeouts.Remove(stepId, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
    }
}
