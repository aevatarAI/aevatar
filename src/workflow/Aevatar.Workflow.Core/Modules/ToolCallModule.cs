// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed class ToolCallModule : IEventModule<IWorkflowExecutionContext>
{
    internal const string ModuleStateKey = "tool_call";
    private static readonly TimeSpan PublicationRetryDelay = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan DurablePublicationRetryDelay => PublicationRetryDelay;

    private readonly IEnumerable<IWorkflowToolSource> _toolSources;
    private readonly ILogger<ToolCallModule> _logger;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>? _toolIndex;

    public ToolCallModule(
        IEnumerable<IWorkflowToolSource> toolSources,
        ILogger<ToolCallModule> logger,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _callerAccessTokenProvider = callerAccessTokenProvider;
    }

    public string Name => "tool_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowResumedEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
        {
            await HandlePublicationRetryAsync(
                payload.Unpack<WorkflowToolCallPublicationRetryFiredEvent>(),
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            await HandleResumeAsync(payload.Unpack<WorkflowResumedEvent>(), ctx, ct);
            return;
        }

        var request = payload.Unpack<StepRequestEvent>();
        if (request.StepType != "tool_call") return;

        var toolName = request.Parameters.GetValueOrDefault("tool", "").Trim();
        var callId = ComposeWorkflowToolCallId(request);
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (await TryHandleStepRedeliveryAsync(state, request, callId, ctx, ct))
        {
            return;
        }

        if (string.IsNullOrEmpty(toolName))
        {
            await PersistAndPublishCompletionAsync(state, new WorkflowToolCallCompletionOutboxEntry
            {
                CallId = callId,
                ExecutionId = request.ExecutionId,
                RunId = request.RunId,
                StepId = request.StepId,
                TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                StepCompletion = new StepCompletedEvent
                {
                    StepId = request.StepId,
                    RunId = request.RunId,
                    ExecutionId = request.ExecutionId,
                    Success = false,
                    Error = "tool_call 缺少 tool 参数",
                },
            }, ctx, ct);
            return;
        }

        var argumentsJson = ResolveArgumentsJson(request);
        var issuedAtUnixMs = ResolveIssuedAtUnixMs(envelope);
        ctx.Logger.LogInformation("ToolCall: {StepId} → 工具 {Tool}", request.StepId, toolName);

        var admission = ResolveInvocationAdmission(ctx, request, toolName, out var admissionError);
        if (admissionError != null)
        {
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                request,
                toolName,
                admissionError,
                ct);
            return;
        }

        // 发布 Tool 调用开始事件（供观测/UI）
        await ctx.PublishAsync(new WorkflowToolCallStartedEvent
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            CallId = callId,
            RunId = request.RunId,
            StepId = request.StepId,
        }, TopologyAudience.Self, ct);

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
        {
            const string notFound = "tool not found or no tool sources configured";
            await PersistAndPublishToolFailureAsync(state, ctx, request, toolName, notFound, ct);
            return;
        }

        WorkflowToolExecutionResult result;
        try
        {
            result = await ExecuteToolAsync(
                tool,
                argumentsJson,
                request,
                callId,
                issuedAtUnixMs,
                ctx,
                ct,
                admission: admission);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "ToolCall: step={StepId} tool={Tool} execution failed", request.StepId, toolName);
            result = WorkflowToolExecutionResult.Failed(string.Empty, string.Empty, ex.Message);
        }

        if (result.PendingApproval != null)
        {
            await SuspendForApprovalAsync(
                state,
                ctx,
                request,
                toolName,
                callId,
                issuedAtUnixMs,
                result.PendingApproval,
                ct);
            return;
        }

        await PersistAndPublishToolOutcomeAsync(state, ctx, request, toolName, callId, result, ct);
    }

    internal static IReadOnlyList<WorkflowToolCallPublicationRetryFiredEvent> BuildPendingPublicationRetries(
        ToolCallModuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var retries = new List<WorkflowToolCallPublicationRetryFiredEvent>();
        retries.AddRange(state.Completions.Select(BuildCompletionRetry));
        retries.AddRange(state.PendingApprovals.Values
            .Where(static pending => !pending.SuspensionPublished)
            .Select(BuildSuspensionRetry));
        return retries;
    }

    internal static string BuildPublicationRetryCallbackId(
        WorkflowToolCallPublicationRetryFiredEvent retry) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "workflow-tool-publication-retry",
            retry.PublicationKind.ToString(),
            retry.RunId,
            retry.StepId,
            retry.CallId,
            retry.ExecutionId,
            retry.ApprovalRequestId,
            retry.TerminalDecision.ToString());

    internal static EventEnvelopePublishOptions BuildPublicationRetryOptions(
        WorkflowToolCallPublicationRetryFiredEvent retry) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = BuildPublicationRetryCallbackId(retry),
            },
        };

    private static async Task HandlePublicationRetryAsync(
        WorkflowToolCallPublicationRetryFiredEvent retry,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        try
        {
            if (retry.PublicationKind == WorkflowToolCallPublicationKind.Completion)
            {
                var completion = FindCompletion(
                    state,
                    retry.RunId,
                    retry.StepId,
                    retry.CallId,
                    retry.ExecutionId,
                    retry.ApprovalRequestId,
                    retry.TerminalDecision);
                if (completion != null)
                    await PublishUnpublishedCompletionEventsAsync(state, completion, ctx, ct);
                return;
            }

            if (retry.PublicationKind != WorkflowToolCallPublicationKind.Suspension)
                return;

            var pending = state.PendingApprovals.Values.FirstOrDefault(candidate =>
                MatchesCallIdentity(
                    candidate.RunId,
                    candidate.StepId,
                    candidate.ToolCallId,
                    candidate.ExecutionId,
                    retry.RunId,
                    retry.StepId,
                    retry.CallId,
                    retry.ExecutionId) &&
                string.Equals(
                    candidate.ApprovalRequestId,
                    NormalizeRequired(retry.ApprovalRequestId),
                    StringComparison.Ordinal));
            if (pending != null)
                await PublishPendingSuspensionAsync(state, pending, ctx, ct);
        }
        catch (WorkflowDurablePublicationPendingException ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ToolCall: durable publication retry remains pending run={RunId} step={StepId} kind={PublicationKind}",
                retry.RunId,
                retry.StepId,
                retry.PublicationKind);
            await TrySchedulePublicationRecoveryAsync(ctx, retry, ct, allowImmediateContinuation: false);
        }
    }

    /// <summary>
    /// Resolves the committed proof for this call site from actor-owned Run state. A step that the
    /// compiler classified as an external invocation must not dispatch without exactly one proof.
    /// </summary>
    private static WorkflowCapabilityInvocationAdmission? ResolveInvocationAdmission(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        out string? error)
    {
        error = null;
        var invocation = request.ExternalInvocation;
        if (invocation is null)
        {
            if (WorkflowAuthorizationDependencyEvaluator.RequiresExternalCapabilityAdmission(toolName))
            {
                error = "EXTERNAL_CAPABILITY_CALL_SITE_NOT_ADMITTED: " +
                        "this call site has no compiled external capability invocation";
            }

            return null;
        }

        if (!string.Equals(invocation.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            error = "EXTERNAL_CAPABILITY_TOOL_MISMATCH: " +
                    "the dispatched tool does not match the admitted call-site tool";
            return null;
        }

        var lookup = WorkflowCapabilityAdmissionRuntimeAccess.Resolve(ctx, invocation);
        if (!lookup.IsResolved)
        {
            error = $"{lookup.FailureCode}: {lookup.FailureMessage}";
            return null;
        }

        return lookup.Admission;
    }

    private async Task<WorkflowToolExecutionResult> ExecuteToolAsync(
        IWorkflowTool tool,
        string argumentsJson,
        StepRequestEvent request,
        string callId,
        long issuedAtUnixMs,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        ToolApprovalGrant? approvalGrant = null,
        WorkflowCapabilityInvocationAdmission? admission = null)
    {
        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
        var callerCredential = credential.Found
            ? await WorkflowCallerAccessTokenResolver.ResolveAsync(
                credential.Credential,
                _callerAccessTokenProvider,
                ct)
            : new WorkflowCallerCredential();
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            request.RunId ?? string.Empty,
            request.StepId ?? string.Empty);
        return await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: argumentsJson,
                RunId: request.RunId ?? string.Empty,
                StepId: request.StepId ?? string.Empty,
                ExecutionId: request.ExecutionId ?? string.Empty,
                CallId: callId,
                ScopeId: ctx.ScopeId ?? string.Empty,
                CallerCredential: callerCredential,
                RuntimeContext: runtimeContext,
                ApprovalGrant: approvalGrant,
                InputFileRefs: request.InputFileRefs,
                IdempotencyKey: request.IdempotencyKey ?? string.Empty,
                ScheduleId: ctx.ScheduleId ?? string.Empty,
                InvocationAdmission: admission,
                LlmControl: GetLlmControl(ctx),
                IssuedAtUnixMs: issuedAtUnixMs),
            ct);
    }

    private static WorkflowLlmControlContext? GetLlmControl(IWorkflowExecutionContext ctx)
    {
        var hasLlm = WorkflowRunExecutionContextStateAccess.TryGetLlm(ctx, out var llm);
        var senderToken = ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor
            ? Normalize(runtimeAccessor.RuntimeContext.SenderNyxIdAccessToken)
            : null;
        if (!hasLlm && senderToken is null)
            return null;

        var control = new WorkflowLlmControlContext
        {
            ModelOverride = hasLlm ? Normalize(llm.ModelOverride) ?? string.Empty : string.Empty,
            RoutePreference = hasLlm ? Normalize(llm.RoutePreference) ?? string.Empty : string.Empty,
            UserMemoryPrompt = hasLlm ? Normalize(llm.UserMemoryPrompt) ?? string.Empty : string.Empty,
            SenderNyxIdAccessToken = senderToken ?? string.Empty,
        };
        if (hasLlm && llm.HasMaxToolRoundsOverride)
            control.MaxToolRoundsOverride = llm.MaxToolRoundsOverride;
        return control;
    }

    private async Task HandleResumeAsync(
        WorkflowResumedEvent resumed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (resumed.ToolApproval == null)
            return;

        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (await TryHandleResumeRedeliveryAsync(state, resumed, ctx, ct))
        {
            return;
        }

        if (!TryResolvePending(state, resumed, out var pendingKey, out var pending))
        {
            await PublishResumeRejectedAsync(state, resumed, ctx, ct);
            return;
        }

        if (!resumed.Approved)
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                ToStepRequest(pending),
                pending.ToolName,
                BuildRejectedApprovalError(resumed),
                "approval_denied",
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Denied,
                ct);
            return;
        }

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var tool))
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                ToStepRequest(pending),
                pending.ToolName,
                "tool not found or no tool sources configured",
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Approved,
                ct);
            return;
        }

        var resumedRequest = ToStepRequest(pending);
        var admission = ResolveInvocationAdmission(ctx, resumedRequest, pending.ToolName, out var admissionError);
        if (admissionError != null)
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                resumedRequest,
                pending.ToolName,
                admissionError,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Approved,
                ct);
            return;
        }

        WorkflowToolExecutionResult result;
        try
        {
            result = await ExecuteToolAsync(
                tool,
                pending.ArgumentsJson,
                resumedRequest,
                pending.ToolCallId,
                pending.IssuedAtUnixMs,
                ctx,
                ct,
                new ToolApprovalGrant(
                    pending.ApprovalRequestId,
                    pending.ToolName,
                    pending.ToolCallId),
                admission);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ToolCall: step={StepId} tool={Tool} approved replay failed",
                pending.StepId,
                pending.ToolName);
            result = WorkflowToolExecutionResult.Failed(string.Empty, string.Empty, ex.Message);
        }

        if (result.Failure is { TerminalInvoked: false, Retryable: true } retryableFailure)
        {
            ctx.Logger.LogWarning(
                "ToolCall: step={StepId} tool={Tool} approved replay remains pending after retryable pre-terminal failure code={FailureCode}",
                pending.StepId,
                pending.ToolName,
                retryableFailure.ErrorCode);
            throw new InvalidOperationException(retryableFailure.ErrorMessage);
        }

        if (result.PendingApproval != null)
        {
            state.PendingApprovals.Remove(pendingKey);
            await SuspendForApprovalAsync(
                state,
                ctx,
                resumedRequest,
                pending.ToolName,
                pending.ToolCallId,
                pending.IssuedAtUnixMs,
                result.PendingApproval,
                ct);
            return;
        }

        state.PendingApprovals.Remove(pendingKey);
        await PersistAndPublishToolOutcomeAsync(
            state,
            ctx,
            resumedRequest,
            pending.ToolName,
            pending.ToolCallId,
            result,
            pending.ApprovalRequestId,
            WorkflowToolCallTerminalDecision.Approved,
            ct);
    }

    private static bool TryResolvePending(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingToolCallApprovalState pending)
    {
        pendingKey = BuildPendingKey(
            resumed.RunId,
            resumed.StepId,
            resumed.ToolApproval?.ExecutionId,
            resumed.ToolApproval?.ToolCallId,
            resumed.ToolApproval?.ApprovalRequestId);
        pending = new PendingToolCallApprovalState();
        if (string.IsNullOrWhiteSpace(resumed.RunId) ||
            string.IsNullOrWhiteSpace(resumed.StepId) ||
            resumed.ToolApproval == null ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ExecutionId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ToolCallId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ApprovalRequestId))
        {
            return false;
        }

        if (!state.PendingApprovals.TryGetValue(pendingKey, out var resolvedPending))
            return false;

        pending = resolvedPending;
        return string.Equals(pending.RunId, NormalizeRequired(resumed.RunId), StringComparison.Ordinal) &&
               string.Equals(pending.StepId, NormalizeRequired(resumed.StepId), StringComparison.Ordinal) &&
               string.Equals(pending.ExecutionId, NormalizeRequired(resumed.ToolApproval.ExecutionId), StringComparison.Ordinal) &&
               string.Equals(pending.ToolCallId, NormalizeRequired(resumed.ToolApproval.ToolCallId), StringComparison.Ordinal) &&
               string.Equals(pending.ApprovalRequestId, NormalizeRequired(resumed.ToolApproval.ApprovalRequestId), StringComparison.Ordinal);
    }

    private static Task PublishResumeRejectedAsync(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var reason = ResolveResumeRejectionReason(state, resumed);
        ctx.Logger.LogWarning(
            "ToolCall: reject tool approval resume run={RunId} step={StepId} reason={Reason}",
            resumed.RunId,
            resumed.StepId,
            reason);
        return ctx.PublishAsync(new WorkflowToolApprovalResumeRejectedEvent
        {
            RunId = resumed.RunId ?? string.Empty,
            StepId = resumed.StepId ?? string.Empty,
            SubmittedApproval = resumed.ToolApproval.Clone(),
            Reason = reason,
        }, TopologyAudience.Self, ct);
    }

    private static WorkflowToolApprovalResumeRejectionReason ResolveResumeRejectionReason(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed)
    {
        if (string.IsNullOrWhiteSpace(resumed.RunId) ||
            string.IsNullOrWhiteSpace(resumed.StepId) ||
            resumed.ToolApproval == null ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ExecutionId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ToolCallId) ||
            string.IsNullOrWhiteSpace(resumed.ToolApproval.ApprovalRequestId))
        {
            return WorkflowToolApprovalResumeRejectionReason.InvalidIdentity;
        }

        var hasPendingForStep = state.PendingApprovals.Values.Any(pending =>
            string.Equals(pending.RunId, resumed.RunId.Trim(), StringComparison.Ordinal) &&
            string.Equals(pending.StepId, resumed.StepId.Trim(), StringComparison.Ordinal));
        return hasPendingForStep
            ? WorkflowToolApprovalResumeRejectionReason.IdentityMismatch
            : WorkflowToolApprovalResumeRejectionReason.PendingApprovalNotFound;
    }

    private static async Task SuspendForApprovalAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string callId,
        long issuedAtUnixMs,
        WorkflowToolApprovalPendingOutcome pending,
        CancellationToken ct)
    {
        var pendingState = new PendingToolCallApprovalState
        {
            RunId = NormalizeRequired(request.RunId),
            StepId = NormalizeRequired(request.StepId),
            ExecutionId = NormalizeRequired(request.ExecutionId),
            ToolName = NormalizeRequired(toolName),
            ToolCallId = NormalizeRequired(callId),
            ApprovalRequestId = NormalizeRequired(pending.ApprovalRequestId),
            ArgumentsJson = pending.ArgumentsJson ?? string.Empty,
            IdempotencyKey = request.IdempotencyKey ?? string.Empty,
            ExternalInvocation = request.ExternalInvocation?.Clone(),
            IssuedAtUnixMs = issuedAtUnixMs,
        };
        pendingState.InputFileRefs.Add(request.InputFileRefs.Select(static fileRef => fileRef.Clone()));
        pendingState.Suspension = BuildSuspension(pendingState);
        state.PendingApprovals[BuildPendingKey(pendingState)] = pendingState;
        await SaveStateAsync(state, ctx, ct);
        await TrySchedulePublicationRecoveryAsync(
            ctx,
            BuildSuspensionRetry(pendingState),
            ct);
        await PublishPendingSuspensionAsync(state, pendingState, ctx, ct);
    }

    private static Task PersistAndPublishToolOutcomeAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string callId,
        WorkflowToolExecutionResult result,
        CancellationToken ct) =>
        PersistAndPublishToolOutcomeAsync(
            state,
            ctx,
            request,
            toolName,
            callId,
            result,
            string.Empty,
            WorkflowToolCallTerminalDecision.NoApproval,
            ct);

    private static Task PersistAndPublishToolOutcomeAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string callId,
        WorkflowToolExecutionResult result,
        string approvalRequestId,
        WorkflowToolCallTerminalDecision terminalDecision,
        CancellationToken ct)
    {
        if (result.Failure != null)
        {
            return PersistAndPublishToolFailureAsync(
                state,
                ctx,
                request,
                toolName,
                result.Failure.ErrorMessage,
                result.Failure.ErrorCode,
                result.ResultJson,
                callId,
                approvalRequestId,
                terminalDecision,
                ct);
        }

        var toolCompletion = new WorkflowToolCallCompletedEvent
        {
            CallId = callId,
            Success = true,
            ResultJson = result.ResultJson,
            RunId = request.RunId,
            StepId = request.StepId,
        };
        if (result.ManagedHandoff != null)
            toolCompletion.ManagedHandoff = result.ManagedHandoff.Clone();

        var entry = new WorkflowToolCallCompletionOutboxEntry
        {
            CallId = callId,
            ExecutionId = request.ExecutionId,
            RunId = request.RunId,
            StepId = request.StepId,
            ApprovalRequestId = approvalRequestId,
            TerminalDecision = terminalDecision,
            ToolCompletion = toolCompletion,
        };
        if (result.ManagedHandoff == null)
        {
            entry.StepCompletion = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                ExecutionId = request.ExecutionId,
                Success = true,
                Output = result.ResultJson,
            };
        }

        return PersistAndPublishCompletionAsync(state, entry, ctx, ct);
    }

    private static async Task<bool> TryHandleStepRedeliveryAsync(
        ToolCallModuleState state,
        StepRequestEvent request,
        string callId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var cached = FindCompletion(
            state,
            request.RunId,
            request.StepId,
            callId,
            request.ExecutionId);
        if (cached != null)
        {
            await TrySchedulePublicationRecoveryAsync(ctx, BuildCompletionRetry(cached), ct);
            await PublishUnpublishedCompletionEventsAsync(state, cached, ctx, ct);
            return true;
        }

        if (FindCompletionTombstone(
                state,
                request.RunId,
                request.StepId,
                callId,
                request.ExecutionId) != null)
        {
            return true;
        }

        var pending = state.PendingApprovals.Values.FirstOrDefault(candidate =>
            MatchesCallIdentity(
                candidate.RunId,
                candidate.StepId,
                candidate.ToolCallId,
                candidate.ExecutionId,
                request.RunId,
                request.StepId,
                callId,
                request.ExecutionId));
        if (pending == null)
            return false;

        await TrySchedulePublicationRecoveryAsync(ctx, BuildSuspensionRetry(pending), ct);
        await PublishPendingSuspensionAsync(state, pending, ctx, ct);
        return true;
    }

    private static async Task<bool> TryHandleResumeRedeliveryAsync(
        ToolCallModuleState state,
        WorkflowResumedEvent resumed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!HasCompleteResumeIdentity(resumed))
            return false;

        var decision = resumed.Approved
            ? WorkflowToolCallTerminalDecision.Approved
            : WorkflowToolCallTerminalDecision.Denied;
        var cached = FindCompletion(
            state,
            resumed.RunId,
            resumed.StepId,
            resumed.ToolApproval.ToolCallId,
            resumed.ToolApproval.ExecutionId,
            resumed.ToolApproval.ApprovalRequestId,
            decision);
        if (cached != null)
        {
            await TrySchedulePublicationRecoveryAsync(ctx, BuildCompletionRetry(cached), ct);
            await PublishUnpublishedCompletionEventsAsync(state, cached, ctx, ct);
            return true;
        }

        return FindCompletionTombstone(
                   state,
                   resumed.RunId,
                   resumed.StepId,
                   resumed.ToolApproval.ToolCallId,
                   resumed.ToolApproval.ExecutionId,
                   resumed.ToolApproval.ApprovalRequestId,
                   decision) != null;
    }

    private static WorkflowToolCallCompletionOutboxEntry? FindCompletion(
        ToolCallModuleState state,
        string? runId,
        string? stepId,
        string? callId,
        string? executionId,
        string? approvalRequestId = null,
        WorkflowToolCallTerminalDecision? terminalDecision = null) =>
        state.Completions.FirstOrDefault(completion =>
            MatchesCallIdentity(
                completion.RunId,
                completion.StepId,
                completion.CallId,
                completion.ExecutionId,
                runId,
                stepId,
                callId,
                executionId) &&
            (approvalRequestId == null ||
             string.Equals(completion.ApprovalRequestId, NormalizeRequired(approvalRequestId), StringComparison.Ordinal)) &&
            (terminalDecision == null || completion.TerminalDecision == terminalDecision));

    private static WorkflowToolCallCompletionTombstone? FindCompletionTombstone(
        ToolCallModuleState state,
        string? runId,
        string? stepId,
        string? callId,
        string? executionId,
        string? approvalRequestId = null,
        WorkflowToolCallTerminalDecision? terminalDecision = null)
    {
        if (!state.CompletionTombstones.TryGetValue(BuildCompletionKey(callId, executionId), out var tombstone) ||
            !MatchesCallIdentity(
                tombstone.RunId,
                tombstone.StepId,
                tombstone.CallId,
                tombstone.ExecutionId,
                runId,
                stepId,
                callId,
                executionId) ||
            (approvalRequestId != null &&
             !string.Equals(tombstone.ApprovalRequestId, NormalizeRequired(approvalRequestId), StringComparison.Ordinal)) ||
            (terminalDecision != null && tombstone.TerminalDecision != terminalDecision))
        {
            return null;
        }

        return tombstone;
    }

    private static async Task PersistAndPublishCompletionAsync(
        ToolCallModuleState state,
        WorkflowToolCallCompletionOutboxEntry completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var cached = FindCompletion(
            state,
            completion.RunId,
            completion.StepId,
            completion.CallId,
            completion.ExecutionId,
            completion.ApprovalRequestId,
            completion.TerminalDecision);
        if (cached == null && FindCompletionTombstone(
                state,
                completion.RunId,
                completion.StepId,
                completion.CallId,
                completion.ExecutionId,
                completion.ApprovalRequestId,
                completion.TerminalDecision) != null)
        {
            return;
        }

        if (cached == null)
        {
            state.Completions.Add(completion);
            cached = completion;
            await SaveStateAsync(state, ctx, ct);
        }

        await TrySchedulePublicationRecoveryAsync(ctx, BuildCompletionRetry(cached), ct);
        await PublishUnpublishedCompletionEventsAsync(state, cached, ctx, ct);
    }

    private static async Task PublishUnpublishedCompletionEventsAsync(
        ToolCallModuleState state,
        WorkflowToolCallCompletionOutboxEntry completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (completion.ToolCompletion != null && !completion.ToolCompletionPublished)
        {
            await ExecuteDurablePublicationAsync(
                async () =>
                {
                    await ctx.PublishAsync(
                        completion.ToolCompletion.Clone(),
                        TopologyAudience.Self,
                        ct,
                        BuildPublicationOptions("workflow-tool-call-completed", completion));
                    completion.ToolCompletionPublished = true;
                    await SaveStateAsync(state, ctx, ct);
                },
                "tool completion",
                ct);
        }

        if (completion.StepCompletion != null && !completion.StepCompletionPublished)
        {
            await ExecuteDurablePublicationAsync(
                async () =>
                {
                    await ctx.PublishAsync(
                        completion.StepCompletion.Clone(),
                        TopologyAudience.Self,
                        ct,
                        BuildPublicationOptions("workflow-tool-step-completed", completion));
                    completion.StepCompletionPublished = true;
                    await SaveStateAsync(state, ctx, ct);
                },
                "step completion",
                ct);
        }

        if ((completion.ToolCompletion == null || completion.ToolCompletionPublished) &&
            (completion.StepCompletion == null || completion.StepCompletionPublished))
        {
            await ExecuteDurablePublicationAsync(
                () => CompressCompletionToTombstoneAsync(state, completion, ctx, ct),
                "completion tombstone checkpoint",
                ct);
        }
    }

    private static Task PersistAndPublishToolFailureAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        CancellationToken ct) =>
        PersistAndPublishToolFailureAsync(
            state,
            ctx,
            request,
            toolName,
            error,
            string.Empty,
            string.Empty,
            ComposeWorkflowToolCallId(request),
            string.Empty,
            WorkflowToolCallTerminalDecision.NoApproval,
            ct);

    private static Task PersistAndPublishToolFailureAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        string approvalRequestId,
        WorkflowToolCallTerminalDecision terminalDecision,
        CancellationToken ct) =>
        PersistAndPublishToolFailureAsync(
            state,
            ctx,
            request,
            toolName,
            error,
            string.Empty,
            string.Empty,
            ComposeWorkflowToolCallId(request),
            approvalRequestId,
            terminalDecision,
            ct);

    private static Task PersistAndPublishToolFailureAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string toolName,
        string error,
        string errorCode,
        string resultJson,
        string callId,
        string approvalRequestId,
        WorkflowToolCallTerminalDecision terminalDecision,
        CancellationToken ct)
    {
        var detail = string.IsNullOrWhiteSpace(errorCode)
            ? error
            : $"{errorCode}: {error}";
        var errorMessage = $"tool '{toolName}' execution failed: {detail}";

        return PersistAndPublishCompletionAsync(state, new WorkflowToolCallCompletionOutboxEntry
        {
            CallId = callId,
            ExecutionId = request.ExecutionId,
            RunId = request.RunId,
            StepId = request.StepId,
            ApprovalRequestId = approvalRequestId,
            TerminalDecision = terminalDecision,
            ToolCompletion = new WorkflowToolCallCompletedEvent
            {
                CallId = callId,
                Success = false,
                ResultJson = resultJson,
                Error = errorMessage,
                RunId = request.RunId,
                StepId = request.StepId,
            },
            StepCompletion = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                ExecutionId = request.ExecutionId,
                Success = false,
                Output = resultJson,
                Error = errorMessage,
            },
        }, ctx, ct);
    }

    private static async Task PublishPendingSuspensionAsync(
        ToolCallModuleState state,
        PendingToolCallApprovalState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.SuspensionPublished)
            return;

        if (pending.Suspension == null)
        {
            await ExecuteDurablePublicationAsync(
                async () =>
                {
                    pending.Suspension = BuildSuspension(pending);
                    await SaveStateAsync(state, ctx, ct);
                },
                "tool approval suspension outbox upgrade",
                ct);
        }

        var suspension = pending.Suspension ??
            throw new InvalidOperationException("Pending tool approval has no durable suspension payload.");

        await ExecuteDurablePublicationAsync(
            async () =>
            {
                await ctx.PublishAsync(
                    suspension.Clone(),
                    TopologyAudience.Self,
                    ct,
                    BuildPublicationOptions("workflow-tool-approval-suspended", pending));
                pending.SuspensionPublished = true;
                await SaveStateAsync(state, ctx, ct);
            },
            "tool approval suspension",
            ct);
    }

    private static WorkflowSuspendedEvent BuildSuspension(PendingToolCallApprovalState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            SuspensionType = "tool_approval",
            Prompt = $"Approve tool '{pending.ToolName}' execution?",
            ToolApproval = new WorkflowToolApprovalSuspension
            {
                ExecutionId = pending.ExecutionId,
                ToolName = pending.ToolName,
                ToolCallId = pending.ToolCallId,
                ApprovalRequestId = pending.ApprovalRequestId,
            },
        };

    private static WorkflowToolCallPublicationRetryFiredEvent BuildCompletionRetry(
        WorkflowToolCallCompletionOutboxEntry completion) =>
        new()
        {
            PublicationKind = WorkflowToolCallPublicationKind.Completion,
            RunId = completion.RunId,
            StepId = completion.StepId,
            CallId = completion.CallId,
            ExecutionId = completion.ExecutionId,
            ApprovalRequestId = completion.ApprovalRequestId,
            TerminalDecision = completion.TerminalDecision,
        };

    private static WorkflowToolCallPublicationRetryFiredEvent BuildSuspensionRetry(
        PendingToolCallApprovalState pending) =>
        new()
        {
            PublicationKind = WorkflowToolCallPublicationKind.Suspension,
            RunId = pending.RunId,
            StepId = pending.StepId,
            CallId = pending.ToolCallId,
            ExecutionId = pending.ExecutionId,
            ApprovalRequestId = pending.ApprovalRequestId,
        };

    private static async Task<bool> TrySchedulePublicationRecoveryAsync(
        IWorkflowExecutionContext ctx,
        WorkflowToolCallPublicationRetryFiredEvent retry,
        CancellationToken ct,
        bool allowImmediateContinuation = true)
    {
        try
        {
            await ctx.ScheduleSelfDurableTimeoutAsync(
                BuildPublicationRetryCallbackId(retry),
                PublicationRetryDelay,
                retry,
                BuildPublicationRetryOptions(retry),
                ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "ToolCall: failed to schedule durable publication recovery run={RunId} step={StepId} kind={PublicationKind}",
                retry.RunId,
                retry.StepId,
                retry.PublicationKind);
            if (allowImmediateContinuation)
            {
                try
                {
                    await ctx.PublishAsync(
                        retry.Clone(),
                        TopologyAudience.Self,
                        ct,
                        BuildPublicationRetryOptions(retry));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception continuationException)
                {
                    ctx.Logger.LogWarning(
                        continuationException,
                        "ToolCall: immediate publication recovery continuation failed; actor activation will recover run={RunId} step={StepId} kind={PublicationKind}",
                        retry.RunId,
                        retry.StepId,
                        retry.PublicationKind);
                }
            }

            return false;
        }
    }

    private static async Task CompressCompletionToTombstoneAsync(
        ToolCallModuleState state,
        WorkflowToolCallCompletionOutboxEntry completion,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var tombstone = new WorkflowToolCallCompletionTombstone
        {
            RunId = NormalizeRequired(completion.RunId),
            StepId = NormalizeRequired(completion.StepId),
            CallId = NormalizeRequired(completion.CallId),
            ExecutionId = NormalizeRequired(completion.ExecutionId),
            ApprovalRequestId = NormalizeRequired(completion.ApprovalRequestId),
            TerminalDecision = completion.TerminalDecision,
        };
        state.CompletionTombstones[BuildCompletionKey(tombstone.CallId, tombstone.ExecutionId)] = tombstone;
        state.Completions.Remove(completion);
        await SaveStateAsync(state, ctx, ct);
    }

    private static async Task ExecuteDurablePublicationAsync(
        Func<Task> action,
        string operation,
        CancellationToken ct)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkflowDurablePublicationPendingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowDurablePublicationPendingException(
                $"Durable workflow {operation} remains pending.",
                ex);
        }
    }

    private static bool HasCompleteResumeIdentity(WorkflowResumedEvent resumed) =>
        !string.IsNullOrWhiteSpace(resumed.RunId) &&
        !string.IsNullOrWhiteSpace(resumed.StepId) &&
        resumed.ToolApproval != null &&
        !string.IsNullOrWhiteSpace(resumed.ToolApproval.ExecutionId) &&
        !string.IsNullOrWhiteSpace(resumed.ToolApproval.ToolCallId) &&
        !string.IsNullOrWhiteSpace(resumed.ToolApproval.ApprovalRequestId);

    private static bool MatchesCallIdentity(
        string? actualRunId,
        string? actualStepId,
        string? actualCallId,
        string? actualExecutionId,
        string? expectedRunId,
        string? expectedStepId,
        string? expectedCallId,
        string? expectedExecutionId) =>
        string.Equals(NormalizeRequired(actualRunId), NormalizeRequired(expectedRunId), StringComparison.Ordinal) &&
        string.Equals(NormalizeRequired(actualStepId), NormalizeRequired(expectedStepId), StringComparison.Ordinal) &&
        string.Equals(NormalizeRequired(actualCallId), NormalizeRequired(expectedCallId), StringComparison.Ordinal) &&
        string.Equals(NormalizeRequired(actualExecutionId), NormalizeRequired(expectedExecutionId), StringComparison.Ordinal);

    private static string BuildCompletionKey(string? callId, string? executionId) =>
        RuntimeCallbackKeyComposer.BuildKey('|', NormalizeRequired(callId), NormalizeRequired(executionId));

    private static EventEnvelopePublishOptions BuildPublicationOptions(
        string prefix,
        WorkflowToolCallCompletionOutboxEntry completion) =>
        BuildPublicationOptions(
            prefix,
            completion.RunId,
            completion.StepId,
            completion.CallId,
            completion.ExecutionId,
            completion.ApprovalRequestId);

    private static EventEnvelopePublishOptions BuildPublicationOptions(
        string prefix,
        PendingToolCallApprovalState pending) =>
        BuildPublicationOptions(
            prefix,
            pending.RunId,
            pending.StepId,
            pending.ToolCallId,
            pending.ExecutionId,
            pending.ApprovalRequestId);

    private static EventEnvelopePublishOptions BuildPublicationOptions(
        string prefix,
        params string[] identity) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(prefix, identity),
            },
        };

    private static StepRequestEvent ToStepRequest(PendingToolCallApprovalState pending) =>
        new()
        {
            StepId = pending.StepId,
            StepType = "tool_call",
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Input = pending.ArgumentsJson,
            IdempotencyKey = pending.IdempotencyKey,
            Parameters = { ["tool"] = pending.ToolName },
            InputFileRefs = { pending.InputFileRefs.Select(static fileRef => fileRef.Clone()) },
            ExternalInvocation = pending.ExternalInvocation?.Clone(),
        };

    private static string BuildRejectedApprovalError(WorkflowResumedEvent resumed)
    {
        var feedback = Normalize(resumed.Feedback)
                       ?? Normalize(resumed.UserInput)
                       ?? Normalize(resumed.EditedContent);
        return feedback == null
            ? "approval rejected"
            : $"approval rejected: {feedback}";
    }

    private static string ResolveArgumentsJson(StepRequestEvent request)
    {
        var configuredArguments = request.Parameters.GetValueOrDefault("arguments", string.Empty);
        if (string.IsNullOrWhiteSpace(configuredArguments))
            configuredArguments = request.Parameters.GetValueOrDefault("args", string.Empty);

        if (!string.IsNullOrWhiteSpace(configuredArguments))
            return configuredArguments.Trim();

        return string.IsNullOrWhiteSpace(request.Input) ? "{}" : request.Input;
    }

    private static string ComposeWorkflowToolCallId(StepRequestEvent request)
    {
        var runId = Normalize(request.RunId);
        var stepId = Normalize(request.StepId);
        var executionId = Normalize(request.ExecutionId);

        if (runId != null && stepId != null && executionId != null)
            return $"workflow:{runId}:{stepId}:{executionId}";

        if (runId != null && stepId != null)
            return $"workflow:{runId}:{stepId}";

        return stepId ?? executionId ?? runId ?? string.Empty;
    }

    private static long ResolveIssuedAtUnixMs(EventEnvelope envelope) =>
        envelope.Timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string BuildPendingKey(PendingToolCallApprovalState pending) =>
        BuildPendingKey(
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.ToolCallId,
            pending.ApprovalRequestId);

    private static string BuildPendingKey(
        string? runId,
        string? stepId,
        string? executionId,
        string? toolCallId,
        string? approvalRequestId) =>
        $"{NormalizeRequired(runId)}:{NormalizeRequired(stepId)}:{NormalizeRequired(executionId)}:{NormalizeRequired(toolCallId)}:{NormalizeRequired(approvalRequestId)}";

    private static Task SaveStateAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.PendingApprovals.Count == 0 &&
            state.Completions.Count == 0 &&
            state.CompletionTombstones.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private Task<IReadOnlyDictionary<string, IWorkflowTool>> GetOrDiscoverAsync(CancellationToken ct)
    {
        while (true)
        {
            var current = _toolIndex;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter88/cluster-088):
            // Old: workflow tool discovery started before CompareExchange, so loser callers could
            // repeat source discovery and external MCP lifecycle work.
            // New: publish Lazy<Task<T>> before evaluation; only the winning Lazy starts discovery.
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>(
                () => DiscoverAllToolsAsync(_toolSources, _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolIndex, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IWorkflowTool>>>? current,
        out Task<IReadOnlyDictionary<string, IWorkflowTool>> task)
    {
        task = null!;
        if (current == null)
            return false;

        if (!current.IsValueCreated)
        {
            task = current.Value;
            return true;
        }

        var existing = current.Value;
        if (existing.IsFaulted || existing.IsCanceled)
            return false;

        task = existing;
        return true;
    }
    private static async Task<IReadOnlyDictionary<string, IWorkflowTool>> DiscoverAllToolsAsync(
        IEnumerable<IWorkflowToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, IWorkflowTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in toolSources)
        {
            IReadOnlyList<IWorkflowTool> tools;
            try
            {
                tools = await source.GetToolsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
                index[tool.Name] = tool;
        }

        return index;
    }

}
