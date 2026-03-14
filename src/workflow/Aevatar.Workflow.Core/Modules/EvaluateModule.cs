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
public sealed class EvaluateModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "evaluate";

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

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
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

            var state = WorkflowExecutionStateAccess.Load<EvaluateModuleState>(ctx, ModuleStateKey);
            var stepKey = request.StepId ?? string.Empty;
            var attempt = state.AttemptsByStepId.GetValueOrDefault(stepKey, 0) + 1;
            state.AttemptsByStepId[stepKey] = attempt;

            var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, runId, stepKey, attempt);
            state.PendingBySessionId[sessionId] = new EvalContextState
            {
                StepId = stepKey,
                RunId = runId,
                OriginalInput = request.Input ?? string.Empty,
                Threshold = threshold,
                OnBelow = onBelow,
            };
            await SaveStateAsync(state, ctx, ct);

            var targetRole = request.TargetRole;
            try
            {
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
                    }, TopologyAudience.Self, ct);
                }
            }
            catch
            {
                state.PendingBySessionId.Remove(sessionId);
                state.AttemptsByStepId.Remove(stepKey);
                await SaveStateAsync(state, ctx, CancellationToken.None);
                throw;
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

        if (sid == null)
            return;

        var stateForCompletion = WorkflowExecutionStateAccess.Load<EvaluateModuleState>(ctx, ModuleStateKey);
        if (!stateForCompletion.PendingBySessionId.Remove(sid, out var evalCtx))
            return;
        stateForCompletion.AttemptsByStepId.Remove(evalCtx.StepId);
        await SaveStateAsync(stateForCompletion, ctx, ct);

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
        completed.Annotations["evaluate.score"] = score.ToString("F1");
        completed.Annotations["evaluate.passed"] = passed.ToString();

        if (!passed && !string.IsNullOrEmpty(evalCtx.OnBelow))
            completed.BranchKey = evalCtx.OnBelow;

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
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

    private static Task SaveStateAsync(
        EvaluateModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingBySessionId.Count == 0 && state.AttemptsByStepId.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

}
