// ─────────────────────────────────────────────────────────────
// ToolApprovalMiddleware — 工具审批中间件
// 根据 IAgentTool.ApprovalMode 决定是否需要审批
// 支持 denial tracking（连续拒绝后自动阻断）
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// 工具审批中间件。检查 tool 的 ApprovalMode 并执行对应策略。
/// 应插入到 tool call middleware 链最前面，确保安全策略不可绕过。
/// </summary>
// Refactor (iter1/cluster-005):
//   Old pattern: instance dictionary kept denial counts across tool calls without an actor-owned fact source.
//   New principle: denial counts are explicit request-scoped inputs or actor-owned facts, never hidden service state.
public sealed class ToolApprovalMiddleware : IToolCallMiddleware
{
    private const int MaxConsecutiveDenials = 3;

    private readonly IToolApprovalHandler _handler;
    private readonly AgentHookPipeline? _hooks;

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

        // Denial tracking is caller-supplied request/session state. This middleware
        // only consumes and returns the count carried on this tool-call context.
        var denialCount = GetRequestScopedDenialCount(context);
        if (denialCount >= MaxConsecutiveDenials)
        {
            var reason = $"Tool '{context.ToolName}' has been denied {denialCount} times consecutively.";
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.ApprovalDenied;
            context.TerminationReason = reason;
            context.Result = $"{reason} Automatic block applied. Consider using a different approach.";
            return;
        }

        if (context.ApprovalGrant is { } grant)
        {
            if (grant.Approved)
            {
                context.Items[DenialCountItemKey] = 0;
                await next();
                return;
            }

            context.Items[DenialCountItemKey] = denialCount + 1;
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.ApprovalDenied;
            context.TerminationReason = "Tool approval rejected.";
            context.Result = $"Tool '{context.ToolName}' execution denied by approval decision.";
            context.Receipt = AgentToolReceiptFactory.CreateDenied(
                context.Tool,
                context.ToolCallId,
                context.ToolName,
                context.Result,
                grant.ApprovalRequestId,
                context.TerminationReason);
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
                context.Items[DenialCountItemKey] = 0;
                await next();
                return;

            case ToolApprovalDecision.Denied:
                context.Items[DenialCountItemKey] = denialCount + 1;
                context.Terminate = true;
                context.TerminationKind = ToolCallTerminationKind.ApprovalDenied;
                context.TerminationReason = result.Reason;
                context.Result = !string.IsNullOrWhiteSpace(result.Reason)
                    ? $"Tool '{context.ToolName}' execution denied: {result.Reason}"
                    : $"Tool '{context.ToolName}' execution denied by approval handler.";
                context.Receipt = AgentToolReceiptFactory.CreateDenied(
                    context.Tool,
                    context.ToolCallId,
                    context.ToolName,
                    context.Result,
                    request.RequestId,
                    result.Reason ?? "Tool approval denied.");
                return;

            case ToolApprovalDecision.Timeout:
                context.Terminate = true;
                context.TerminationKind = ToolCallTerminationKind.ApprovalTimedOut;
                context.TerminationReason = result.Reason;
                context.Result = $"Tool '{context.ToolName}' approval timed out. " +
                                 "The tool was not executed. Please try again or approve when prompted.";
                context.Receipt = AgentToolReceiptFactory.CreateApprovalError(
                    context.Tool,
                    context.ToolCallId,
                    context.ToolName,
                    context.Result,
                    request.RequestId,
                    "approval_timeout",
                    "Tool approval timed out.");
                return;

            case ToolApprovalDecision.Yield:
                // 非阻塞 yield：返回 pending result，不增加 denial counter。
                // Actor 层检测此 result 后持久化 pending state 并走事件化续传。
                context.Terminate = true;
                context.TerminationKind = ToolCallTerminationKind.ApprovalPending;
                context.TerminationReason = result.Reason;
                context.PendingApproval = new ToolApprovalPendingContext(
                    request.RequestId,
                    request.ToolName,
                    request.ToolCallId,
                    request.ArgumentsJson,
                    request.ApprovalMode,
                    request.IsReadOnly,
                    request.IsDestructive);
                context.Result = BuildApprovalPendingResult(request);
                context.Receipt = AgentToolReceiptFactory.CreateApprovalRequired(
                    context.Tool,
                    context.ToolCallId,
                    context.ToolName,
                    context.Result,
                    request.RequestId);
                return;
        }
    }

    /// <summary>Approval pending marker key in tool result JSON.</summary>
    public const string ApprovalRequiredKey = "approval_required";

    /// <summary>Request-scoped consecutive denial count carried by the caller.</summary>
    public const string DenialCountItemKey = "approval_denial_count";

    private static int GetRequestScopedDenialCount(ToolCallContext context)
    {
        if (!context.Items.TryGetValue(DenialCountItemKey, out var value))
            return 0;

        return value switch
        {
            int count when count > 0 => count,
            long count when count > 0 && count <= int.MaxValue => (int)count,
            string text when int.TryParse(text, out var count) && count > 0 => count,
            _ => 0,
        };
    }

    private static string BuildApprovalPendingResult(ToolApprovalRequest request) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            approval_required = true,
            request_id = request.RequestId,
            tool_name = request.ToolName,
            tool_call_id = request.ToolCallId,
            arguments = request.ArgumentsJson,
            message = "This tool requires user approval before execution. Execution is paused; " +
                      "the approval request will be submitted for review and the run resumes only " +
                      "after it is approved. Do not claim the tool has executed.",
        });
}
