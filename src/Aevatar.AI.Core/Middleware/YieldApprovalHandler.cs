// ─────────────────────────────────────────────────────────────
// YieldApprovalHandler — 非阻塞审批处理器
// 立即返回 Yield，由 actor 事件化续传处理审批流程。
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// Non-blocking approval handler. Returns <see cref="ToolApprovalDecision.Yield"/>
/// immediately so the middleware produces a "pending" tool result instead of blocking.
/// The actor then persists the pending state, publishes an approval request event,
/// and schedules a durable timeout for remote escalation.
/// </summary>
public sealed class YieldApprovalHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        ToolApprovalRequest request, CancellationToken ct)
    {
        return Task.FromResult(ToolApprovalResult.Yielded(request.RequestId));
    }
}
