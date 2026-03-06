using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Self-reflection loop: draft → critique → improve → critique → ...
/// Repeats until critique says "PASS" or max rounds reached.
/// </summary>
public sealed class ReflectModule : IEventModule
{
    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;
    private readonly Dictionary<string, ReflectState> _pendingLLM = [];

    public ReflectModule(WorkflowStepTargetAgentResolver? targetAgentResolver = null)
    {
        _targetAgentResolver = targetAgentResolver;
    }

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
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var chatMetadataParameters = request.Parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

            WorkflowStepTargetAgentResolution target;
            try
            {
                target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(request.StepId, runId, $"reflect target resolution failed: {ex.Message}", ctx, ct);
                return;
            }

            var state = new ReflectState(
                request.StepId,
                runId,
                target.UseSelf ? string.Empty : target.ActorId,
                request.Input ?? "",
                criteria,
                maxRounds,
                chatMetadataParameters,
                0,
                ReflectPhase.Critique);

            try
            {
                await SendCritiqueAsync(state, request.Input ?? "", ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(request.StepId, runId, $"reflect dispatch failed: {ex.Message}", ctx, ct);
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
                var completed = new StepCompletedEvent
                {
                    StepId = state2.StepId, RunId = state2.RunId, Success = true, Output = state2.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString();
                completed.Metadata["reflect.passed"] = passed.ToString();
                await ctx.PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var next = state2 with { Round = round, Phase = ReflectPhase.Improve };
            try
            {
                await SendImproveAsync(next, content, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(state2.StepId, state2.RunId, $"reflect improve dispatch failed: {ex.Message}", ctx, ct);
            }
        }
        else
        {
            var next = state2 with { CurrentDraft = content, Phase = ReflectPhase.Critique };
            try
            {
                await SendCritiqueAsync(next, content, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(state2.StepId, state2.RunId, $"reflect critique dispatch failed: {ex.Message}", ctx, ct);
            }
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

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_critique");
        _pendingLLM[sessionId] = state;

        var chatRequest = new ChatRequestEvent { Prompt = prompt, SessionId = sessionId };
        CopyParametersToChatMetadata(state.ChatMetadataParameters, chatRequest.Metadata);

        if (!string.IsNullOrEmpty(state.TargetActorId))
        {
            await ctx.SendToAsync(state.TargetActorId, chatRequest, ct);
        }
        else
            await ctx.PublishAsync(chatRequest, EventDirection.Self, ct);
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

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_improve");
        _pendingLLM[sessionId] = state;

        var chatRequest = new ChatRequestEvent { Prompt = prompt, SessionId = sessionId };
        CopyParametersToChatMetadata(state.ChatMetadataParameters, chatRequest.Metadata);

        if (!string.IsNullOrEmpty(state.TargetActorId))
        {
            await ctx.SendToAsync(state.TargetActorId, chatRequest, ct);
        }
        else
            await ctx.PublishAsync(chatRequest, EventDirection.Self, ct);
    }

    private static Task PublishFailedCompletionAsync(
        string stepId,
        string runId,
        string error,
        IEventHandlerContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = error,
                WorkerId = ctx.AgentId,
            },
            EventDirection.Self,
            ct);

    private enum ReflectPhase { Critique, Improve }

    private static void CopyParametersToChatMetadata(
        IReadOnlyDictionary<string, string> parameters,
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
            $"{nameof(WorkflowStepTargetAgentResolver)} is not registered in DI and was not provided to {nameof(ReflectModule)}.");
    }

    private sealed record ReflectState(
        string StepId, string RunId, string TargetActorId, string CurrentDraft,
        string Criteria, int MaxRounds, IReadOnlyDictionary<string, string> ChatMetadataParameters, int Round, ReflectPhase Phase);
}
