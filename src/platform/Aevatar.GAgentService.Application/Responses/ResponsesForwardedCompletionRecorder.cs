using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.Presentation.AGUI;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed class ResponsesForwardedCompletionRecorder
{
    private readonly ILlmSessionRegistrationPort _sessionRegistrationPort;
    private readonly ILlmSessionQueryPort _sessionQueryPort;

    public ResponsesForwardedCompletionRecorder(
        ILlmSessionRegistrationPort sessionRegistrationPort,
        ILlmSessionQueryPort sessionQueryPort)
    {
        _sessionRegistrationPort = sessionRegistrationPort ?? throw new ArgumentNullException(nameof(sessionRegistrationPort));
        _sessionQueryPort = sessionQueryPort ?? throw new ArgumentNullException(nameof(sessionQueryPort));
    }

    public async Task<ResponsesForwardedCompletionRecordResult> RecordAsync(
        ResponsesForwardCommandResult plan,
        IEnumerable<AGUIEvent> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(events);

        var completion = BuildCompletion(events, DateTimeOffset.UtcNow);
        return await CommitAndReadAsync(plan, completion, ct);
    }

    public ResponsesForwardedCompletionCollector CreateCollector(ResponsesForwardCommandResult plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ResponsesForwardedCompletionCollector(this, plan);
    }

    public async Task<ResponsesForwardedCompletionRecordResult> CommitAndReadAsync(
        ResponsesForwardCommandResult plan,
        LlmSessionCompletion completion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completion);

        await _sessionRegistrationPort.RecordCompletionAsync(
            plan.Session.ActorId,
            plan.Session.ResponseId,
            completion,
            ct);

        var snapshot = await _sessionQueryPort.GetByResponseIdAsync(plan.Session.ResponseId, ct);
        if (snapshot?.Completion is null)
        {
            return ResponsesForwardedCompletionRecordResult.FromError(new ResponsesCommandError(
                503,
                "response_completion_not_observed",
                "Forwarded response completion was committed but is not yet visible in the read model."));
        }

        return ResponsesForwardedCompletionRecordResult.FromSnapshot(snapshot);
    }

    public static LlmSessionCompletion BuildCompletion(
        IEnumerable<AGUIEvent> events,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(events);

        var completion = new LlmSessionCompletion
        {
            CompletedAt = Timestamp.FromDateTimeOffset(completedAt),
        };
        var toolStarts = new List<(string ToolCallId, string ToolName)>();
        var observedLiveTextDelta = false;
        string? runFinishedOutput = null;

        foreach (var evt in events)
        {
            switch (evt.EventCase)
            {
                case AGUIEvent.EventOneofCase.TextMessageContent:
                    var delta = evt.TextMessageContent?.Delta ?? string.Empty;
                    if (delta.Length > 0)
                    {
                        observedLiveTextDelta = true;
                        completion.OutputText += delta;
                    }
                    break;
                case AGUIEvent.EventOneofCase.RunFinished:
                    runFinishedOutput = ResolveGAgentDraftRunOutput(evt.RunFinished?.Result) ?? runFinishedOutput;
                    break;
                case AGUIEvent.EventOneofCase.ToolCallStart:
                    if (!string.IsNullOrWhiteSpace(evt.ToolCallStart?.ToolCallId))
                    {
                        toolStarts.Add((
                            evt.ToolCallStart.ToolCallId.Trim(),
                            evt.ToolCallStart.ToolName?.Trim() ?? string.Empty));
                    }
                    break;
                case AGUIEvent.EventOneofCase.ToolCallEnd:
                    var end = evt.ToolCallEnd;
                    if (end is null || string.IsNullOrWhiteSpace(end.ToolCallId))
                        break;
                    var toolName = toolStarts.LastOrDefault(x =>
                        string.Equals(x.ToolCallId, end.ToolCallId.Trim(), StringComparison.Ordinal)).ToolName;
                    completion.ToolCalls.Add(new LlmSessionCompletedToolCall
                    {
                        CallId = end.ToolCallId.Trim(),
                        ToolName = toolName ?? string.Empty,
                        Result = ResponsesJsonValues.ParseBoundaryPayload(
                            string.IsNullOrWhiteSpace(end.Result) ? "{}" : end.Result),
                    });
                    break;
                case AGUIEvent.EventOneofCase.RunError:
                    completion.FailureCode = string.IsNullOrWhiteSpace(evt.RunError?.Code)
                        ? "gagent_invocation_failed"
                        : evt.RunError!.Code;
                    completion.FailureMessage = string.IsNullOrWhiteSpace(evt.RunError?.Message)
                        ? "GAgent invocation failed."
                        : evt.RunError!.Message;
                    break;
            }
        }

        // Refactor (iter98/cluster-790): Old: backend could synthesize missed-live TextMessageContent, duplicating output for clients that did observe deltas. New: consumers fallback to typed RunFinished.result.output only when no live delta was observed.
        if (!observedLiveTextDelta && !string.IsNullOrEmpty(runFinishedOutput))
            completion.OutputText = runFinishedOutput;

        return completion;
    }

    private static string? ResolveGAgentDraftRunOutput(Any? result)
    {
        if (result?.Is(GAgentDraftRunResultPayload.Descriptor) != true)
            return null;

        var payload = result.Unpack<GAgentDraftRunResultPayload>();
        return payload.Output ?? string.Empty;
    }

    public static LlmSessionCompletion BuildFailureCompletion(
        string code,
        string message,
        DateTimeOffset completedAt) =>
        new()
        {
            CompletedAt = Timestamp.FromDateTimeOffset(completedAt),
            FailureCode = string.IsNullOrWhiteSpace(code) ? "gagent_invocation_failed" : code,
            FailureMessage = string.IsNullOrWhiteSpace(message) ? "GAgent invocation failed." : message,
        };
}

public sealed record ResponsesForwardedCompletionRecordResult(
    ResponsesCommandError? Error,
    LlmSessionSnapshot? Snapshot)
{
    public static ResponsesForwardedCompletionRecordResult FromError(ResponsesCommandError error) =>
        new(error, null);

    public static ResponsesForwardedCompletionRecordResult FromSnapshot(LlmSessionSnapshot snapshot) =>
        new(null, snapshot);
}

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: ForwardToTeam/ForwardToGAgent skipped session lifecycle; Host new'd StringBuilder/Dictionary/List<ToolCall> to synthesize response.completed
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed class ResponsesForwardedCompletionCollector
{
    private readonly ResponsesForwardedCompletionRecorder _recorder;
    private readonly ResponsesForwardCommandResult _plan;
    private readonly List<AGUIEvent> _events = [];

    internal ResponsesForwardedCompletionCollector(
        ResponsesForwardedCompletionRecorder recorder,
        ResponsesForwardCommandResult plan)
    {
        _recorder = recorder;
        _plan = plan;
    }

    public bool HasFailureEvent { get; private set; }

    public ValueTask ObserveAsync(AGUIEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();
        if (evt.EventCase == AGUIEvent.EventOneofCase.RunError)
            HasFailureEvent = true;
        _events.Add(evt.Clone());
        return ValueTask.CompletedTask;
    }

    public Task<ResponsesForwardedCompletionRecordResult> CommitAndReadAsync(CancellationToken ct = default) =>
        _recorder.RecordAsync(_plan, _events, ct);

    public Task<ResponsesForwardedCompletionRecordResult> CommitFailureAndReadAsync(
        string code,
        string message,
        CancellationToken ct = default)
    {
        var completion = ResponsesForwardedCompletionRecorder.BuildFailureCompletion(
            code,
            message,
            DateTimeOffset.UtcNow);
        return _recorder.CommitAndReadAsync(_plan, completion, ct);
    }
}
