// ─────────────────────────────────────────────────────────────
// CheckpointPrimitiveExecutor — 检查点模块
// 将当前变量保存到检查点（供故障恢复）
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>检查点模块。处理 type=checkpoint 的步骤。</summary>
public sealed class CheckpointPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "checkpoint";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "checkpoint") return;

        // 检查点：记录当前 input（变量快照）
        var checkpointName = request.Parameters.GetValueOrDefault("name", request.StepId);

        ctx.Logger.LogInformation("Checkpoint: {Name}, 数据长度={Len}", checkpointName, request.Input?.Length ?? 0);

        // 简化实现：将 input 透传为 output
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true, Output = request.Input,
        }, EventDirection.Self, ct);
    }
}
