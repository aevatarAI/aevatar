using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowReflectCapability : IWorkflowRunCapability
{
    private static readonly WorkflowRunCapabilityDescriptor DescriptorInstance = new(
        Name: "reflect",
        SupportedStepTypes: ["reflect"],
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
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var maxRounds = int.TryParse(request.Parameters.GetValueOrDefault("max_rounds", "3"), out var parsedMaxRounds)
            ? Math.Clamp(parsedMaxRounds, 1, 10)
            : 3;
        var criteria = request.Parameters.GetValueOrDefault("criteria", "quality and correctness");
        var initialState = new WorkflowPendingReflectState
        {
            SessionId = string.Empty,
            StepId = request.StepId,
            TargetRole = request.TargetRole ?? string.Empty,
            CurrentDraft = request.Input ?? string.Empty,
            Criteria = criteria,
            MaxRounds = maxRounds,
            Round = 0,
            Phase = "critique",
        };

        await DispatchReflectPhaseAsync(runId, initialState, request.Input ?? string.Empty, read, write, effects, ct);
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
               read.State.PendingReflections.ContainsKey(sessionId);
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
        if (!state.PendingReflections.TryGetValue(sessionId, out var pending))
            return;

        var next = state.Clone();
        next.PendingReflections.Remove(sessionId);
        await write.PersistStateAsync(next, ct);

        if (string.Equals(pending.Phase, "critique", StringComparison.OrdinalIgnoreCase))
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = pending.Round + 1;
            if (passed || round >= pending.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = state.RunId,
                    Success = true,
                    Output = pending.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString(CultureInfo.InvariantCulture);
                completed.Metadata["reflect.passed"] = passed.ToString();
                await write.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var nextPending = pending.Clone();
            nextPending.Round = round;
            nextPending.Phase = "improve";
            await DispatchReflectPhaseAsync(state.RunId, nextPending, content, read, write, effects, ct);
            return;
        }

        var critiquePending = pending.Clone();
        critiquePending.CurrentDraft = content;
        critiquePending.Phase = "critique";
        await DispatchReflectPhaseAsync(state.RunId, critiquePending, content, read, write, effects, ct);
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

    private static async Task DispatchReflectPhaseAsync(
        string runId,
        WorkflowPendingReflectState pending,
        string content,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects,
        CancellationToken ct)
    {
        await effects.EnsureAgentTreeAsync(ct);

        var prompt = string.Equals(pending.Phase, "critique", StringComparison.OrdinalIgnoreCase)
            ? $"""
                Review the following content against these criteria: {pending.Criteria}
                If the content meets the criteria, respond with exactly "PASS".
                Otherwise, explain what needs improvement.

                Content:
                {content}
                """
            : $"""
                Improve the following content based on this feedback.

                Feedback:
                {content}

                Original content:
                {pending.CurrentDraft}
                """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(
            read.ActorId,
            runId,
            $"{pending.StepId}_r{pending.Round}_{pending.Phase}");
        var nextPending = pending.Clone();
        nextPending.SessionId = sessionId;

        var next = read.State.Clone();
        next.PendingReflections[sessionId] = nextPending;
        await write.PersistStateAsync(next, ct);

        var request = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(nextPending.TargetRole))
        {
            await write.SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(read.ActorId, nextPending.TargetRole),
                request,
                ct);
            return;
        }

        await write.PublishAsync(request, EventDirection.Self, ct);
    }

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
