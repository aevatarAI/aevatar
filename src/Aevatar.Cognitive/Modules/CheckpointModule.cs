// ─────────────────────────────────────────────────────────────
// CheckpointModule — 检查点模块
// 将当前变量保存到检查点（供故障恢复）
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>检查点模块。处理 type=checkpoint 的步骤。</summary>
public sealed class CheckpointModule : IEventModule
{
    public string Name => "checkpoint";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "checkpoint") return;

        // 检查点：记录当前 input（变量快照）
        var checkpointName = request.Parameters.GetValueOrDefault("name", request.StepId);

        ctx.Logger.LogInformation("Checkpoint: {Name}, 数据长度={Len}", checkpointName, request.Input?.Length ?? 0);

        // 简化实现：将 input 透传为 output
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId,
            Success = true, Output = request.Input,
        }, EventDirection.Self, ct);
    }
}
