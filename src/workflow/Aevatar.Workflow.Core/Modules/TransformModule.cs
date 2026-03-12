// ─────────────────────────────────────────────────────────────
// TransformModule — 确定性变换模块
// 对 input 执行纯函数变换（count, take, join, split 等）
// 不调用 LLM，纯确定性逻辑
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>确定性变换模块。处理 type=transform 的步骤。</summary>
public sealed class TransformModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "transform";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "transform") return;

        var op = request.Parameters.GetValueOrDefault("op", "identity").Trim().ToLowerInvariant();
        var input = request.Input ?? "";
        var separator = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n", "separator", "delimiter"),
            "\n");
        var n = WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 5, 1, 10_000, "n", "count");

        string output;
        try
        {
            output = op switch
            {
                "identity" => input,
                "count" => CountLines(input).ToString(),
                "count_words" => input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
                "take" => TakeLines(input, n),
                "take_last" => TakeLastLines(input, n),
                "join" => string.Join(separator, SplitSections(input)),
                "split" => string.Join(
                    "\n---\n",
                    WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(input, separator)),
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
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true, Output = output,
        }, BroadcastDirection.Self, ct);
    }

    private static int CountLines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    private static string TakeLines(string s, int n) => string.Join("\n", s.Split('\n').Take(n));
    private static string TakeLastLines(string s, int n) => string.Join("\n", s.Split('\n').TakeLast(n));
    private static string[] SplitSections(string s) => s.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
}
