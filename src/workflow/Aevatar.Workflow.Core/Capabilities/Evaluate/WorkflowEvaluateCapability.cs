using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowEvaluateCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "evaluate",
        SupportedStepTypes: ["evaluate"],
        SupportedResponseTypeUrls:
        [
            WorkflowCapabilityRoutes.For<TextMessageEndEvent>(),
            WorkflowCapabilityRoutes.For<ChatResponseEvent>(),
        ]);

    public IWorkflowRunCapabilityDescriptor Descriptor => DescriptorInstance;

    public async Task HandleStepAsync(
        StepRequestEvent request,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        await effects.EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var criteria = request.Parameters.GetValueOrDefault("criteria", "quality");
        var scale = request.Parameters.GetValueOrDefault("scale", "1-5");
        var threshold = double.TryParse(request.Parameters.GetValueOrDefault("threshold", "3"), out var parsedThreshold)
            ? parsedThreshold
            : 3.0;
        var onBelow = request.Parameters.GetValueOrDefault("on_below", string.Empty);

        var state = read.State;
        var attempt = state.StepExecutions.TryGetValue(request.StepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(read.ActorId, runId, request.StepId, attempt);
        var prompt = $"""
            Evaluate the following content on these criteria: {criteria}
            Use a numeric scale of {scale}. Respond with ONLY a single number (the score).

            Content to evaluate:
            {request.Input}
            """;

        var next = state.Clone();
        next.PendingEvaluations[sessionId] = new WorkflowPendingEvaluateState
        {
            SessionId = sessionId,
            StepId = request.StepId,
            OriginalInput = request.Input ?? string.Empty,
            Threshold = threshold,
            OnBelow = onBelow,
            TargetRole = request.TargetRole ?? string.Empty,
            Attempt = attempt,
        };
        await write.PersistStateAsync(next, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await write.SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(read.ActorId, request.TargetRole),
                chatRequest,
                ct);
            return;
        }

        await write.PublishAsync(chatRequest, EventDirection.Self, ct);
    }

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
        WorkflowRunReadContext read)
    {
        return TryExtractSession(envelope, out var sessionId, out _) &&
               !string.IsNullOrWhiteSpace(sessionId) &&
               read.State.PendingEvaluations.ContainsKey(sessionId);
    }

    public async Task HandleResponseAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        if (!TryExtractSession(envelope, out var sessionId, out var content) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var state = read.State;
        if (!state.PendingEvaluations.TryGetValue(sessionId, out var pending))
            return;

        var score = WorkflowCapabilityValueParsers.ParseScore(content);
        var passed = score >= pending.Threshold;
        var next = state.Clone();
        next.PendingEvaluations.Remove(sessionId);
        await write.PersistStateAsync(next, ct);

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = state.RunId,
            Success = true,
            Output = pending.OriginalInput,
        };
        completed.Metadata["evaluate.score"] = score.ToString("F1", CultureInfo.InvariantCulture);
        completed.Metadata["evaluate.passed"] = passed.ToString();
        if (!passed && !string.IsNullOrWhiteSpace(pending.OnBelow))
            completed.Metadata["branch"] = pending.OnBelow;
        await write.PublishAsync(completed, EventDirection.Self, ct);
    }

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

    public bool CanHandleResume(WorkflowResumedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleResumeAsync(
        WorkflowResumedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    public bool CanHandleExternalSignal(SignalReceivedEvent evt, WorkflowRunReadContext read) => false;

    public Task HandleExternalSignalAsync(
        SignalReceivedEvent evt,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct) =>
        Task.CompletedTask;

    private static bool TryExtractSession(EventEnvelope envelope, out string sessionId, out string content)
    {
        sessionId = string.Empty;
        content = string.Empty;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            sessionId = evt.SessionId ?? string.Empty;
            content = evt.Content ?? string.Empty;
            return true;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            sessionId = evt.SessionId ?? string.Empty;
            content = evt.Content ?? string.Empty;
            return true;
        }

        return false;
    }
}
