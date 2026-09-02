using System.Text;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;

namespace Aevatar.GAgentService.Application.ServiceRuns;

internal sealed class ServiceRunTerminalAguiObservation
{
    private readonly Dictionary<string, StringBuilder> _messageBuffers = new(StringComparer.Ordinal);
    private string _currentMessageId = string.Empty;
    private string _lastCompletedOutput = string.Empty;
    private string _lastObservedOutput = string.Empty;

    public bool HasTerminalObservation { get; private set; }

    public ServiceRunStatus Status { get; private set; } = ServiceRunStatus.Unspecified;

    public string LastOutput { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public void Observe(AGUIEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageStart:
                ObserveTextStart(evt.TextMessageStart);
                break;
            case AGUIEvent.EventOneofCase.TextMessageContent:
                ObserveTextContent(evt.TextMessageContent);
                break;
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                ObserveTextEnd(evt.TextMessageEnd);
                break;
            case AGUIEvent.EventOneofCase.RunFinished:
                ObserveRunFinished(evt.RunFinished);
                break;
            case AGUIEvent.EventOneofCase.RunError:
                ObserveRunError(evt.RunError);
                break;
        }
    }

    private void ObserveTextStart(TextMessageStartEvent evt)
    {
        _currentMessageId = evt.MessageId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(_currentMessageId) && !_messageBuffers.ContainsKey(_currentMessageId))
            _messageBuffers[_currentMessageId] = new StringBuilder();
    }

    private void ObserveTextContent(TextMessageContentEvent evt)
    {
        var messageId = string.IsNullOrWhiteSpace(evt.MessageId)
            ? _currentMessageId
            : evt.MessageId;
        if (string.IsNullOrWhiteSpace(messageId))
            messageId = "_default";

        if (!_messageBuffers.TryGetValue(messageId, out var buffer))
        {
            buffer = new StringBuilder();
            _messageBuffers[messageId] = buffer;
        }

        buffer.Append(evt.Delta ?? string.Empty);
        _currentMessageId = messageId;
        _lastObservedOutput = buffer.ToString();
    }

    private void ObserveTextEnd(TextMessageEndEvent evt)
    {
        var messageId = string.IsNullOrWhiteSpace(evt.MessageId)
            ? _currentMessageId
            : evt.MessageId;
        if (!string.IsNullOrWhiteSpace(messageId) &&
            _messageBuffers.TryGetValue(messageId, out var buffer))
        {
            _lastCompletedOutput = buffer.ToString();
            _lastObservedOutput = _lastCompletedOutput;
        }
    }

    private void ObserveRunFinished(RunFinishedEvent evt)
    {
        if (Status == ServiceRunStatus.OutcomeUncertain)
            return;

        HasTerminalObservation = true;
        Status = ServiceRunStatus.Completed;
        LastError = string.Empty;
        LastOutput = TryUnpackOutput(evt, out var output)
            ? output
            : ResolveObservedOutput();
    }

    private void ObserveRunError(RunErrorEvent evt)
    {
        if (Status == ServiceRunStatus.OutcomeUncertain)
            return;

        HasTerminalObservation = true;
        Status = string.Equals(
            evt.Code,
            GAgentRunFailureCodes.OutcomeUncertain,
            StringComparison.Ordinal)
            ? ServiceRunStatus.OutcomeUncertain
            : ServiceRunStatus.Failed;
        LastOutput = ResolveObservedOutput();
        LastError = evt.Message ?? string.Empty;
    }

    private string ResolveObservedOutput() =>
        !string.IsNullOrEmpty(_lastCompletedOutput)
            ? _lastCompletedOutput
            : _lastObservedOutput;

    private static bool TryUnpackOutput(RunFinishedEvent evt, out string output)
    {
        output = string.Empty;
        if (evt.Result?.Is(GAgentDraftRunResultPayload.Descriptor) != true)
            return false;

        output = evt.Result.Unpack<GAgentDraftRunResultPayload>().Output ?? string.Empty;
        return true;
    }
}
