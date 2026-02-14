// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule
{
    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "tool_call") return;

        var toolName = request.Parameters.GetValueOrDefault("tool", "");
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId,
                Success = false, Error = "tool_call 缺少 tool 参数",
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogInformation("ToolCall: {StepId} → 工具 {Tool}", request.StepId, toolName);

        // 通过 ToolCallEvent 委托给 Agent 的 Tool 系统
        await ctx.PublishAsync(new ToolCallEvent
        {
            ToolName = toolName,
            ArgumentsJson = request.Input,
            CallId = request.StepId,
        }, EventDirection.Self, ct);
    }
}
