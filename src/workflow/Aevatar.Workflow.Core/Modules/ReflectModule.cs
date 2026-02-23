using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Self-reflection loop: draft → critique → improve → critique → ...
/// Repeats until critique says "PASS" or max rounds reached.
/// </summary>
public sealed class ReflectModule : IEventModule
{
    private readonly Dictionary<string, ReflectState> _states = [];
    private readonly Dictionary<string, ReflectState> _pendingLLM = [];

    public string Name => "reflect";
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
            if (request.StepType != "reflect") return;

            var maxRounds = int.TryParse(request.Parameters.GetValueOrDefault("max_rounds", "3"), out var mr) ? mr : 3;
            var criteria = request.Parameters.GetValueOrDefault("criteria", "quality and correctness");
            maxRounds = Math.Clamp(maxRounds, 1, 10);

            var state = new ReflectState(request.StepId, request.TargetRole ?? "", request.Input ?? "",
                criteria, maxRounds, 0, ReflectPhase.Critique);
            _states[request.StepId] = state;

            await SendCritiqueAsync(state, request.Input ?? "", ctx, ct);
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

        if (sid == null || !_pendingLLM.Remove(sid, out var state2)) return;
        content ??= "";

        if (state2.Phase == ReflectPhase.Critique)
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = state2.Round + 1;

            ctx.Logger.LogInformation("Reflect {StepId}: round={Round}/{Max} critique={Verdict}",
                state2.StepId, round, state2.MaxRounds, passed ? "PASS" : "NEEDS_IMPROVEMENT");

            if (passed || round >= state2.MaxRounds)
            {
                _states.Remove(state2.StepId);
                var completed = new StepCompletedEvent
                {
                    StepId = state2.StepId, Success = true, Output = state2.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString();
                completed.Metadata["reflect.passed"] = passed.ToString();
                await ctx.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var next = state2 with { Round = round, Phase = ReflectPhase.Improve };
            _states[state2.StepId] = next;
            await SendImproveAsync(next, content, ctx, ct);
        }
        else
        {
            var next = state2 with { CurrentDraft = content, Phase = ReflectPhase.Critique };
            _states[state2.StepId] = next;
            await SendCritiqueAsync(next, content, ctx, ct);
        }
    }

    private async Task SendCritiqueAsync(ReflectState state, string draft, IEventHandlerContext ctx, CancellationToken ct)
    {
        var prompt = $"""
            Review the following content against these criteria: {state.Criteria}
            If the content meets the criteria, respond with exactly "PASS".
            Otherwise, explain what needs improvement.

            Content:
            {draft}
            """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, $"{state.StepId}_r{state.Round}_critique");
        _pendingLLM[sessionId] = state;

        if (!string.IsNullOrEmpty(state.TargetRole))
        {
            var targetActorId = ResolveTargetActorId(ctx.AgentId, state.TargetRole);
            await ctx.SendToAsync(targetActorId, new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, ct);
        }
        else
            await ctx.PublishAsync(new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, EventDirection.Self, ct);
    }

    private async Task SendImproveAsync(ReflectState state, string critique, IEventHandlerContext ctx, CancellationToken ct)
    {
        var prompt = $"""
            Improve the following content based on this feedback.

            Feedback:
            {critique}

            Original content:
            {state.CurrentDraft}
            """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, $"{state.StepId}_r{state.Round}_improve");
        _pendingLLM[sessionId] = state;

        if (!string.IsNullOrEmpty(state.TargetRole))
        {
            var targetActorId = ResolveTargetActorId(ctx.AgentId, state.TargetRole);
            await ctx.SendToAsync(targetActorId, new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, ct);
        }
        else
            await ctx.PublishAsync(new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, EventDirection.Self, ct);
    }

    private static string ResolveTargetActorId(string workflowActorId, string targetRole)
    {
        if (string.IsNullOrWhiteSpace(targetRole)) return targetRole;
        return targetRole.Contains(':', StringComparison.Ordinal)
            ? targetRole
            : $"{workflowActorId}:{targetRole}";
    }

    private enum ReflectPhase { Critique, Improve }

    private sealed record ReflectState(
        string StepId, string TargetRole, string CurrentDraft,
        string Criteria, int MaxRounds, int Round, ReflectPhase Phase);
}
