// ─────────────────────────────────────────────────────────────
// LocalApprovalHandler — 本地聊天窗口审批
// 通过 AG-UI 事件发送审批请求，等待 ToolApprovalDecisionEvent 回传
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// 本地聊天窗口审批处理器。
/// 通过 publish 回调发送 ToolApprovalRequestEvent（AG-UI），
/// 然后通过 TaskCompletionSource 等待 ToolApprovalDecisionEvent。
/// </summary>
public sealed class LocalApprovalHandler : IToolApprovalHandler
{
    /// <summary>默认本地审批超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 15;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalResult>> _pending = new();
    private Func<ToolApprovalRequestEvent, Task>? _publishCallback;
    private int _timeoutSeconds = DefaultTimeoutSeconds;

    /// <summary>设置发布审批请求事件的回调。由 Agent 层在 HandleChatRequest 时配置。</summary>
    public void SetPublishCallback(Func<ToolApprovalRequestEvent, Task> callback) =>
        _publishCallback = callback;

    /// <summary>设置超时时间（秒）。</summary>
    public void SetTimeout(int seconds) =>
        _timeoutSeconds = seconds > 0 ? seconds : DefaultTimeoutSeconds;

    /// <summary>
    /// 提交审批决策。由 Agent 的 EventHandler 在收到 ToolApprovalDecisionEvent 时调用。
    /// </summary>
    public void SubmitDecision(string requestId, bool approved, string? reason = null)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(approved
                ? ToolApprovalResult.Approved(reason)
                : ToolApprovalResult.Denied(reason));
        }
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        if (_publishCallback == null)
            return ToolApprovalResult.Denied("No local approval channel configured.");

        var tcs = new TaskCompletionSource<ToolApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = tcs;

        try
        {
            var evt = new ToolApprovalRequestEvent
            {
                RequestId = request.RequestId,
                ToolName = request.ToolName,
                ToolCallId = request.ToolCallId,
                ArgumentsJson = request.ArgumentsJson,
                ApprovalMode = request.ApprovalMode.ToString().ToLowerInvariant(),
                IsDestructive = request.IsDestructive,
                TimeoutSeconds = _timeoutSeconds,
            };

            await _publishCallback(evt);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolApprovalResult.TimedOut("Local approval timed out.");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }
}
