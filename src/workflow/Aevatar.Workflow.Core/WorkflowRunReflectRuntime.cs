using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunReflectRuntime
{
    private readonly WorkflowRunRuntimeContext _context;

    public WorkflowRunReflectRuntime(WorkflowRunRuntimeContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task HandleReflectStepRequestAsync(StepRequestEvent request, CancellationToken ct)
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

        await DispatchReflectPhaseAsync(runId, initialState, request.Input ?? string.Empty, ct);
    }

    public async Task DispatchReflectPhaseAsync(
        string runId,
        WorkflowPendingReflectState pending,
        string content,
        CancellationToken ct)
    {
        await _context.EnsureAgentTreeAsync(ct);

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
            _context.ActorId,
            runId,
            $"{pending.StepId}_r{pending.Round}_{pending.Phase}");
        var nextPending = pending.Clone();
        nextPending.SessionId = sessionId;

        var next = _context.State.Clone();
        next.PendingReflections[sessionId] = nextPending;
        await _context.PersistStateAsync(next, ct);

        var request = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(nextPending.TargetRole))
        {
            await _context.SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(_context.ActorId, nextPending.TargetRole),
                request,
                ct);
            return;
        }

        await _context.PublishAsync(request, EventDirection.Self, ct);
    }

    public async Task<bool> TryHandleLlmLikeResponseAsync(
        string sessionId,
        string content,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!state.PendingReflections.TryGetValue(sessionId, out var pending))
            return false;

        var next = state.Clone();
        next.PendingReflections.Remove(sessionId);
        await _context.PersistStateAsync(next, ct);

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
                await _context.PublishAsync(completed, EventDirection.Self, ct);
                return true;
            }

            var nextPending = pending.Clone();
            nextPending.Round = round;
            nextPending.Phase = "improve";
            await DispatchReflectPhaseAsync(state.RunId, nextPending, content, ct);
            return true;
        }

        var critiquePending = pending.Clone();
        critiquePending.CurrentDraft = content;
        critiquePending.Phase = "critique";
        await DispatchReflectPhaseAsync(state.RunId, critiquePending, content, ct);
        return true;
    }
}
