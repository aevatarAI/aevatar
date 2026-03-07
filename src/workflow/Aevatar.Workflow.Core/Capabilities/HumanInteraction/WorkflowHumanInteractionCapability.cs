using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowHumanInteractionCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "human_interaction",
        SupportedStepTypes: ["human_input", "human_approval"]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "human_input" => HandleHumanGateStepAsync(request, "human_input", read, write, ct),
            "human_approval" => HandleHumanGateStepAsync(request, "human_approval", read, write, ct),
            _ => Task.CompletedTask,
        };

    public bool CanHandleCompletion(StepCompletedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleCompletionAsync(
        StepCompletedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleInternalSignal(EventEnvelope envelope, WorkflowRunReadContext read) => false;

    public Task HandleInternalSignalAsync(
        EventEnvelope envelope,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResponse(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleChildRunCompletion(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read) =>
        false;

    public Task HandleChildRunCompletionAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read)
    {
        return string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), read.RunId, StringComparison.Ordinal) &&
               WorkflowPendingTokenLookup.TryResolvePendingHumanGate(read.State, evt.ResumeToken, out _);
    }

    public async Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (!WorkflowPendingTokenLookup.TryResolvePendingHumanGate(read.State, evt.ResumeToken, out var pending))
            return;

        var next = read.State.Clone();
        next.PendingHumanGates.Remove(pending.StepId);
        next.Status = "active";
        await write.PersistStateAsync(next, ct);

        if (string.Equals(pending.GateType, "human_input", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(evt.UserInput) && !evt.Approved)
            {
                var onTimeout = string.IsNullOrWhiteSpace(pending.OnTimeout) ? "fail" : pending.OnTimeout;
                await write.PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = read.RunId,
                    Success = !string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase),
                    Output = pending.Input,
                    Error = string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase)
                        ? "Human input timed out"
                        : string.Empty,
                }, EventDirection.Self, ct);
                return;
            }

            await write.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = read.RunId,
                Success = true,
                Output = evt.UserInput ?? string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var rejected = !evt.Approved;
        var rejectionOutput = !string.IsNullOrEmpty(evt.UserInput)
            ? $"[Previous content]\n{pending.Input}\n\n[User feedback]\n{evt.UserInput}"
            : pending.Input;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = read.RunId,
            Success = !rejected || !string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase),
            Output = rejected
                ? rejectionOutput
                : string.IsNullOrEmpty(evt.UserInput) ? pending.Input : evt.UserInput,
            Error = rejected && string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase)
                ? "Human approval rejected"
                : string.Empty,
        };
        completed.Metadata["branch"] = evt.Approved ? "true" : "false";
        await write.PublishAsync(completed, EventDirection.Self, ct);
    }

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static async Task HandleHumanGateStepAsync(
        StepRequestEvent request,
        string gateType,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        CancellationToken ct)
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

        var next = read.State.Clone();
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
        await write.PersistStateAsync(next, ct);

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = read.RunId,
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
        await write.PublishAsync(suspended, EventDirection.Both, ct);
    }
}
