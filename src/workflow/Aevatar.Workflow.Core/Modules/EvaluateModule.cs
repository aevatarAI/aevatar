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
public sealed class EvaluateModule : IEventModule
{
    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;
    private readonly Dictionary<string, EvalContext> _pending = [];
    private readonly Dictionary<string, int> _attemptsByRunStep = [];

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

            WorkflowStepTargetAgentResolution target;
            try
            {
                target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
            }
            catch (Exception ex)
            {
                _pending.Remove(sessionId);
                _attemptsByRunStep.Remove(stepRunKey);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = false,
                    Error = $"evaluate target resolution failed: {ex.Message}",
                }, EventDirection.Self, ct);
                return;
            }

            try
            {
                if (!target.UseSelf)
                {
                    ctx.Logger.LogInformation(
                        "EvaluateModule: step={StepId} → SendTo mode={Mode} actor={ActorId}",
                        request.StepId, target.Mode, target.ActorId);
                    var chatRequest = new ChatRequestEvent
                    {
                        Prompt = prompt,
                        SessionId = sessionId,
                    };
                    CopyParametersToChatMetadata(request.Parameters, chatRequest.Metadata);
                    await ctx.SendToAsync(target.ActorId, chatRequest, ct);
                }
                else
                {
                    var chatRequest = new ChatRequestEvent
                    {
                        Prompt = prompt,
                        SessionId = sessionId,
                    };
                    CopyParametersToChatMetadata(request.Parameters, chatRequest.Metadata);
                    await ctx.PublishAsync(chatRequest, EventDirection.Self, ct);
                }
            }
            catch (Exception ex)
            {
                _pending.Remove(sessionId);
                _attemptsByRunStep.Remove(stepRunKey);
                await ctx.PublishAsync(new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = runId,
                    Success = false,
                    Error = $"evaluate dispatch failed: {ex.Message}",
                }, EventDirection.Self, ct);
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

    private WorkflowStepTargetAgentResolver ResolveTargetAgentResolver(IEventHandlerContext ctx)
    {
        if (_targetAgentResolver != null)
            return _targetAgentResolver;

        var resolver = ctx.Services.GetService(typeof(WorkflowStepTargetAgentResolver)) as WorkflowStepTargetAgentResolver;
        if (resolver != null)
            return resolver;

        throw new InvalidOperationException(
            $"{nameof(WorkflowStepTargetAgentResolver)} is not registered in DI and was not provided to {nameof(EvaluateModule)}.");
    }

    private sealed record EvalContext(string StepId, string RunId, string OriginalInput, double Threshold, string OnBelow);
}
