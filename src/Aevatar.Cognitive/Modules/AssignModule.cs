// ─────────────────────────────────────────────────────────────
// AssignModule — 变量赋值模块
// 从点号路径或字面值赋值到变量
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>变量赋值模块。处理 type=assign 的步骤。</summary>
public sealed class AssignModule : IEventModule
{
    public string Name => "assign";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "assign") return;

        // 参数: target = 目标变量名, value = 值或路径
        var target = request.Parameters.GetValueOrDefault("target", "");
        var value = request.Parameters.GetValueOrDefault("value", "");

        // 如果 value 以 $ 开头，表示从 input（上一步输出）中取值
        var resolvedValue = value.StartsWith('$') ? request.Input : value;

        ctx.Logger.LogInformation("Assign: {Target} = {Value}", target, resolvedValue.Length > 50 ? resolvedValue[..50] + "..." : resolvedValue);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = resolvedValue,
        }, EventDirection.Self, ct);
    }
}
