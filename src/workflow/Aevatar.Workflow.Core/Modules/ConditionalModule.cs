// ─────────────────────────────────────────────────────────────
// ConditionalModule — 条件分支模块
// 根据上一步结果中的关键词选择不同的下一步
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>条件分支模块。处理 type=conditional 的步骤。</summary>
public sealed class ConditionalModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "conditional";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
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
