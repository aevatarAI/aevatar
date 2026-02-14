// ─────────────────────────────────────────────────────────────
// AGUIEventChannel — Channel 驱动的事件收集器
// 每个 chat 请求创建一个，Push 写入 Channel，ReadAllAsync 读出
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// 基于 Channel 的 AG-UI 事件收集器。线程安全，无界缓冲。
/// </summary>
public sealed class AGUIEventChannel : IAGUIEventSink
{
    private readonly Channel<AGUIEvent> _channel =
        Channel.CreateUnbounded<AGUIEvent>(new UnboundedChannelOptions { SingleReader = true });

    public void Push(AGUIEvent evt) => _channel.Writer.TryWrite(evt);

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<AGUIEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
