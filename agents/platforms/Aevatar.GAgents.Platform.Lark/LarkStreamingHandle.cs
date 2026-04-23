using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

internal sealed class LarkStreamingHandle : StreamingHandle
{
    private readonly LarkChannelAdapter _adapter;
    private readonly ConversationReference _conversation;
    private readonly string _activityId;
    private readonly MessageContent _template;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedDictionary<long, string> _deltasBySequence = [];
    private readonly System.Text.StringBuilder _accumulated = new();
    private long _nextExpectedSequence = 1;
    private bool _completed;

    public LarkStreamingHandle(
        LarkChannelAdapter adapter,
        ConversationReference conversation,
        string activityId,
        MessageContent template)
    {
        _adapter = adapter;
        _conversation = conversation;
        _activityId = activityId;
        _template = template;
    }

    public override async Task AppendAsync(StreamChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_completed || _deltasBySequence.ContainsKey(chunk.SequenceNumber))
                return;

            var delta = chunk.Delta ?? string.Empty;
            _deltasBySequence[chunk.SequenceNumber] = delta;

            if (chunk.SequenceNumber == _nextExpectedSequence)
            {
                _accumulated.Append(delta);
                _nextExpectedSequence++;

                while (_deltasBySequence.TryGetValue(_nextExpectedSequence, out var buffered))
                {
                    _accumulated.Append(buffered);
                    _nextExpectedSequence++;
                }

                await _adapter.UpdateAsync(
                    _conversation,
                    _activityId,
                    BuildMessage(_accumulated.ToString()),
                    CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
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
            var interruptedText = string.Concat(_deltasBySequence.Values);
            if (string.IsNullOrWhiteSpace(interruptedText))
                interruptedText = _template.Text;
            interruptedText = string.IsNullOrWhiteSpace(interruptedText)
                ? "(reply interrupted)"
                : $"{interruptedText} (reply interrupted)";

            await _adapter.UpdateAsync(
                _conversation,
                _activityId,
                BuildMessage(interruptedText),
                CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private MessageContent BuildMessage(string text)
    {
        var content = _template.Clone();
        content.Text = text;
        return content;
    }
}
