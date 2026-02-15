// ─────────────────────────────────────────────────────────────
// AGUIEventChannel — Channel 驱动的事件收集器
// 每个 chat 请求创建一个，Push 写入 Channel，ReadAllAsync 读出
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// 基于 Channel 的 AG-UI 事件收集器。线程安全，有界缓冲。
/// </summary>
public sealed class AGUIEventChannel : IAGUIEventSink
{
    private readonly Channel<AGUIEvent> _channel;

    public AGUIEventChannel()
        : this(new AGUIEventChannelOptions())
    {
    }

    public AGUIEventChannel(AGUIEventChannelOptions options)
    {
        var capacity = options.Capacity > 0 ? options.Capacity : 1024;
        _channel = Channel.CreateBounded<AGUIEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = options.FullMode,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Push(AGUIEvent evt)
    {
        if (_channel.Writer.TryWrite(evt))
            return;

        throw new InvalidOperationException("AGUI event channel is full or completed.");
    }

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
