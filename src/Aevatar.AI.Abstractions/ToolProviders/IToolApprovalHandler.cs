// ─────────────────────────────────────────────────────────────
// IToolApprovalHandler — 工具审批处理器抽象
// 支持不同审批后端（本地聊天窗口、NyxID 远程推送等）
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>工具审批处理器。不同审批渠道（本地 UI、NyxID 远程）实现此接口。</summary>
public interface IToolApprovalHandler
{
    /// <summary>请求审批并等待结果。</summary>
    Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct);
}

/// <summary>工具审批请求。</summary>
public sealed class ToolApprovalRequest
{
    /// <summary>审批请求唯一标识。</summary>
    public required string RequestId { get; init; }

    /// <summary>工具名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>LLM 生成的 tool call ID。</summary>
    public required string ToolCallId { get; init; }

    /// <summary>工具参数 JSON。</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>工具声明的审批模式。</summary>
    public required ToolApprovalMode ApprovalMode { get; init; }

    /// <summary>工具是否只读。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>工具是否有破坏性。</summary>
    public bool IsDestructive { get; init; }

    /// <summary>请求发起用户标识（可选，来自 NyxID token）。</summary>
    public string? UserId { get; init; }
}

/// <summary>工具审批结果。</summary>
public sealed class ToolApprovalResult
{
    /// <summary>审批决策。</summary>
    public required ToolApprovalDecision Decision { get; init; }

    /// <summary>审批原因或说明（可选）。</summary>
    public string? Reason { get; init; }

    public static ToolApprovalResult Approved(string? reason = null) =>
        new() { Decision = ToolApprovalDecision.Approved, Reason = reason };

    public static ToolApprovalResult Denied(string? reason = null) =>
        new() { Decision = ToolApprovalDecision.Denied, Reason = reason };

    public static ToolApprovalResult TimedOut(string? reason = null) =>
        new() { Decision = ToolApprovalDecision.Timeout, Reason = reason ?? "Approval request timed out" };
}

/// <summary>工具审批决策。</summary>
public enum ToolApprovalDecision
{
    /// <summary>已批准，允许执行。</summary>
    Approved = 0,

    /// <summary>已拒绝，禁止执行。</summary>
    Denied = 1,

    /// <summary>审批超时。</summary>
    Timeout = 2,
}
