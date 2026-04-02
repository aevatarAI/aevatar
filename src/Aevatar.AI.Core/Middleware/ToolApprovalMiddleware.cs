// ─────────────────────────────────────────────────────────────
// ToolApprovalMiddleware — 工具审批中间件
// 根据 IAgentTool.ApprovalMode 决定是否需要审批
// 支持 denial tracking（连续拒绝后自动阻断）
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// 工具审批中间件。检查 tool 的 ApprovalMode 并执行对应策略。
/// 应插入到 tool call middleware 链最前面，确保安全策略不可绕过。
/// </summary>
public sealed class ToolApprovalMiddleware : IToolCallMiddleware
{
    private const int MaxConsecutiveDenials = 3;

    private readonly IToolApprovalHandler _handler;
    private readonly AgentHookPipeline? _hooks;
    private readonly Dictionary<string, int> _denialCounts = new(StringComparer.OrdinalIgnoreCase);

    public ToolApprovalMiddleware(IToolApprovalHandler handler, AgentHookPipeline? hooks = null)
    {
        _handler = handler;
        _hooks = hooks;
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        var mode = context.Tool.ApprovalMode;

        // NeverRequire → 直接执行
        if (mode == ToolApprovalMode.NeverRequire)
        {
            await next();
            return;
        }

        // Runtime argument-based check: tool can inspect call arguments to decide.
        // Returns true → requires approval, false → skip, null → fall through to static check.
        var runtimeDecision = context.Tool.RequiresApproval(context.ArgumentsJson);
        if (runtimeDecision == false)
        {
            await next();
            return;
        }

        // Auto 模式分类器 (only when runtime check returned null)
        if (runtimeDecision == null && mode == ToolApprovalMode.Auto)
        {
            if (context.Tool.IsReadOnly)
            {
                await next();
                return;
            }

            if (!context.Tool.IsDestructive)
            {
                // 既非 ReadOnly 也非 Destructive → 默认放行
                await next();
                return;
            }
            // IsDestructive → 继续走审批流程
        }

        // 检查 denial tracking — 连续拒绝过多则自动阻断
        if (_denialCounts.TryGetValue(context.ToolName, out var count) && count >= MaxConsecutiveDenials)
        {
            context.Terminate = true;
            context.Result = $"Tool '{context.ToolName}' has been denied {count} times consecutively. " +
                             "Automatic block applied. Consider using a different approach.";
            return;
        }

        // 请求审批
        var request = new ToolApprovalRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ToolName = context.ToolName,
            ToolCallId = context.ToolCallId,
            ArgumentsJson = context.ArgumentsJson,
            ApprovalMode = mode,
            IsReadOnly = context.Tool.IsReadOnly,
            IsDestructive = context.Tool.IsDestructive,
        };

        // Hook: approval requested
        var hookCtx = new AIGAgentExecutionHookContext
        {
            ToolName = context.ToolName,
            ToolCallId = context.ToolCallId,
            ToolArguments = context.ArgumentsJson,
        };
        hookCtx.Items["approval_request_id"] = request.RequestId;
        hookCtx.Items["approval_mode"] = mode.ToString();
        if (_hooks != null) await _hooks.RunToolApprovalRequestedAsync(hookCtx, context.CancellationToken);

        var result = await _handler.RequestApprovalAsync(request, context.CancellationToken);

        // Hook: approval completed
        hookCtx.Items["approval_decision"] = result.Decision.ToString();
        hookCtx.Items["approval_reason"] = result.Reason ?? "";
        if (_hooks != null) await _hooks.RunToolApprovalCompletedAsync(hookCtx, context.CancellationToken);

        switch (result.Decision)
        {
            case ToolApprovalDecision.Approved:
                // 重置该 tool 的 denial counter
                _denialCounts.Remove(context.ToolName);
                await next();
                return;

            case ToolApprovalDecision.Denied:
                _denialCounts[context.ToolName] = (_denialCounts.TryGetValue(context.ToolName, out var dc) ? dc : 0) + 1;
                context.Terminate = true;
                context.Result = !string.IsNullOrWhiteSpace(result.Reason)
                    ? $"Tool '{context.ToolName}' execution denied: {result.Reason}"
                    : $"Tool '{context.ToolName}' execution denied by approval handler.";
                return;

            case ToolApprovalDecision.Timeout:
                context.Terminate = true;
                context.Result = $"Tool '{context.ToolName}' approval timed out. " +
                                 "The tool was not executed. Please try again or approve when prompted.";
                return;

            case ToolApprovalDecision.Yield:
                // 非阻塞 yield：返回 pending result，不增加 denial counter。
                // Actor 层检测此 result 后持久化 pending state 并走事件化续传。
                context.Terminate = true;
                context.Result = BuildApprovalPendingResult(request);
                return;
        }
    }

    /// <summary>Approval pending marker key in tool result JSON.</summary>
    public const string ApprovalRequiredKey = "approval_required";

    private static string BuildApprovalPendingResult(ToolApprovalRequest request) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            approval_required = true,
            request_id = request.RequestId,
            tool_name = request.ToolName,
            tool_call_id = request.ToolCallId,
            arguments = request.ArgumentsJson,
            message = "This tool requires user approval before execution. An approval request has been sent.",
        });
}
