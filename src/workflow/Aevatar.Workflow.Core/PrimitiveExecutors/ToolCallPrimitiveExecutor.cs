// ─────────────────────────────────────────────────────────────
// ToolCallPrimitiveExecutor — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "tool_call";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "tool_call") return;

        var toolName = request.Parameters.GetValueOrDefault("tool", "").Trim();
        if (string.IsNullOrEmpty(toolName))
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false, Error = "tool_call 缺少 tool 参数",
            }, EventDirection.Self, ct);
            return;
        }

        var argumentsJson = string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
        ctx.Logger.LogInformation("ToolCall: {StepId} → 工具 {Tool}", request.StepId, toolName);

        // 发布 Tool 调用开始事件（供观测/UI）
        await ctx.PublishAsync(new ToolCallEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = request.StepId,
        }, EventDirection.Self, ct);

        var tool = await ResolveToolAsync(toolName, ctx, ct);
        if (tool == null)
        {
            const string notFound = "tool not found or no tool sources configured";
            await PublishToolFailureAsync(ctx, request, toolName, notFound, ct);
            return;
        }

        try
        {
            var result = await tool.ExecuteAsync(argumentsJson, ct);

            await ctx.PublishAsync(new ToolResultEvent
            {
                CallId = request.StepId,
                Success = true,
                ResultJson = result,
            }, EventDirection.Self, ct);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = result,
            }, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishToolFailureAsync(ctx, request, toolName, ex.Message, ct);
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
        }
    }

    private static async Task<IAgentTool?> ResolveToolAsync(
        string toolName,
        WorkflowPrimitiveExecutionContext ctx,
        CancellationToken ct)
    {
        var sources = ctx.Services.GetServices<IAgentToolSource>().ToList();
        foreach (var source in sources)
        try
        {
            var tools = await source.DiscoverToolsAsync(ct);
            var matched = tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return matched;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
        }

        return null;
    }

    private static async Task PublishToolFailureAsync(
        WorkflowPrimitiveExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        CancellationToken ct)
    {
        var errorMessage = $"tool '{toolName}' execution failed: {error}";

        await ctx.PublishAsync(new ToolResultEvent
        {
            CallId = request.StepId,
            Success = false,
            Error = errorMessage,
        }, EventDirection.Self, ct);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = errorMessage,
        }, EventDirection.Self, ct);
    }
}
