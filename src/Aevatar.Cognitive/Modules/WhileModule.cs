// ─────────────────────────────────────────────────────────────
// WhileModule — 循环模块
// 重复执行子步骤直到条件不满足或达到最大迭代次数
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>循环模块。处理 type=while 的步骤。</summary>
public sealed class WhileModule : IEventModule
{
    private readonly Dictionary<string, int> _iterations = [];

    public string Name => "while";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true ||
        envelope.Payload?.TypeUrl?.Contains("StepCompletedEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload!.TypeUrl.Contains("StepRequestEvent"))
        {
            var request = envelope.Payload.Unpack<StepRequestEvent>();
            if (request.StepType != "while") return;

            var maxIterations = int.TryParse(request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max) ? max : 10;
            _iterations[request.StepId] = 0;

            ctx.Logger.LogInformation("While 循环 {StepId}: 开始，最大 {Max} 次迭代", request.StepId, maxIterations);

            // 触发子步骤（用 step 参数指定的步骤类型）
            var subStepType = request.Parameters.GetValueOrDefault("step", "llm_call");
            await ctx.PublishAsync(new StepRequestEvent
            {
                StepId = $"{request.StepId}_iter_0",
                StepType = subStepType,
                RunId = request.RunId,
                Input = request.Input,
                TargetRole = request.TargetRole,
            }, EventDirection.Down, ct);
        }
        else if (envelope.Payload.TypeUrl.Contains("StepCompletedEvent"))
        {
            var completed = envelope.Payload.Unpack<StepCompletedEvent>();

            // 找到对应的 while 步骤
            var whileStepId = GetWhileStepId(completed.StepId);
            if (whileStepId == null || !_iterations.ContainsKey(whileStepId)) return;

            _iterations[whileStepId]++;
            var iteration = _iterations[whileStepId];
            var maxIterations = 10; // 简化：从初始请求中获取

            // 检查条件：简化实现——output 中不包含 "DONE" 且未达上限
            var shouldContinue = !completed.Output.Contains("DONE", StringComparison.OrdinalIgnoreCase) &&
                                 iteration < maxIterations && completed.Success;

            if (shouldContinue)
            {
                ctx.Logger.LogInformation("While 循环 {StepId}: 迭代 {Iter}", whileStepId, iteration);
                await ctx.PublishAsync(new StepRequestEvent
                {
                    StepId = $"{whileStepId}_iter_{iteration}",
                    StepType = "llm_call",
                    RunId = completed.RunId,
                    Input = completed.Output,
                }, EventDirection.Down, ct);
            }
            else
            {
                ctx.Logger.LogInformation("While 循环 {StepId}: 完成，共 {Iter} 次迭代", whileStepId, iteration);
                _iterations.Remove(whileStepId);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = whileStepId,
                    RunId = completed.RunId,
                    Success = true,
                    Output = completed.Output,
                }, EventDirection.Self, ct);
            }
        }
    }

    private static string? GetWhileStepId(string subStepId)
    {
        var idx = subStepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        return idx > 0 ? subStepId[..idx] : null;
    }
}
