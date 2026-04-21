using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Lark;

internal sealed class LarkStreamingHandle : StreamingHandle
{
    private readonly LarkChannelAdapter _adapter;
    private readonly ConversationReference _conversation;
    private readonly string _activityId;
    private readonly MessageContent _template;
    private readonly SortedDictionary<long, string> _deltasBySequence = [];
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
        if (_completed || _deltasBySequence.ContainsKey(chunk.SequenceNumber))
            return;

        _deltasBySequence[chunk.SequenceNumber] = chunk.Delta ?? string.Empty;
        await _adapter.UpdateAsync(_conversation, _activityId, BuildMessage(string.Concat(_deltasBySequence.Values)), CancellationToken.None);
    }

    public override async Task CompleteAsync(MessageContent final)
    {
        ArgumentNullException.ThrowIfNull(final);
        if (_completed)
            return;

        _completed = true;
        await _adapter.UpdateAsync(_conversation, _activityId, final, CancellationToken.None);
    }

    public override async ValueTask DisposeAsync()
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

        await _adapter.UpdateAsync(_conversation, _activityId, BuildMessage(interruptedText), CancellationToken.None);
    }

    private MessageContent BuildMessage(string text)
    {
        var content = _template.Clone();
        content.Text = text;
        return content;
    }
}
