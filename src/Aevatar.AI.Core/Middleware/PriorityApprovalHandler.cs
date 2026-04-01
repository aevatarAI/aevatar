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

        // 如果本地返回明确决策（Approved 或 Denied），直接使用
        if (localResult.Decision != ToolApprovalDecision.Timeout)
            return localResult;

        // 2. 本地超时 → fallback 到远程
        if (_remoteHandler == null)
            return localResult; // 没有远程 handler，返回超时

        return await _remoteHandler.RequestApprovalAsync(request, ct);
    }
}
