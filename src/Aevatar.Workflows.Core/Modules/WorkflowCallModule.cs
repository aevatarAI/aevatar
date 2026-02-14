// ─────────────────────────────────────────────────────────────
// WorkflowCallModule — 子工作流调用模块
// 递归调用另一个 workflow（通过 StartWorkflowEvent）
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>子工作流调用模块。处理 type=workflow_call 的步骤。</summary>
public sealed class WorkflowCallModule : IEventModule
{
    public string Name => "workflow_call";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "workflow_call") return;

        var workflowName = request.Parameters.GetValueOrDefault("workflow", "");
        if (string.IsNullOrEmpty(workflowName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId,
                Success = false, Error = "workflow_call 缺少 workflow 参数",
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogInformation("WorkflowCall: {StepId} → 调用子工作流 {Workflow}", request.StepId, workflowName);

        // 触发子工作流
        await ctx.PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = workflowName,
            RunId = $"{request.RunId}_{request.StepId}",
            Input = request.Input,
        }, EventDirection.Self, ct);
    }
}
