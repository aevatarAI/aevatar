// ─────────────────────────────────────────────────────────────
// HumanApprovalModule — 人工审批模块
// 暂停工作流执行，等待人工审批后继续或终止
// Inspired by MAF's Confirmation / RequestExternalInput actions
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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
    private const int MaxApprovalTimeoutSeconds = 5_400;

    public string Name => "human_approval";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowResumedEvent.Descriptor) ||
                payload.Is(WorkflowHumanApprovalTimeoutFiredEvent.Descriptor));
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
            if (StepPresentation.HasInteractionTemplateSpec(request.StepParameters?.InteractionTemplateSpec))
            {
                await PublishUnsupportedInteractionTemplateAsync(request, runId, ctx, ct);
                return;
            }

            var prompt = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "Approve this step?",
                "prompt",
                "message");
            var timeoutSeconds = Math.Clamp(
                WorkflowParameterValueParser.ResolveTimeoutSeconds(request.Parameters, defaultSeconds: 3600),
                0,
                MaxApprovalTimeoutSeconds);
            var deliveryTargetId = request.StepParameters?.DeliveryTargetId?.Trim();
            var timeoutDecision = ResolveTimeoutDefaultDecision(request.StepParameters?.HumanApproval);

            var state = WorkflowExecutionStateAccess.Load<HumanApprovalModuleState>(ctx, ModuleStateKey);
            var pendingKey = BuildPendingKey(runId, request.StepId);
            await CancelPendingAsync(state, pendingKey, ctx, CancellationToken.None);

            var pending = new PendingApprovalState
            {
                StepId = request.StepId,
                RunId = runId,
                Input = string.IsNullOrWhiteSpace(request.InputValueId)
                    ? request.Input ?? string.Empty
                    : string.Empty,
                InputValueId = request.InputValueId,
                ExecutionId = request.ExecutionId,
                OnReject = request.Parameters.GetValueOrDefault("on_reject", "fail"),
                DeliveryTargetId = deliveryTargetId ?? string.Empty,
                TimeoutDefaultDecision = timeoutDecision,
                TimeoutSeconds = timeoutSeconds,
                TimeoutCallbackId = timeoutSeconds > 0
                    ? BuildTimeoutCallbackId(runId, request.StepId, ResolveOriginEnvelopeId(envelope))
                    : string.Empty,
            };
            state.Pending[pendingKey] = pending;
            await SaveStateAsync(state, ctx, ct);

            if (timeoutSeconds > 0)
            {
                var timeoutEvent = new WorkflowHumanApprovalTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = request.StepId,
                    TimeoutSeconds = timeoutSeconds,
                };
                var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                    pending.TimeoutCallbackId,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    timeoutEvent,
                    ct: ct);
                pending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
                state.Pending[pendingKey] = pending;
                await SaveStateAsync(state, ctx, ct);
            }

            // Refactor (iter85/cluster-085-workflow-raw-content-information-logs):
            //   Old pattern: Information log included raw value/prompt/input preview
            //   New principle: only stable id + length + status + redaction marker
            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} status=suspended prompt_len={PromptLen} prompt_redacted=true timeout={Timeout}s deliveryTargetId={DeliveryTargetId} hasDeliveryTargetId={HasDeliveryTargetId}",
                runId,
                request.StepId,
                prompt.Length,
                timeoutSeconds,
                deliveryTargetId ?? string.Empty,
                !string.IsNullOrWhiteSpace(deliveryTargetId));

            var suspended = new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = "human_approval",
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
            };
            WorkflowSuspensionRequestSupport.ApplyContent(suspended, request.Input);
            ApplyTypedInteraction(suspended, request);
            ApplyTypedDeliveryTarget(suspended, deliveryTargetId);

            await ctx.PublishAsync(suspended, TopologyAudience.Self, ct);
            return;
        }

        // ─── Handle WorkflowResumedEvent: resume or reject ───
        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            var resumed = payload.Unpack<WorkflowResumedEvent>();
            var state = WorkflowExecutionStateAccess.Load<HumanApprovalModuleState>(ctx, ModuleStateKey);
            if (!TryResolvePending(state, resumed, out var pendingKey, out var pending))
                return;

            await CompleteApprovalAsync(
                ctx,
                state,
                pendingKey,
                pending,
                resumed.Approved,
                userInput: resumed.UserInput,
                approvedContent: ResolveApprovedContent(resumed),
                editedContent: ResolveEditedContent(resumed),
                feedback: resumed.Approved ? ResolveApprovalFeedback(resumed) : ResolveFeedback(resumed),
                resolutionSource: WorkflowHumanApprovalResolutionSource.User,
                ct);
            return;
        }

        if (payload.Is(WorkflowHumanApprovalTimeoutFiredEvent.Descriptor))
        {
            var timeout = payload.Unpack<WorkflowHumanApprovalTimeoutFiredEvent>();
            var runId = WorkflowRunIdNormalizer.Normalize(timeout.RunId);
            var pendingKey = BuildPendingKey(runId, timeout.StepId);
            var state = WorkflowExecutionStateAccess.Load<HumanApprovalModuleState>(ctx, ModuleStateKey);
            if (!state.Pending.TryGetValue(pendingKey, out var pending))
                return;

            if (!MatchesTimeout(envelope, pending))
            {
                ctx.Logger.LogDebug(
                    "HumanApproval: ignore stale timeout run={RunId} step={StepId}",
                    runId,
                    timeout.StepId);
                return;
            }

            var approved = pending.TimeoutDefaultDecision == WorkflowHumanApprovalTimeoutDefaultDecision.Approve;
            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} timed out decision={Decision}",
                pending.RunId,
                pending.StepId,
                approved ? "approve" : "reject");

            await CompleteApprovalAsync(
                ctx,
                state,
                pendingKey,
                pending,
                approved,
                userInput: string.Empty,
                approvedContent: null,
                editedContent: null,
                feedback: string.Empty,
                resolutionSource: WorkflowHumanApprovalResolutionSource.Timeout,
                ct);
        }
    }

    private static async Task CompleteApprovalAsync(
        IWorkflowExecutionContext ctx,
        HumanApprovalModuleState state,
        string pendingKey,
        PendingApprovalState pending,
        bool approved,
        string? userInput,
        string? approvedContent,
        string? editedContent,
        string? feedback,
        WorkflowHumanApprovalResolutionSource resolutionSource,
        CancellationToken ct)
    {
        if (approved)
        {
            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} approved source={Source}",
                pending.RunId,
                pending.StepId,
                resolutionSource);
            var pendingInput = ResolvePendingInput(pending, ctx);
            var output = approvedContent ?? pendingInput;
            var completed = new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                ExecutionId = pending.ExecutionId,
                Success = true,
                Output = output,
                BranchKey = "true",
                OutputProvenance = approvedContent == null
                    ? WorkflowStepOutputProvenance.ForwardedInput
                    : WorkflowStepOutputProvenance.Produced,
            };
            await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
            await PublishResolutionAsync(
                ctx,
                pending,
                approved: true,
                userInput: userInput,
                editedContent: output,
                feedback: feedback,
                resolvedContent: completed.Output,
                resolutionSource,
                ct);
        }
        else
        {
            var onReject = pending.OnReject;
            ctx.Logger.LogInformation(
                "HumanApproval: run={RunId} step={StepId} rejected, on_reject={OnReject} source={Source}",
                pending.RunId,
                pending.StepId,
                onReject,
                resolutionSource);

            var pendingInput = ResolvePendingInput(pending, ctx);
            var rejectionOutput = !string.IsNullOrEmpty(feedback)
                ? $"[Previous content]\n{pendingInput}\n\n[User feedback]\n{feedback}"
                : pendingInput;

            var completed = new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                ExecutionId = pending.ExecutionId,
                Success = onReject != "fail",
                Output = rejectionOutput,
                Error = onReject == "fail" ? "Human approval rejected" : "",
                BranchKey = "false",
                OutputProvenance = onReject != "fail" && string.IsNullOrEmpty(feedback)
                    ? WorkflowStepOutputProvenance.ForwardedInput
                    : WorkflowStepOutputProvenance.Produced,
            };
            await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
            await PublishResolutionAsync(
                ctx,
                pending,
                approved: false,
                userInput: userInput,
                editedContent: editedContent,
                feedback: feedback,
                resolvedContent: completed.Output,
                resolutionSource,
                ct);
        }

        state.Pending.Remove(pendingKey);
        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            pending.TimeoutLease,
            $"HumanApproval timeout cleanup run={pending.RunId} step={pending.StepId}",
            CancellationToken.None);
    }

    private static Task PublishResolutionAsync(
        IWorkflowExecutionContext ctx,
        PendingApprovalState pending,
        bool approved,
        string? userInput,
        string? editedContent,
        string? feedback,
        string? resolvedContent,
        WorkflowHumanApprovalResolutionSource resolutionSource,
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
                ResolutionSource = resolutionSource,
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

    private static WorkflowHumanApprovalTimeoutDefaultDecision ResolveTimeoutDefaultDecision(
        WorkflowHumanApprovalOptions? options)
    {
        if (options?.TimeoutDefaultDecision == WorkflowHumanApprovalTimeoutDefaultDecision.Approve)
            return WorkflowHumanApprovalTimeoutDefaultDecision.Approve;

        return WorkflowHumanApprovalTimeoutDefaultDecision.Reject;
    }

    private static bool MatchesTimeout(EventEnvelope envelope, PendingApprovalState pending)
    {
        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id;

    private static string BuildTimeoutCallbackId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("human-approval-timeout", runId, stepId, originEnvelopeId);

    private static async Task CancelPendingAsync(
        HumanApprovalModuleState state,
        string pendingKey,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.Pending.Remove(pendingKey, out var existingPending))
            return;

        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            existingPending.TimeoutLease,
            $"HumanApproval replaced pending cleanup run={existingPending.RunId} step={existingPending.StepId}",
            ct);
    }

    private static void ApplyTypedInteraction(
        WorkflowSuspendedEvent suspended,
        StepRequestEvent request)
    {
        var interaction = request.StepParameters?.InteractionSpec;
        if (!StepPresentation.HasInteractionSpec(interaction))
            return;

        suspended.Interaction = interaction!.Clone();
    }

    private static void ApplyTypedDeliveryTarget(
        WorkflowSuspendedEvent suspended,
        string? deliveryTargetId)
    {
        if (!string.IsNullOrWhiteSpace(deliveryTargetId))
            suspended.DeliveryTargetId = deliveryTargetId;
    }

    private static Task PublishUnsupportedInteractionTemplateAsync(
        StepRequestEvent request,
        string runId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "human_approval does not support interaction_template; use interaction_spec.",
                ExecutionId = request.ExecutionId,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            },
            TopologyAudience.Self,
            ct);

    private static string ResolvePendingInput(
        PendingApprovalState pending,
        IWorkflowExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(pending.InputValueId))
            return pending.Input ?? string.Empty;

        var kernelState = WorkflowExecutionStateAccess.Load<WorkflowExecutionKernelState>(
            ctx,
            WorkflowExecutionKernel.ModuleStateKey);
        return WorkflowExecutionValueStore.GetCanonicalValue(kernelState, pending.InputValueId).Value;
    }

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
