// ─────────────────────────────────────────────────────────────
// PriorityApprovalHandler — 优先级审批编排
// 本地聊天窗口优先 → NyxID 远程 fallback
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// 优先级审批编排器。先尝试本地审批，超时后 fallback 到远程审批。
/// </summary>
public sealed class PriorityApprovalHandler : IToolApprovalHandler
{
    private readonly IToolApprovalHandler? _remoteHandler;

    /// <summary>本地审批处理器。</summary>
    public IToolApprovalHandler LocalHandler { get; }

    public PriorityApprovalHandler(
        IToolApprovalHandler localHandler,
        IToolApprovalHandler? remoteHandler = null)
    {
        LocalHandler = localHandler;
        _remoteHandler = remoteHandler;
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        // 1. 先尝试本地审批
        var localResult = await LocalHandler.RequestApprovalAsync(request, ct);

        // 本地明确批准 → 直接使用
        if (localResult.Decision == ToolApprovalDecision.Approved)
            return localResult;

        // 2. 本地未批准（超时、拒绝、或技术性失败如 "No local approval channel"）→ fallback 到远程
        // 注意：在 actor 单线程模型下，本地审批因可重入性限制会始终超时（grain 无法在
        // HandleChatRequest 运行期间处理 ToolApprovalDecisionEvent），因此 fallback 到远程是必须的。
        if (_remoteHandler == null)
            return localResult;

        return await _remoteHandler.RequestApprovalAsync(request, ct);
    }
}
