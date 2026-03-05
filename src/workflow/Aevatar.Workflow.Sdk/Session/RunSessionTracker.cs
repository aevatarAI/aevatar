using System.Text.Json;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;

namespace Aevatar.Workflow.Sdk.Session;

public sealed record RunSessionSnapshot
{
    public string? ActorId { get; init; }
    public string? WorkflowName { get; init; }
    public string? CommandId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? SuspensionType { get; init; }
    public string? LastSignalName { get; init; }

    public bool CanResume =>
        !string.IsNullOrWhiteSpace(ActorId) &&
        !string.IsNullOrWhiteSpace(RunId) &&
        !string.IsNullOrWhiteSpace(StepId);

    public bool CanSignal =>
        !string.IsNullOrWhiteSpace(ActorId) &&
        !string.IsNullOrWhiteSpace(RunId) &&
        !string.IsNullOrWhiteSpace(LastSignalName);
}

public sealed class RunSessionTracker
{
    private string? _actorId;
    private string? _workflowName;
    private string? _commandId;
    private string? _runId;
    private string? _stepId;
    private string? _suspensionType;
    private string? _lastSignalName;

    public RunSessionSnapshot Snapshot => new()
    {
        ActorId = _actorId,
        WorkflowName = _workflowName,
        CommandId = _commandId,
        RunId = _runId,
        StepId = _stepId,
        SuspensionType = _suspensionType,
        LastSignalName = _lastSignalName,
    };

    public void Track(WorkflowEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        Track(evt.Frame);
    }

    public void Track(WorkflowOutputFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.Equals(frame.Type, WorkflowEventTypes.RunStarted, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(frame.ThreadId))
        {
            _actorId ??= frame.ThreadId;
            return;
        }

        if (!string.Equals(frame.Type, WorkflowEventTypes.Custom, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(frame.Name))
        {
            return;
        }

        TrackCustomFrame(frame.Name!, frame.Value);
    }

    public WorkflowResumeRequest CreateResumeRequest(
        bool approved,
        string? userInput = null,
        IDictionary<string, string>? metadata = null,
        string? commandId = null)
    {
        EnsureResumeContext();
        return new WorkflowResumeRequest
        {
            ActorId = _actorId!,
            RunId = _runId!,
            StepId = _stepId!,
            Approved = approved,
            UserInput = userInput,
            CommandId = commandId ?? _commandId,
            Metadata = metadata,
        };
    }

    public WorkflowSignalRequest CreateSignalRequest(
        string? signalName = null,
        string? payload = null,
        string? commandId = null,
        string? stepId = null)
    {
        EnsureNotBlank(_actorId, "actorId");
        EnsureNotBlank(_runId, "runId");

        var resolvedSignalName = string.IsNullOrWhiteSpace(signalName)
            ? _lastSignalName
            : signalName.Trim();
        EnsureNotBlank(resolvedSignalName, "signalName");
        var resolvedStepId = string.IsNullOrWhiteSpace(stepId)
            ? _stepId
            : stepId.Trim();

        return new WorkflowSignalRequest
        {
            ActorId = _actorId!,
            RunId = _runId!,
            SignalName = resolvedSignalName!,
            StepId = resolvedStepId,
            Payload = payload,
            CommandId = commandId ?? _commandId,
        };
    }

    private void TrackCustomFrame(string customEventName, JsonElement? value)
    {
        if (WorkflowCustomEventParser.TryParseRunContext(customEventName, value, out var runContext))
        {
            _actorId = runContext.ActorId ?? _actorId;
            _workflowName = runContext.WorkflowName ?? _workflowName;
            _commandId = runContext.CommandId ?? _commandId;
            return;
        }

        if (WorkflowCustomEventParser.TryParseStepRequest(customEventName, value, out var stepRequest))
        {
            _runId = stepRequest.RunId ?? _runId;
            _stepId = stepRequest.StepId ?? _stepId;
            return;
        }

        if (WorkflowCustomEventParser.TryParseStepCompleted(customEventName, value, out var stepCompleted))
        {
            _runId = stepCompleted.RunId ?? _runId;
            _stepId = stepCompleted.StepId ?? _stepId;
            return;
        }

        if (WorkflowCustomEventParser.TryParseHumanInputRequest(customEventName, value, out var humanInput))
        {
            _runId = humanInput.RunId ?? _runId;
            _stepId = humanInput.StepId ?? _stepId;
            _suspensionType = humanInput.SuspensionType ?? _suspensionType;
            return;
        }

        if (WorkflowCustomEventParser.TryParseWaitingSignal(customEventName, value, out var waitingSignal))
        {
            _runId = waitingSignal.RunId ?? _runId;
            _stepId = waitingSignal.StepId ?? _stepId;
            _lastSignalName = waitingSignal.SignalName ?? _lastSignalName;
        }
    }

    private void EnsureResumeContext()
    {
        EnsureNotBlank(_actorId, "actorId");
        EnsureNotBlank(_runId, "runId");
        EnsureNotBlank(_stepId, "stepId");
    }

    private static void EnsureNotBlank(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw AevatarWorkflowException.InvalidRequest($"Run session is missing '{field}'.");
    }
}
