// ─────────────────────────────────────────────────────────────
// TransformModule — 确定性变换模块
// 对 input 执行纯函数变换（count, take, join, split 等）
// 不调用 LLM，纯确定性逻辑
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>确定性变换模块。处理 type=transform 的步骤。</summary>
public sealed class TransformModule : IEventModule
{
    public string Name => "transform";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "transform") return;

        var op = request.Parameters.GetValueOrDefault("op", "identity");
        var input = request.Input ?? "";

        string output;
        try
        {
            output = op switch
            {
                "identity" => input,
                "count" => CountLines(input).ToString(),
                "count_words" => input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
                "take" => TakeLines(input, int.TryParse(request.Parameters.GetValueOrDefault("n", "5"), out var n) ? n : 5),
                "take_last" => TakeLastLines(input, int.TryParse(request.Parameters.GetValueOrDefault("n", "5"), out var nl) ? nl : 5),
                "join" => string.Join(request.Parameters.GetValueOrDefault("separator", "\n"), SplitSections(input)),
                "split" => string.Join("\n---\n", input.Split(request.Parameters.GetValueOrDefault("separator", "\n"), StringSplitOptions.RemoveEmptyEntries)),
                "distinct" => string.Join("\n", input.Split('\n').Distinct()),
                "uppercase" => input.ToUpperInvariant(),
                "lowercase" => input.ToLowerInvariant(),
                "trim" => input.Trim(),
                "reverse_lines" => string.Join("\n", input.Split('\n').Reverse()),
                _ => input, // 未知操作返回原文
            };
        }
        catch (Exception ex)
        {
            output = $"Transform 错误: {ex.Message}";
        }

        ctx.Logger.LogInformation("Transform {StepId}: op={Op}, input_len={InLen}, output_len={OutLen}",
            request.StepId, op, input.Length, output.Length);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId, RunId = request.RunId,
            Success = true, Output = output,
        }, EventDirection.Self, ct);
    }

    private static int CountLines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    private static string TakeLines(string s, int n) => string.Join("\n", s.Split('\n').Take(n));
    private static string TakeLastLines(string s, int n) => string.Join("\n", s.Split('\n').TakeLast(n));
    private static string[] SplitSections(string s) => s.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
}
