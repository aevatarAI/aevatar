using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram;

internal sealed class TelegramStreamingHandle : StreamingHandle
{
    private readonly TelegramChannelAdapter _adapter;
    private readonly ConversationReference _conversation;
    private readonly string _activityId;
    private readonly MessageContent _template;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<long> _acceptedSequenceNumbers = [];
    private readonly SortedDictionary<long, string> _deltasBySequence = [];
    private long _flushGeneration;
    private bool _completed;
    private string _currentText;
    private CancellationTokenSource? _flushCts;

    public TelegramStreamingHandle(
        TelegramChannelAdapter adapter,
        ConversationReference conversation,
        string activityId,
        MessageContent template,
        TimeProvider timeProvider,
        TimeSpan debounce)
    {
        _adapter = adapter;
        _conversation = conversation;
        _activityId = activityId;
        _template = template;
        _timeProvider = timeProvider;
        _debounce = debounce;
        _currentText = template.Text ?? string.Empty;
    }

    public override async Task AppendAsync(StreamChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        CancellationTokenSource? previousFlush = null;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_completed || !_acceptedSequenceNumbers.Add(chunk.SequenceNumber))
                return;

            _deltasBySequence[chunk.SequenceNumber] = chunk.Delta ?? string.Empty;
            previousFlush = _flushCts;
            _flushCts = new CancellationTokenSource();
            _ = FlushLaterAsync(++_flushGeneration, _flushCts.Token);
        }
        finally
        {
            _gate.Release();
        }

        CancelPendingFlush(previousFlush);
    }

    public override async Task CompleteAsync(MessageContent final)
    {
        ArgumentNullException.ThrowIfNull(final);

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_completed)
                return;

            _completed = true;
            CancelPendingFlush(_flushCts);
            _flushCts = null;
            await _adapter.UpdateAsync(_conversation, _activityId, final, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_completed)
                return;

            _completed = true;
            CancelPendingFlush(_flushCts);
            _flushCts = null;
            var interruptedText = string.IsNullOrWhiteSpace(_currentText)
                ? "(reply interrupted)"
                : $"{_currentText} (reply interrupted)";

            try
            {
                await _adapter.UpdateAsync(
                    _conversation,
                    _activityId,
                    BuildStreamingMessage(interruptedText),
                    CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushLaterAsync(long generation, CancellationToken ct)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
                await Task.Delay(_debounce, _timeProvider, ct);

            await _gate.WaitAsync(ct);
            try
            {
                if (_completed || ct.IsCancellationRequested || generation != _flushGeneration || _deltasBySequence.Count == 0)
                    return;

                _currentText += string.Concat(_deltasBySequence.OrderBy(static pair => pair.Key).Select(static pair => pair.Value));
                _deltasBySequence.Clear();
                await _adapter.UpdateAsync(
                    _conversation,
                    _activityId,
                    BuildStreamingMessage(_currentText),
                    ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private MessageContent BuildStreamingMessage(string text)
    {
        var content = _template.Clone();
        content.Text = text;
        content.Attachments.Clear();
        return content;
    }

    private static void CancelPendingFlush(CancellationTokenSource? flushCts)
    {
        if (flushCts is null)
            return;

        flushCts.Cancel();
        flushCts.Dispose();
    }
}
