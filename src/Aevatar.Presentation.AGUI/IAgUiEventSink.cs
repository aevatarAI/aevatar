// ─────────────────────────────────────────────────────────────
// IAgUiEventSink — per-request 事件收集器
// 生产端 Push，消费端 ReadAllAsync（SSE 写出循环）
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// AG-UI 事件收集器。每个 chat 请求创建一个实例。
/// </summary>
public interface IAgUiEventSink : IAsyncDisposable
{
    /// <summary>推送一个事件（线程安全，不阻塞）。</summary>
    void Push(AgUiEvent evt);

    /// <summary>完成事件流（不再有新事件）。</summary>
    void Complete();

    /// <summary>消费端：异步读取所有事件直到 Complete 或取消。</summary>
    IAsyncEnumerable<AgUiEvent> ReadAllAsync(CancellationToken ct = default);
}
