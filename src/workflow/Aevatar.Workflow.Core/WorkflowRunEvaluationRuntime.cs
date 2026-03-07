using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunEvaluationRuntime
    : IWorkflowStepFamilyHandler
{
    private static readonly string[] SupportedTypes = ["evaluate"];

    private readonly WorkflowRunRuntimeContext _context;

    public WorkflowRunEvaluationRuntime(WorkflowRunRuntimeContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyCollection<string> SupportedStepTypes => SupportedTypes;

    public Task HandleStepRequestAsync(StepRequestEvent request, CancellationToken ct) =>
        HandleEvaluateStepRequestAsync(request, ct);

    public async Task HandleEvaluateStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await _context.EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var criteria = request.Parameters.GetValueOrDefault("criteria", "quality");
        var scale = request.Parameters.GetValueOrDefault("scale", "1-5");
        var threshold = double.TryParse(request.Parameters.GetValueOrDefault("threshold", "3"), out var parsedThreshold)
            ? parsedThreshold
            : 3.0;
        var onBelow = request.Parameters.GetValueOrDefault("on_below", string.Empty);

        var state = _context.State;
        var attempt = state.StepExecutions.TryGetValue(request.StepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(_context.ActorId, runId, request.StepId, attempt);
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
        await _context.PersistStateAsync(next, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await _context.SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(_context.ActorId, request.TargetRole),
                chatRequest,
                ct);
            return;
        }

        await _context.PublishAsync(chatRequest, EventDirection.Self, ct);
    }

    public async Task<bool> TryHandleLlmLikeResponseAsync(
        string sessionId,
        string content,
        CancellationToken ct)
    {
        var state = _context.State;
        if (!state.PendingEvaluations.TryGetValue(sessionId, out var pending))
            return false;

        var score = WorkflowRunSupport.ParseScore(content);
        var passed = score >= pending.Threshold;
        var next = state.Clone();
        next.PendingEvaluations.Remove(sessionId);
        await _context.PersistStateAsync(next, ct);

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
        await _context.PublishAsync(completed, EventDirection.Self, ct);
        return true;
    }
}
