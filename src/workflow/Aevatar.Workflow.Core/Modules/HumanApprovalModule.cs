// ─────────────────────────────────────────────────────────────
// HumanApprovalModule — 人工审批模块
// 暂停工作流执行，等待人工审批后继续或终止
// Inspired by MAF's Confirmation / RequestExternalInput actions
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Human approval module. Handles step_type == "human_approval".
/// Suspends workflow and waits for a WorkflowResumedEvent.
/// </summary>
public sealed class HumanApprovalModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "human_approval";

    public string Name => "human_approval";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowResumedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        // ─── Handle StepRequestEvent: suspend workflow ───
        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "human_approval") return;
            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);

            var prompt = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "Approve this step?",
                "prompt",
                "message");
            var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
                request.Parameters,
                defaultSeconds: 3600);
            var deliveryTargetId = WorkflowSuspensionRequestSupport.ResolveDeliveryTargetId(request);

            var state = WorkflowExecutionStateAccess.Load<HumanApprovalModuleState>(ctx, ModuleStateKey);
            state.Pending[BuildPendingKey(runId, request.StepId)] = new PendingApprovalState
            {
                StepId = request.StepId,
                RunId = runId,
                Input = request.Input ?? string.Empty,
                OnReject = request.Parameters.GetValueOrDefault("on_reject", "fail"),
                DeliveryTargetId = deliveryTargetId ?? string.Empty,
            };
            await SaveStateAsync(state, ctx, ct);

            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} suspended, prompt=\"{Prompt}\", timeout={Timeout}s",
                runId, request.StepId, prompt, timeoutSeconds);

            var suspended = new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = "human_approval",
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
            };
            WorkflowSuspensionRequestSupport.ApplyContent(suspended, request.Input);
            WorkflowSuspensionRequestSupport.ApplyDeliveryTarget(suspended, request);

            await ctx.PublishAsync(suspended, TopologyAudience.ParentAndChildren, ct);
            return;
        }

        // ─── Handle WorkflowResumedEvent: resume or reject ───
        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            var resumed = payload.Unpack<WorkflowResumedEvent>();
            var state = WorkflowExecutionStateAccess.Load<HumanApprovalModuleState>(ctx, ModuleStateKey);
            if (!TryResolvePending(state, resumed, out var pendingKey, out var pending))
                return;

            var onReject = pending.OnReject;

            if (resumed.Approved)
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} approved",
                    pending.RunId,
                    pending.StepId);
                var approvedContent = ResolveApprovedContent(resumed) ?? pending.Input;
                var approved = new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = true,
                    Output = approvedContent,
                    BranchKey = "true",
                };
                await ctx.PublishAsync(approved, TopologyAudience.Self, ct);
                await PublishResolutionAsync(
                    ctx,
                    pending,
                    approved: true,
                    userInput: resumed.UserInput,
                    editedContent: approvedContent,
                    feedback: ResolveApprovalFeedback(resumed),
                    resolvedContent: approved.Output,
                    ct);
                state.Pending.Remove(pendingKey);
                await SaveStateAsync(state, ctx, ct);
            }
            else
            {
                ctx.Logger.LogInformation(
                    "HumanApproval: run={RunId} step={StepId} rejected, on_reject={OnReject}",
                    pending.RunId,
                    pending.StepId,
                    onReject);

                var feedback = ResolveFeedback(resumed);
                var rejectionOutput = !string.IsNullOrEmpty(feedback)
                    ? $"[Previous content]\n{pending.Input}\n\n[User feedback]\n{feedback}"
                    : pending.Input;

                var rejected = new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = pending.RunId,
                    Success = onReject != "fail",
                    Output = rejectionOutput,
                    Error = onReject == "fail" ? "Human approval rejected" : "",
                    BranchKey = "false",
                };
                await ctx.PublishAsync(rejected, TopologyAudience.Self, ct);
                await PublishResolutionAsync(
                    ctx,
                    pending,
                    approved: false,
                    userInput: resumed.UserInput,
                    editedContent: ResolveEditedContent(resumed),
                    feedback: feedback,
                    resolvedContent: rejected.Output,
                    ct);
                state.Pending.Remove(pendingKey);
                await SaveStateAsync(state, ctx, ct);
            }
        }
    }

    private static Task PublishResolutionAsync(
        IWorkflowExecutionContext ctx,
        PendingApprovalState pending,
        bool approved,
        string? userInput,
        string? editedContent,
        string? feedback,
        string? resolvedContent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pending.DeliveryTargetId))
            return Task.CompletedTask;

        return ctx.PublishAsync(
            new WorkflowHumanApprovalResolvedEvent
            {
                RunId = pending.RunId,
                StepId = pending.StepId,
                Approved = approved,
                UserInput = userInput ?? string.Empty,
                DeliveryTargetId = pending.DeliveryTargetId,
                ResolvedContent = resolvedContent ?? string.Empty,
                EditedContent = editedContent ?? string.Empty,
                Feedback = feedback ?? string.Empty,
            },
            TopologyAudience.Self,
            ct);
    }

    private static string? ResolveApprovedContent(WorkflowResumedEvent resumed)
    {
        var editedContent = NormalizeOptional(resumed.EditedContent);
        if (editedContent is not null)
            return editedContent;

        return NormalizeOptional(resumed.UserInput);
    }

    private static string? ResolveEditedContent(WorkflowResumedEvent resumed) =>
        NormalizeOptional(resumed.EditedContent);

    private static string? ResolveApprovalFeedback(WorkflowResumedEvent resumed) =>
        NormalizeOptional(resumed.Feedback);

    private static string? ResolveFeedback(WorkflowResumedEvent resumed)
    {
        var feedback = NormalizeOptional(resumed.Feedback);
        if (feedback is not null)
            return feedback;

        feedback = NormalizeOptional(resumed.UserInput);
        if (feedback is not null)
            return feedback;

        return NormalizeOptional(resumed.EditedContent);
    }

    private bool TryResolvePending(
        HumanApprovalModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingApprovalState pending)
    {
        pendingKey = string.Empty;
        pending = new PendingApprovalState();
        if (string.IsNullOrWhiteSpace(resumed.RunId))
            return false;

        pendingKey = BuildPendingKey(
            WorkflowRunIdNormalizer.Normalize(resumed.RunId),
            resumed.StepId ?? string.Empty);
        if (!state.Pending.TryGetValue(pendingKey, out var resolvedPending))
            return false;

        pending = resolvedPending;
        return string.Equals(
            pending.RunId,
            WorkflowRunIdNormalizer.Normalize(resumed.RunId),
            StringComparison.Ordinal);
    }

    private static string BuildPendingKey(string runId, string stepId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId}";

    private static Task SaveStateAsync(
        HumanApprovalModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

}
