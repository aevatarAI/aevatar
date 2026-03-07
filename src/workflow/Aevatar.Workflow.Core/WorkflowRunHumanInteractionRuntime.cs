using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunHumanInteractionRuntime
{
    private readonly Func<WorkflowRunState> _stateAccessor;
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;

    public WorkflowRunHumanInteractionRuntime(
        Func<WorkflowRunState> stateAccessor,
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync)
    {
        _stateAccessor = stateAccessor ?? throw new ArgumentNullException(nameof(stateAccessor));
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
    }

    public async Task HandleHumanGateStepRequestAsync(StepRequestEvent request, string gateType, CancellationToken ct)
    {
        var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
            request.Parameters,
            gateType == "human_input" ? 1800 : 3600);
        var prompt = WorkflowParameterValueParser.GetString(
            request.Parameters,
            gateType == "human_input" ? "Please provide input:" : "Approve this step?",
            "prompt",
            "message");
        var variable = WorkflowParameterValueParser.GetString(request.Parameters, "user_input", "variable");
        var onTimeout = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_timeout");
        var onReject = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_reject");

        var state = _stateAccessor();
        var next = state.Clone();
        next.Status = "suspended";
        next.PendingHumanGates[request.StepId] = new WorkflowPendingHumanGateState
        {
            StepId = request.StepId,
            GateType = gateType,
            Input = request.Input ?? string.Empty,
            Prompt = prompt,
            Variable = variable,
            TimeoutSeconds = timeoutSeconds,
            OnTimeout = onTimeout,
            OnReject = onReject,
            ResumeToken = Guid.NewGuid().ToString("N"),
        };
        await _persistStateAsync(next, ct);

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = state.RunId,
            StepId = request.StepId,
            SuspensionType = gateType,
            Prompt = prompt,
            TimeoutSeconds = timeoutSeconds,
            ResumeToken = next.PendingHumanGates[request.StepId].ResumeToken,
        };
        if (!string.IsNullOrWhiteSpace(variable))
            suspended.Metadata["variable"] = variable;
        if (!string.IsNullOrWhiteSpace(onTimeout))
            suspended.Metadata["on_timeout"] = onTimeout;
        if (!string.IsNullOrWhiteSpace(onReject))
            suspended.Metadata["on_reject"] = onReject;
        suspended.Metadata["resume_token"] = next.PendingHumanGates[request.StepId].ResumeToken;
        await _publishAsync(suspended, EventDirection.Both, ct);
    }
}
