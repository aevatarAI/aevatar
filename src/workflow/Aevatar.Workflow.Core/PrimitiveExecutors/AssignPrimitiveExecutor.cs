// ─────────────────────────────────────────────────────────────
// AssignPrimitiveExecutor — 变量赋值模块
// 从点号路径或字面值赋值到变量
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>变量赋值模块。处理 type=assign 的步骤。</summary>
public sealed class AssignPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "assign";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "assign") return;

        // 参数: target = 目标变量名, value = 值或路径
        var target = request.Parameters.GetValueOrDefault("target", "");
        var value = request.Parameters.GetValueOrDefault("value", "");

        // 如果 value 以 $ 开头，表示从 input（上一步输出）中取值
        var resolvedValue = value.StartsWith('$') ? request.Input ?? string.Empty : value;

        ctx.Logger.LogInformation("Assign: {Target} = {Value}", target, resolvedValue.Length > 50 ? resolvedValue[..50] + "..." : resolvedValue);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = resolvedValue,
        };
        if (!string.IsNullOrWhiteSpace(target))
        {
            completed.Metadata["assign.target"] = target;
            completed.Metadata["assign.value"] = resolvedValue;
        }

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }
}
