// ─────────────────────────────────────────────────────────────
// WorkflowLoopModule — 工作流循环驱动模块
// 响应 StartWorkflowEvent 与 StepCompletedEvent，串行调度步骤直至完成
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflows.Core.Primitives;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>
/// 工作流循环驱动模块。负责接收启动与完成事件，按工作流定义依次调度步骤。
/// </summary>
public sealed class WorkflowLoopModule : IEventModule
{
    private WorkflowDefinition? _workflow;
    private string? _currentRunId;

    /// <summary>
    /// 模块名称。
    /// </summary>
    public string Name => "workflow_loop";

    /// <summary>
    /// 处理优先级，数值越小优先级越高。
    /// </summary>
    public int Priority => 0;

    /// <summary>
    /// 设置当前要执行的工作流定义。
    /// </summary>
    /// <param name="workflow">工作流定义。</param>
    public void SetWorkflow(WorkflowDefinition workflow) => _workflow = workflow;

    /// <summary>
    /// 判断是否可处理该事件。
    /// </summary>
    /// <param name="envelope">事件信封。</param>
    /// <returns>若为 StartWorkflowEvent 或 StepCompletedEvent 则返回 true。</returns>
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor));
    }

    /// <summary>
    /// 处理事件。启动时取入口步骤并下发；完成时取后继步骤并下发，无后继则发布 WorkflowCompletedEvent。
    /// </summary>
    /// <param name="envelope">事件信封。</param>
    /// <param name="ctx">事件处理上下文。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (_workflow == null) return;
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            var evt = payload.Unpack<StartWorkflowEvent>();
            _currentRunId = evt.RunId;
            var entry = _workflow.Steps.FirstOrDefault();
            if (entry == null) { await ctx.PublishAsync(new WorkflowCompletedEvent { WorkflowName = _workflow.Name, RunId = evt.RunId, Success = false, Error = "无步骤" }, EventDirection.Both, ct); return; }
            await DispatchStep(entry, evt.Input, evt.RunId, ctx, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            if (evt.RunId != _currentRunId) return;
            var current = _workflow.GetStep(evt.StepId);

            // Ignore internal sub-step completions (e.g. analyze_item_0_sub_1 / *_vote).
            // Workflow loop should advance only on declared top-level workflow steps.
            if (current == null)
            {
                ctx.Logger.LogDebug("workflow_loop: ignore internal completion step={StepId}", evt.StepId);
                return;
            }

            var outputPreview = (evt.Output ?? "").Length > 200 ? evt.Output![..200] + "..." : evt.Output ?? "";
            ctx.Logger.LogInformation("workflow_loop: step={StepId} completed success={Success} output=({Len} chars) {Preview}",
                evt.StepId, evt.Success, (evt.Output ?? "").Length, outputPreview);

            if (!evt.Success) { await ctx.PublishAsync(new WorkflowCompletedEvent { WorkflowName = _workflow.Name, RunId = evt.RunId, Success = false, Error = evt.Error }, EventDirection.Both, ct); return; }
            var next = _workflow.GetNextStep(current.Id);
            if (next == null) { await ctx.PublishAsync(new WorkflowCompletedEvent { WorkflowName = _workflow.Name, RunId = evt.RunId, Success = true, Output = evt.Output }, EventDirection.Both, ct); return; }
            await DispatchStep(next, evt.Output ?? string.Empty, evt.RunId, ctx, ct);
        }
    }

    private async Task DispatchStep(StepDefinition step, string input, string runId, IEventHandlerContext ctx, CancellationToken ct)
    {
        var inputPreview = input.Length > 200 ? input[..200] + "..." : input;
        ctx.Logger.LogInformation("workflow_loop: dispatch step={StepId} type={Type} role={Role} input=({Len} chars) {Preview}",
            step.Id, step.Type, step.TargetRole ?? "(none)", input.Length, inputPreview);

        var req = new StepRequestEvent { StepId = step.Id, StepType = step.Type, RunId = runId, Input = input, TargetRole = step.TargetRole ?? "" };
        foreach (var (k, v) in step.Parameters) req.Parameters[k] = v;

        // 当步骤指定了 TargetRole 且该角色配置了 connectors 允许列表时，注入 allowed_connectors 供 ConnectorCallModule 校验
        if (!string.IsNullOrWhiteSpace(step.TargetRole) && _workflow != null)
        {
            var role = _workflow.Roles.FirstOrDefault(r => string.Equals(r.Id, step.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                req.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }
}
