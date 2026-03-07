// ─────────────────────────────────────────────────────────────
// ConditionalPrimitiveExecutor — 条件分支模块
// 根据上一步结果中的关键词选择不同的下一步
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>条件分支模块。处理 type=conditional 的步骤。</summary>
public sealed class ConditionalPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "conditional";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "conditional") return;

        var condition = request.Parameters.GetValueOrDefault("condition", "default");
        var input = request.Input ?? "";
        var branchKey = TryParseBoolean(condition, out var evaluated)
            ? (evaluated ? "true" : "false")
            : (input.Contains(condition, StringComparison.OrdinalIgnoreCase) ? "true" : "false");

        ctx.Logger.LogInformation("条件分支 {StepId}: 条件={Condition}, 分支={Branch}",
            request.StepId, condition, branchKey);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true, Output = input,
        };
        completed.Metadata["branch"] = branchKey;
        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;
        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            result = true;
            return true;
        }
        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }
}
