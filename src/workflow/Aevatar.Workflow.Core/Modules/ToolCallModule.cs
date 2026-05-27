// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "tool_call") return;

        var toolName = request.Parameters.GetValueOrDefault("tool", "").Trim();
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false, Error = "tool_call 缺少 tool 参数",
            }, TopologyAudience.Self, ct);
            return;
        }

        var argumentsJson = string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
            ctx.Logger,
            "ToolCall: {StepId} → 工具 {Tool}",
            request.StepId,
            toolName);

        // 发布 Tool 调用开始事件（供观测/UI）
        await ctx.PublishAsync(new WorkflowToolCallEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = request.StepId,
        }, TopologyAudience.Self, ct);

        await PublishToolFailureAsync(ctx, request, toolName, "workflow core has no tool execution adapter registered", ct);
    }

    private static async Task PublishToolFailureAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        CancellationToken ct)
    {
        var errorMessage = $"tool '{toolName}' execution failed: {error}";

        await ctx.PublishAsync(new WorkflowToolResultEvent
        {
            CallId = request.StepId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = errorMessage,
        }, TopologyAudience.Self, ct);
    }
}
