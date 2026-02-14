// ─────────────────────────────────────────────────────────────
// ConditionalModule — 条件分支模块
// 根据上一步结果中的关键词选择不同的下一步
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>条件分支模块。处理 type=conditional 的步骤。</summary>
public sealed class ConditionalModule : IEventModule
{
    public string Name => "conditional";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "conditional") return;

        var condition = request.Parameters.GetValueOrDefault("condition", "default");
        var input = request.Input ?? "";
        var branchKey = input.Contains(condition, StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        ctx.Logger.LogInformation("条件分支 {StepId}: 条件={Condition}, 分支={Branch}",
            request.StepId, condition, branchKey);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId,
            Success = true, Output = input,
        }, EventDirection.Self, ct);
    }
}
