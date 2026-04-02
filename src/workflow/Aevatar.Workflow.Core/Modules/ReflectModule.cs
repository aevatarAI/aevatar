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
public sealed class ReflectModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "reflect";
    private readonly WorkflowStepTargetAgentResolver? _targetAgentResolver;

    public ReflectModule(WorkflowStepTargetAgentResolver? targetAgentResolver = null)
    {
        _targetAgentResolver = targetAgentResolver;
    }

    public string Name => "reflect";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(TextMessageEndEvent.Descriptor) ||
                payload.Is(ChatResponseEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "reflect")
                return;

            var maxRounds = int.TryParse(request.Parameters.GetValueOrDefault("max_rounds", "3"), out var parsed)
                ? parsed
                : 3;
            var criteria = request.Parameters.GetValueOrDefault("criteria", "quality and correctness");
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var stepId = request.StepId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stepId))
            {
                await PublishFailedCompletionAsync(stepId, runId, "reflect step requires non-empty step_id", ctx, ct);
                return;
            }

            WorkflowStepTargetAgentResolution target;
            try
            {
                target = await ResolveTargetAgentResolver(ctx).ResolveAsync(request, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(stepId, runId, $"reflect target resolution failed: {ex.Message}", ctx, ct);
                return;
            }

            var reflectState = new ReflectState
            {
                StepId = stepId,
                RunId = runId,
                TargetRole = request.TargetRole ?? string.Empty,
                TargetActorId = target.UseSelf ? string.Empty : target.ActorId,
                CurrentDraft = request.Input ?? string.Empty,
                Criteria = criteria,
                MaxRounds = Math.Clamp(maxRounds, 1, 10),
                Round = 0,
                Phase = ReflectPhaseState.Critique,
            };
            CopyParameters(request.Parameters, reflectState.ChatMetadataParameters);

            var runtimeState = WorkflowExecutionStateAccess.Load<ReflectModuleState>(ctx, ModuleStateKey);
            try
            {
                await SendCritiqueAsync(runtimeState, reflectState, request.Input ?? string.Empty, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(stepId, runId, $"reflect dispatch failed: {ex.Message}", ctx, ct);
            }

            return;
        }

        string? content = null;
        string? sessionId = null;
        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            content = evt.Content;
            sessionId = evt.SessionId;
        }
        else if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            content = evt.Content;
            sessionId = evt.SessionId;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var runtimeStateForCompletion = WorkflowExecutionStateAccess.Load<ReflectModuleState>(ctx, ModuleStateKey);
        if (!runtimeStateForCompletion.PendingBySessionId.TryGetValue(sessionId, out var pendingState))
            return;

        content ??= string.Empty;
        if (pendingState.Phase == ReflectPhaseState.Critique)
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = pendingState.Round + 1;
            ctx.Logger.LogInformation(
                "Reflect {StepId}: round={Round}/{Max} critique={Verdict}",
                pendingState.StepId,
                round,
                pendingState.MaxRounds,
                passed ? "PASS" : "NEEDS_IMPROVEMENT");

            if (passed || round >= pendingState.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = pendingState.StepId,
                    RunId = pendingState.RunId,
                    Success = true,
                    Output = pendingState.CurrentDraft,
                };
                completed.Annotations["reflect.rounds"] = round.ToString();
                completed.Annotations["reflect.passed"] = passed.ToString();
                await ctx.PublishAsync(completed, TopologyAudience.Self, ct);

                runtimeStateForCompletion.PendingBySessionId.Remove(sessionId);
                await SaveStateAsync(runtimeStateForCompletion, ctx, ct);
                return;
            }

            runtimeStateForCompletion.PendingBySessionId.Remove(sessionId);
            await SaveStateAsync(runtimeStateForCompletion, ctx, ct);

            var next = pendingState.Clone();
            next.Round = round;
            next.Phase = ReflectPhaseState.Improve;
            try
            {
                await SendImproveAsync(runtimeStateForCompletion, next, content, ctx, ct);
            }
            catch (Exception ex)
            {
                await PublishFailedCompletionAsync(pendingState.StepId, pendingState.RunId, $"reflect improve dispatch failed: {ex.Message}", ctx, ct);
            }

            return;
        }

        runtimeStateForCompletion.PendingBySessionId.Remove(sessionId);
        await SaveStateAsync(runtimeStateForCompletion, ctx, ct);

        var nextCritique = pendingState.Clone();
        nextCritique.CurrentDraft = content;
        nextCritique.Phase = ReflectPhaseState.Critique;
        try
        {
            await SendCritiqueAsync(runtimeStateForCompletion, nextCritique, content, ctx, ct);
        }
        catch (Exception ex)
        {
            await PublishFailedCompletionAsync(pendingState.StepId, pendingState.RunId, $"reflect critique dispatch failed: {ex.Message}", ctx, ct);
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

        var sessionId = CreatePhaseSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_critique");
        runtimeState.PendingBySessionId[sessionId] = state;
        await SaveStateAsync(runtimeState, ctx, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        CopyParameters(state.ChatMetadataParameters, chatRequest.Headers);

        if (!string.IsNullOrWhiteSpace(state.TargetActorId))
            await ctx.SendToAsync(state.TargetActorId, chatRequest, ct);
        else
            await ctx.PublishAsync(chatRequest, TopologyAudience.Self, ct);
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

        var sessionId = CreatePhaseSessionId(ctx.AgentId, state.RunId, $"{state.StepId}_r{state.Round}_improve");
        runtimeState.PendingBySessionId[sessionId] = state;
        await SaveStateAsync(runtimeState, ctx, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        CopyParameters(state.ChatMetadataParameters, chatRequest.Headers);

        if (!string.IsNullOrWhiteSpace(state.TargetActorId))
            await ctx.SendToAsync(state.TargetActorId, chatRequest, ct);
        else
            await ctx.PublishAsync(chatRequest, TopologyAudience.Self, ct);
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

    private static Task PublishFailedCompletionAsync(
        string stepId,
        string runId,
        string error,
        IEventContext ctx,
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
            TopologyAudience.Self,
            ct);

    private static void CopyParameters(
        MapField<string, string> source,
        MapField<string, string> destination)
    {
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(key, "agent_type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "agent_id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination[key.Trim()] = value.Trim();
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

    private static string CreatePhaseSessionId(string scopeId, string runId, string stepToken) =>
        string.IsNullOrWhiteSpace(runId)
            ? ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, stepToken)
            : ChatSessionKeys.CreateWorkflowStepSessionId(scopeId, runId, stepToken, attempt: 1);
}
