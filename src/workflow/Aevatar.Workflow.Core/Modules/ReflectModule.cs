using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Self-reflection loop: draft → critique → improve → critique → ...
/// Repeats until critique says "PASS" or max rounds reached.
/// </summary>
public sealed class ReflectModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "reflect";

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

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
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
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);

            var state = new ReflectState
            {
                StepId = request.StepId,
                RunId = runId,
                TargetRole = request.TargetRole ?? string.Empty,
                CurrentDraft = request.Input ?? string.Empty,
                Criteria = criteria,
                MaxRounds = maxRounds,
                Round = 0,
                Phase = ReflectPhaseState.Critique,
            };

            var runtimeState = WorkflowExecutionStateAccess.Load<ReflectModuleState>(ctx, ModuleStateKey);
            await SendCritiqueAsync(runtimeState, state, request.Input ?? "", ctx, ct);
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

        var runtimeStateForCompletion = WorkflowExecutionStateAccess.Load<ReflectModuleState>(ctx, ModuleStateKey);
        if (!runtimeStateForCompletion.PendingBySessionId.Remove(sid, out var state2))
            return;
        await SaveStateAsync(runtimeStateForCompletion, ctx, ct);
        content ??= "";

        if (state2.Phase == ReflectPhaseState.Critique)
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = state2.Round + 1;

            ctx.Logger.LogInformation("Reflect {StepId}: round={Round}/{Max} critique={Verdict}",
                state2.StepId, round, state2.MaxRounds, passed ? "PASS" : "NEEDS_IMPROVEMENT");

            if (passed || round >= state2.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = state2.StepId, RunId = state2.RunId, Success = true, Output = state2.CurrentDraft,
                };
                completed.Annotations["reflect.rounds"] = round.ToString();
                completed.Annotations["reflect.passed"] = passed.ToString();
                await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
                return;
            }

            var next = state2.Clone();
            next.Round = round;
            next.Phase = ReflectPhaseState.Improve;
            await SendImproveAsync(runtimeStateForCompletion, next, content, ctx, ct);
        }
        else
        {
            var next = state2.Clone();
            next.CurrentDraft = content;
            next.Phase = ReflectPhaseState.Critique;
            await SendCritiqueAsync(runtimeStateForCompletion, next, content, ctx, ct);
        }
    }

    private async Task SendCritiqueAsync(
        ReflectModuleState runtimeState,
        ReflectState state,
        string draft,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = $"""
            Review the following content against these criteria: {state.Criteria}
            If the content meets the criteria, respond with exactly "PASS".
            Otherwise, explain what needs improvement.

            Content:
            {draft}
            """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_critique");
        runtimeState.PendingBySessionId[sessionId] = state;
        await SaveStateAsync(runtimeState, ctx, ct);

        if (!string.IsNullOrEmpty(state.TargetRole))
        {
            var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, state.TargetRole);
            await ctx.SendToAsync(targetActorId, new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, ct);
        }
        else
            await ctx.PublishAsync(new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, TopologyAudience.Self, ct);
    }

    private async Task SendImproveAsync(
        ReflectModuleState runtimeState,
        ReflectState state,
        string critique,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = $"""
            Improve the following content based on this feedback.

            Feedback:
            {critique}

            Original content:
            {state.CurrentDraft}
            """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_improve");
        runtimeState.PendingBySessionId[sessionId] = state;
        await SaveStateAsync(runtimeState, ctx, ct);

        if (!string.IsNullOrEmpty(state.TargetRole))
        {
            var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(ctx.AgentId, state.TargetRole);
            await ctx.SendToAsync(targetActorId, new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, ct);
        }
        else
            await ctx.PublishAsync(new ChatRequestEvent { Prompt = prompt, SessionId = sessionId }, TopologyAudience.Self, ct);
    }

    private static Task SaveStateAsync(
        ReflectModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingBySessionId.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }
}
