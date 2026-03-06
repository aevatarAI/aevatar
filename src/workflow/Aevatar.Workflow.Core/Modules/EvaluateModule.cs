using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// LLM-as-Judge evaluation module.
/// Sends a structured evaluation prompt to the target role, parses the numeric score
/// from the response, and applies threshold-based branching.
/// </summary>
public sealed class EvaluateModule : IEventModule
{
    private readonly Dictionary<string, EvalContext> _pending = [];
    private readonly Dictionary<string, int> _attemptsByRunStep = [];

    public string Name => "evaluate";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var p = envelope.Payload;
        return p != null &&
               (p.Is(StepRequestEvent.Descriptor) ||
                p.Is(TextMessageEndEvent.Descriptor) ||
                p.Is(ChatResponseEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "evaluate") return;

            var criteria = request.Parameters.GetValueOrDefault("criteria", "quality");
            var scale = request.Parameters.GetValueOrDefault("scale", "1-5");
            var thresholdStr = request.Parameters.GetValueOrDefault("threshold", "3");
            var threshold = double.TryParse(thresholdStr, out var th) ? th : 3.0;
            var onBelow = request.Parameters.GetValueOrDefault("on_below", "");
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);

            var prompt = $"""
                Evaluate the following content on these criteria: {criteria}
                Use a numeric scale of {scale}. Respond with ONLY a single number (the score).

                Content to evaluate:
                {request.Input}
                """;

            var stepRunKey = $"{runId}:{request.StepId}";
            var attempt = _attemptsByRunStep.GetValueOrDefault(stepRunKey, 0) + 1;
            _attemptsByRunStep[stepRunKey] = attempt;

            var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, runId, request.StepId, attempt);
            _pending[sessionId] = new EvalContext(request.StepId, runId, request.Input ?? "", threshold, onBelow);

            var targetRole = request.TargetRole;
            if (!string.IsNullOrEmpty(targetRole))
            {
                var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, targetRole);
                ctx.Logger.LogInformation(
                    "EvaluateModule: step={StepId} → SendTo role={Role} actor={ActorId}",
                    request.StepId, targetRole, targetActorId);
                await ctx.SendToAsync(targetActorId, new ChatRequestEvent
                {
                    Prompt = prompt, SessionId = sessionId,
                }, ct);
            }
            else
            {
                await ctx.PublishAsync(new ChatRequestEvent
                {
                    Prompt = prompt, SessionId = sessionId,
                }, EventDirection.Self, ct);
            }
            return;
        }

        string? content = null;
        string? sid = null;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            content = evt.Content; sid = evt.SessionId;
        }
        else if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            content = evt.Content; sid = evt.SessionId;
        }

        if (sid == null || !_pending.Remove(sid, out var evalCtx)) return;
        _attemptsByRunStep.Remove($"{evalCtx.RunId}:{evalCtx.StepId}");

        var score = ParseScore(content ?? "");
        var passed = score >= evalCtx.Threshold;

        ctx.Logger.LogInformation("Evaluate {StepId}: score={Score} threshold={Threshold} passed={Passed}",
            evalCtx.StepId, score, evalCtx.Threshold, passed);

        var completed = new StepCompletedEvent
        {
            StepId = evalCtx.StepId,
            RunId = evalCtx.RunId,
            Success = true,
            Output = evalCtx.OriginalInput,
        };
        completed.Metadata["evaluate.score"] = score.ToString("F1");
        completed.Metadata["evaluate.passed"] = passed.ToString();

        if (!passed && !string.IsNullOrEmpty(evalCtx.OnBelow))
            completed.Metadata["branch"] = evalCtx.OnBelow;

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }

    private static double ParseScore(string text)
    {
        var trimmed = text.Trim();
        if (double.TryParse(trimmed, out var d)) return d;

        foreach (var word in trimmed.Split([' ', '\n', '\r', ',', '/', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(word, out var n)) return n;
        }
        return 0;
    }

    private sealed record EvalContext(string StepId, string RunId, string OriginalInput, double Threshold, string OnBelow);
}
