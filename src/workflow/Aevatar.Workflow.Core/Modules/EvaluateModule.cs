using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.Collections;
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
    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;

    public EvaluateModule(WorkflowStepTargetAgentResolver? targetAgentResolver = null)
    {
        _targetAgentResolver = targetAgentResolver;
    }

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
            var stepId = request.StepId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stepId))
            {
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = false,
                    Error = "evaluate step requires non-empty step_id",
                }, TopologyAudience.Self, ct);
                return;
            }

            var prompt = $"""
                Evaluate the following content on these criteria: {criteria}
                Use a numeric scale of {scale}. Respond with ONLY a single number (the score).

                Content to evaluate:
                {request.Input}
                """;

            var state = WorkflowExecutionStateAccess.Load<EvaluateModuleState>(ctx, ModuleStateKey);
            var attemptKey = BuildAttemptKey(runId, stepId);
            var attempt = state.AttemptsByStepId.GetValueOrDefault(attemptKey, 0) + 1;
            state.AttemptsByStepId[attemptKey] = attempt;

            WorkflowStepTargetAgentResolution target;
            try
            {
                target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
            }
            catch (Exception ex)
            {
                state.AttemptsByStepId.Remove(attemptKey);
                await SaveStateAsync(state, ctx, CancellationToken.None);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = false,
                    Error = $"evaluate target resolution failed: {ex.Message}",
                }, TopologyAudience.Self, ct);
                return;
            }

            var sessionId = CreateSessionId(ctx.AgentId, runId, stepId, attempt);
            state.PendingBySessionId[sessionId] = new EvalContextState
            {
                StepId = stepId,
                RunId = runId,
                OriginalInput = request.Input ?? string.Empty,
                Threshold = threshold,
                OnBelow = onBelow,
            };
            await SaveStateAsync(state, ctx, ct);

            var chatRequest = new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = sessionId,
            };
            CopyParametersToChatMetadata(request.Parameters, chatRequest.Metadata);
            try
            {
                if (!target.UseSelf)
                {
                    ctx.Logger.LogInformation(
                        "EvaluateModule: step={StepId} → SendTo mode={Mode} actor={ActorId}",
                        stepId,
                        target.Mode,
                        target.ActorId);
                    await ctx.SendToAsync(target.ActorId, chatRequest, ct);
                }
                else
                {
                    await ctx.PublishAsync(chatRequest, TopologyAudience.Self, ct);
                }
            }
            catch (Exception ex)
            {
                state.PendingBySessionId.Remove(sessionId);
                state.AttemptsByStepId.Remove(attemptKey);
                await SaveStateAsync(state, ctx, CancellationToken.None);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = stepId,
                    RunId = runId,
                    Success = false,
                    Error = $"evaluate dispatch failed: {ex.Message}",
                }, TopologyAudience.Self, ct);
                return;
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
        if (!stateForCompletion.PendingBySessionId.TryGetValue(sid, out var evalCtx))
            return;

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

        stateForCompletion.PendingBySessionId.Remove(sid);
        stateForCompletion.AttemptsByStepId.Remove(BuildAttemptKey(evalCtx.RunId, evalCtx.StepId));
        await SaveStateAsync(stateForCompletion, ctx, ct);
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

    private static void CopyParametersToChatMetadata(
        MapField<string, string> parameters,
        MapField<string, string> metadata)
    {
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(key, "agent_type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "agent_id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metadata[key.Trim()] = value.Trim();
        }
    }

    private WorkflowStepTargetAgentResolver ResolveTargetAgentResolver(IEventContext ctx)
    {
        if (_targetAgentResolver != null)
            return _targetAgentResolver;

        var resolver = ctx.Services.GetService(typeof(WorkflowStepTargetAgentResolver)) as WorkflowStepTargetAgentResolver;
        if (resolver != null)
            return resolver;

        return new WorkflowStepTargetAgentResolver();
    }

    private static string BuildAttemptKey(string runId, string stepId) =>
        string.IsNullOrWhiteSpace(runId) ? stepId : $"{runId}:{stepId}";

    private static string CreateSessionId(string scopeId, string runId, string stepId, int attempt) =>
        string.IsNullOrWhiteSpace(runId)
            ? ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, $"{stepId}:a{attempt}")
            : ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, runId, stepId, attempt);
}
