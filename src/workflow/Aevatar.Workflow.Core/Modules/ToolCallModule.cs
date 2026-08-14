// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>工具调用模块。处理 type=tool_call 的步骤。</summary>
public sealed partial class ToolCallModule :
    IEventModule<IWorkflowExecutionContext>,
    IWorkflowExecutionBackgroundWorkOwner
{
    internal const string ModuleStateKey = "tool_call";
    private const int DefaultToolTimeoutMs = 360_000;
    private const int MaxToolTimeoutMs = 1_800_000;
    private const int MinToolTimeoutMs = 100;
    private const string ToolTimeoutCallbackPrefix = "workflow-tool-timeout";
    private const string ToolRetryCallbackPrefix = "workflow-tool-retry";
    private const string ToolExecutionRecoveryCallbackPrefix = "workflow-tool-execution-recovery";
    private const string ToolOutcomeUnknownCode = "tool_outcome_unknown";
    private const string ToolOutcomeUnknownMessage =
        "The tool call timed out and the external execution outcome cannot be proven.";
    private const int MaxToolExecutionAttempts = 5;
    private const int MaxCompletionSignalTransportAttempts = 4;
    private static readonly TimeSpan ToolRetryBaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxToolRetryDelay = TimeSpan.FromSeconds(4);
    private const string ProjectedToolFailureCode = "WORKFLOW_PROJECTED_TOOL_CALL_FAILED";
    private const string ProjectedToolFailureMessage =
        "The projected tool call failed before a durable response was produced.";
    private const string NyxIdProxyHttpFailurePrefix = "NYXID_PROXY_HTTP_";
    private static readonly TimeSpan PublicationRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> ProjectionSafeFailureCodes = new(StringComparer.Ordinal)
    {
        "authorization_required",
        "credential_denied",
        "invalid_tool_execution_identity",
        "tool_admission_conflict",
        "tool_admission_unavailable",
        "tool_denied",
        "tool_error",
        "tool_execution_already_started",
        "tool_outcome_unknown",
        "tool_approval_deadline_exceeded",
        "tool_retry_deadline_exceeded",
        "tool_call_protected_material_digest_mismatch",
        "tool_call_protected_material_identity_mismatch",
        "tool_call_protected_material_invalid_encoding",
        "tool_call_protected_material_invalid_identity",
        "tool_call_protected_material_invalid_reference",
        "tool_call_protected_material_resolve_failed",
        "tool_call_protected_material_schema_mismatch",
        "tool_call_protected_material_store_failed",
        "tool_call_protected_material_store_unavailable",
        "tool_call_protected_material_unavailable",
        "tool_watchdog_unavailable",
        "NYXID_PROXY_FORBIDDEN",
        "NYXID_PROXY_RESPONSE_TOO_LARGE",
        "NYXID_PROXY_SERVICE_ID_REQUIRED",
        "NYXID_PROXY_SERVICE_SCOPE_FORBIDDEN",
        "NYXID_PROXY_UNAUTHORIZED",
    };

    internal static TimeSpan DurablePublicationRetryDelay => PublicationRetryDelay;

    private readonly IEnumerable<IWorkflowToolSource> _toolSources;
    private readonly ILogger<ToolCallModule> _logger;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider;
    private readonly ConcurrentDictionary<string, BackgroundExecutionRegistration> _backgroundExecutions =
        new(StringComparer.Ordinal);
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
        envelope.Payload?.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallRetryFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallExecutionRecoveryFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor))
        {
            await HandleToolAttemptCompletedAsync(
                payload.Unpack<WorkflowToolCallAttemptCompletedEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor))
        {
            await HandleToolTimeoutFiredAsync(
                payload.Unpack<WorkflowToolCallTimeoutFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowToolCallRetryFiredEvent.Descriptor))
        {
            await HandleApprovedToolRetryFiredAsync(
                payload.Unpack<WorkflowToolCallRetryFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowToolCallExecutionRecoveryFiredEvent.Descriptor))
        {
            await HandleToolExecutionRecoveryFiredAsync(
                payload.Unpack<WorkflowToolCallExecutionRecoveryFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

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

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(toolName, out var tool))
        {
            const string notFound = "tool not found or no tool sources configured";
            await PersistAndPublishToolFailureAsync(state, ctx, request, toolName, notFound, ct);
            return;
        }

        WorkflowToolExecutionRequest executionRequest;
        try
        {
            executionRequest = await BuildToolExecutionRequestAsync(
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
            ctx.Logger.LogWarning(
                "ToolCall: step={StepId} tool={Tool} dispatch preparation failed failure_type={FailureType}",
                request.StepId,
                toolName,
                ex.GetType().Name);
            var result = request.ExternalInvocation?.ResponseProjection is null
                ? WorkflowToolExecutionResult.Failed(string.Empty, string.Empty, ex.Message)
                : ProjectedToolFailure();
            await PersistAndPublishToolOutcomeAsync(state, ctx, request, toolName, callId, result, ct);
            return;
        }

        ToolCallProtectedMaterial protectedMaterial;
        RuntimeSecretReference protectedMaterialReference;
        try
        {
            protectedMaterial = BuildProtectedMaterial(
                request,
                request.RunId,
                toolName,
                callId,
                approvalRequestId: string.Empty);
            protectedMaterialReference = await StoreProtectedMaterialAsync(protectedMaterial, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception is InvalidOperationException &&
                            IsProtectedMaterialErrorCode(exception.Message)
                ? exception.Message
                : ToolCallProtectedMaterialErrorCodes.StoreFailed;
            ctx.Logger.LogWarning(
                "ToolCall: protected material store failed run={RunId} step={StepId} failure_type={FailureType}",
                request.RunId,
                request.StepId,
                exception.GetType().Name);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                request,
                toolName,
                "The tool call was not dispatched because its protected material could not be stored.",
                errorCode,
                string.Empty,
                callId,
                string.Empty,
                WorkflowToolCallTerminalDecision.NoApproval,
                terminalInvoked: false,
                retryable: false,
                protectedMaterialReference: null,
                ct);
            return;
        }

        var pending = BuildPendingExecution(
            request,
            toolName,
            callId,
            issuedAtUnixMs,
            ResolveToolTimeoutMs(request),
            ctx.UtcNow,
            approvalRequestId: string.Empty,
            WorkflowToolCallTerminalDecision.NoApproval,
            protectedMaterialReference,
            ComputeProtectedMaterialDigest(protectedMaterial));
        await StartToolExecutionAsync(
            state,
            pending,
            tool,
            executionRequest,
            request.ExternalInvocation?.ResponseProjection,
            ctx,
            ct);
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
            var awakened = await TrySchedulePublicationRecoveryAsync(
                ctx,
                retry,
                ct,
                allowImmediateContinuation: false);
            if (!awakened)
            {
                throw new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
                    "Durable tool-call publication retry has no confirmed wakeup.",
                    ex);
            }
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

    private async Task<WorkflowToolExecutionRequest> BuildToolExecutionRequestAsync(
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
        var unattendedPermit = ResolveUnattendedInvocationPermit(
            ctx,
            credential.Found ? credential.Credential : null,
            admission);
        var runtimeContext = WorkflowRunExecutionContextStateAccess.GetWorkflowRuntimeContext(
            ctx,
            ctx.AgentId ?? string.Empty,
            request.RunId ?? string.Empty,
            request.StepId ?? string.Empty);
        return new WorkflowToolExecutionRequest(
            ArgumentsJson: argumentsJson,
            RunId: request.RunId ?? string.Empty,
            StepId: request.StepId ?? string.Empty,
            ExecutionId: request.ExecutionId ?? string.Empty,
            CallId: callId,
            ScopeId: ctx.ScopeId ?? string.Empty,
            CallerCredential: callerCredential.Clone(),
            RuntimeContext: runtimeContext,
            ApprovalGrant: approvalGrant,
            InputFileRefs: request.InputFileRefs,
            IdempotencyKey: request.IdempotencyKey ?? string.Empty,
            ScheduleId: ctx.ScheduleId ?? string.Empty,
            InvocationAdmission: admission,
            LlmControl: GetLlmControl(ctx),
            IssuedAtUnixMs: issuedAtUnixMs,
            UnattendedInvocationPermit: unattendedPermit);
    }

    private static WorkflowUnattendedInvocationPermit? ResolveUnattendedInvocationPermit(
        IWorkflowExecutionContext ctx,
        WorkflowCallerCredential? credential,
        WorkflowCapabilityInvocationAdmission? admission)
    {
        if (ctx is not IWorkflowExecutionStateHostAccessor accessor ||
            accessor.StateHost.ExecutionContextSnapshot.UnattendedEffectAuthorization is not { } authorization)
            return null;

        if (!string.Equals(accessor.StateHost.RunOrigin, WorkflowRunOrigins.Webhook, StringComparison.Ordinal) ||
            credential?.NyxIdAuthority is not { } authority ||
            string.IsNullOrWhiteSpace(authority.BindingId))
        {
            throw new InvalidOperationException(
                "The unattended webhook authorization is not bound to a valid actor-owned caller authority.");
        }

        try
        {
            WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForActorState(
                authorization,
                authority,
                accessor.StateHost.DefinitionActorId,
                accessor.StateHost.ScopeId,
                accessor.StateHost.WorkflowId,
                accessor.StateHost.RevisionId,
                accessor.StateHost.DefinitionVersion,
                accessor.StateHost.CapabilityAdmissionPlanSnapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The unattended webhook authorization no longer matches the actor-owned workflow definition.",
                exception);
        }

        var permit = WorkflowUnattendedEffectAuthorizationIntegrity.CreateInvocationPermit(
            authorization,
            authority,
            admission);
        if (permit is null && RequiresUnattendedEffectPermit(admission))
        {
            throw new InvalidOperationException(
                "The workflow effect is not covered by the exact unattended webhook authorization.");
        }

        return permit;
    }

    private static bool RequiresUnattendedEffectPermit(WorkflowCapabilityInvocationAdmission? admission) =>
        admission?.Capability?.CapabilityCase ==
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest &&
        admission.Capability.NyxIdUserRequest.ExecutionPolicy is { } policy &&
        policy.Risk == NyxIdOperationRisk.Write;

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

        var materialResolution = await ResolveAndVerifyProtectedMaterialAsync(
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.ToolCallId,
            ctx,
            ct);
        if (!materialResolution.Resolved)
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                ToStepRequest(pending, material: null),
                pending.ToolName,
                "The approved tool call was not dispatched because its protected material is unavailable.",
                materialResolution.ErrorCode,
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                resumed.Approved
                    ? WorkflowToolCallTerminalDecision.Approved
                    : WorkflowToolCallTerminalDecision.Denied,
                terminalInvoked: false,
                retryable: false,
                pending.ProtectedMaterialReference,
                ct);
            return;
        }

        var protectedMaterial = materialResolution.Material!;
        var resumedRequest = ToStepRequest(pending, protectedMaterial);
        if (pending.TimeoutDeadlineUnixMs <= 0 ||
            ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs)
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                resumedRequest,
                pending.ToolName,
                "The approved tool call was not dispatched because its original execution deadline elapsed.",
                "tool_approval_deadline_exceeded",
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                resumed.Approved
                    ? WorkflowToolCallTerminalDecision.Approved
                    : WorkflowToolCallTerminalDecision.Denied,
                terminalInvoked: false,
                retryable: false,
                pending.ProtectedMaterialReference,
                ct);
            return;
        }

        if (!resumed.Approved)
        {
            state.PendingApprovals.Remove(pendingKey);
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                resumedRequest,
                pending.ToolName,
                BuildRejectedApprovalError(resumed),
                "approval_denied",
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Denied,
                terminalInvoked: false,
                retryable: false,
                pending.ProtectedMaterialReference,
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
                resumedRequest,
                pending.ToolName,
                "tool not found or no tool sources configured",
                string.Empty,
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Approved,
                terminalInvoked: false,
                retryable: false,
                pending.ProtectedMaterialReference,
                ct);
            return;
        }

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
                string.Empty,
                string.Empty,
                pending.ToolCallId,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Approved,
                terminalInvoked: false,
                retryable: false,
                pending.ProtectedMaterialReference,
                ct);
            return;
        }

        WorkflowToolExecutionRequest executionRequest;
        try
        {
            executionRequest = await BuildToolExecutionRequestAsync(
                protectedMaterial.ArgumentsJson,
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
                "ToolCall: step={StepId} tool={Tool} approved dispatch preparation failed failure_type={FailureType}",
                pending.StepId,
                pending.ToolName,
                ex.GetType().Name);
            state.PendingApprovals.Remove(pendingKey);
            var result = resumedRequest.ExternalInvocation?.ResponseProjection is null
                ? WorkflowToolExecutionResult.Failed(string.Empty, string.Empty, ex.Message)
                : ProjectedToolFailure();
            await PersistAndPublishToolOutcomeAsync(
                state,
                ctx,
                resumedRequest,
                pending.ToolName,
                pending.ToolCallId,
                result,
                pending.ApprovalRequestId,
                WorkflowToolCallTerminalDecision.Approved,
                pending.ProtectedMaterialReference,
                ct);
            return;
        }

        state.PendingApprovals.Remove(pendingKey);
        var executionPending = BuildPendingExecution(
            resumedRequest,
            pending.ToolName,
            pending.ToolCallId,
            pending.IssuedAtUnixMs,
            pending.TimeoutMs,
            ctx.UtcNow,
            pending.ApprovalRequestId,
            WorkflowToolCallTerminalDecision.Approved,
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.TimeoutDeadlineUnixMs,
            pending.ContinuationToken,
            checked(pending.Attempt + 1));
        await StartToolExecutionAsync(
            state,
            executionPending,
            tool,
            executionRequest,
            resumedRequest.ExternalInvocation?.ResponseProjection,
            ctx,
            ct);
    }

    private static WorkflowToolExecutionResult ApplyResponseProjection(
        WorkflowToolResponseProjection? projection,
        WorkflowToolExecutionResult result,
        ILogger logger,
        string runId,
        string stepId)
    {
        if (projection is null)
            return result;

        if (result.PendingApproval is not null)
        {
            if (result.Failure is not null ||
                result.ManagedHandoff is not null ||
                !string.IsNullOrEmpty(result.ResultJson))
            {
                logger.LogWarning(
                    "ToolCall: projected response returned an inconsistent approval outcome run={RunId} step={StepId}",
                    runId,
                    stepId);
                return ProjectedToolFailure();
            }

            return result;
        }

        // A provider failure remains the authoritative failure, but its response body must not
        // bypass an authored persistence boundary.
        if (result.Failure is not null)
        {
            var safeCode = IsProjectionSafeFailureCode(result.Failure.ErrorCode)
                ? result.Failure.ErrorCode
                : ProjectedToolFailureCode;
            return result with
            {
                ResultJson = string.Empty,
                Failure = result.Failure with
                {
                    ErrorCode = safeCode,
                    ErrorMessage = ProjectedToolFailureMessage,
                },
            };
        }

        try
        {
            return result with
            {
                ResultJson = WorkflowToolResponseProjector.Project(result.ResultJson, projection),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(
                "ToolCall: response projection failed before persistence run={RunId} step={StepId}",
                runId,
                stepId);
            return WorkflowToolExecutionResult.Failed(
                string.Empty,
                "WORKFLOW_TOOL_RESPONSE_PROJECTION_FAILED",
                "The tool response did not satisfy the admitted response projection.",
                terminalInvoked: true,
                retryable: false);
        }
    }

    private static bool IsProjectionSafeFailureCode(string errorCode)
    {
        if (string.IsNullOrEmpty(errorCode))
            return false;

        if (ProjectionSafeFailureCodes.Contains(errorCode))
            return true;

        return errorCode.Length == NyxIdProxyHttpFailurePrefix.Length + 3 &&
               errorCode.StartsWith(NyxIdProxyHttpFailurePrefix, StringComparison.Ordinal) &&
               errorCode[NyxIdProxyHttpFailurePrefix.Length] is >= '1' and <= '5' &&
               errorCode[NyxIdProxyHttpFailurePrefix.Length + 1] is >= '0' and <= '9' &&
               errorCode[NyxIdProxyHttpFailurePrefix.Length + 2] is >= '0' and <= '9';
    }

    private static WorkflowToolExecutionResult ProjectedToolFailure() =>
        WorkflowToolExecutionResult.Failed(
            string.Empty,
            ProjectedToolFailureCode,
            ProjectedToolFailureMessage,
            terminalInvoked: true,
            retryable: false);

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
        int timeoutMs,
        long timeoutDeadlineUnixMs,
        string timeoutCallbackId,
        WorkflowRuntimeCallbackLeaseState? timeoutLease,
        string continuationToken,
        int attempt,
        WorkflowToolApprovalPendingOutcome pending,
        RuntimeSecretReference protectedMaterialReference,
        string protectedMaterialDigestSha256,
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
            IssuedAtUnixMs = issuedAtUnixMs,
            TimeoutMs = timeoutMs,
            TimeoutDeadlineUnixMs = timeoutDeadlineUnixMs,
            TimeoutCallbackId = NormalizeRequired(timeoutCallbackId),
            TimeoutLease = timeoutLease?.Clone(),
            ContinuationToken = NormalizeRequired(continuationToken),
            Attempt = Math.Max(1, attempt),
            ProtectedMaterialReference = protectedMaterialReference.Clone(),
            ProtectedMaterialDigestSha256 = protectedMaterialDigestSha256,
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
        };
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
            protectedMaterialReference: null,
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
        RuntimeSecretReference? protectedMaterialReference,
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
                result.Failure.TerminalInvoked,
                result.Failure.Retryable,
                protectedMaterialReference,
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
            ProtectedMaterialReference = protectedMaterialReference?.Clone(),
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

    private async Task<bool> TryHandleStepRedeliveryAsync(
        ToolCallModuleState state,
        StepRequestEvent request,
        string callId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (await TryDrainPersistedCompletionAsync(
                state,
                request.RunId,
                request.StepId,
                callId,
                request.ExecutionId,
                ctx,
                ct))
        {
            return true;
        }

        if (state.PendingExecutions.TryGetValue(BuildExecutionKey(callId, request.ExecutionId), out var execution) &&
            MatchesExecutionIdentity(
                execution,
                request.RunId,
                request.StepId,
                callId,
                request.ExecutionId))
        {
            var pendingKey = BuildExecutionKey(execution.CallId, execution.ExecutionId);
            if (execution.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending &&
                !_backgroundExecutions.ContainsKey(pendingKey))
            {
                if (!await EnsurePendingExecutionWatchdogAsync(execution, ctx, ct))
                    return true;
                await EnsureExecutionRecoveryWakeupAsync(execution, ctx, ct);
            }

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

    private static async Task<bool> TryDrainPersistedCompletionAsync(
        ToolCallModuleState state,
        string? runId,
        string? stepId,
        string? callId,
        string? executionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct,
        string? approvalRequestId = null,
        WorkflowToolCallTerminalDecision? terminalDecision = null)
    {
        var cached = FindCompletion(
            state,
            runId,
            stepId,
            callId,
            executionId,
            approvalRequestId,
            terminalDecision);
        if (cached != null)
        {
            await TrySchedulePublicationRecoveryAsync(ctx, BuildCompletionRetry(cached), ct);
            await PublishUnpublishedCompletionEventsAsync(state, cached, ctx, ct);
            return true;
        }

        return FindCompletionTombstone(
                state,
                runId,
                stepId,
                callId,
                executionId,
                approvalRequestId,
                terminalDecision) != null;
    }

    private static async Task<bool> TryDrainPersistedAttemptSuccessorAsync(
        ToolCallModuleState state,
        string? runId,
        string? stepId,
        string? callId,
        string? executionId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (await TryDrainPersistedCompletionAsync(
                state,
                runId,
                stepId,
                callId,
                executionId,
                ctx,
                ct))
        {
            return true;
        }

        var pending = state.PendingApprovals.Values.FirstOrDefault(candidate =>
            MatchesCallIdentity(
                candidate.RunId,
                candidate.StepId,
                candidate.ToolCallId,
                candidate.ExecutionId,
                runId,
                stepId,
                callId,
                executionId));
        if (pending == null)
            return false;

        await TrySchedulePublicationRecoveryAsync(ctx, BuildSuspensionRetry(pending), ct);
        await PublishPendingSuspensionAsync(state, pending, ctx, ct);
        return true;
    }

    private async Task<bool> TryHandleResumeRedeliveryAsync(
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
        if (state.PendingExecutions.TryGetValue(
                BuildExecutionKey(
                    resumed.ToolApproval.ToolCallId,
                    resumed.ToolApproval.ExecutionId),
                out var execution) &&
            MatchesExecutionIdentity(
                execution,
                resumed.RunId,
                resumed.StepId,
                resumed.ToolApproval.ToolCallId,
                resumed.ToolApproval.ExecutionId) &&
            string.Equals(
                execution.ApprovalRequestId,
                NormalizeRequired(resumed.ToolApproval.ApprovalRequestId),
                StringComparison.Ordinal) &&
            execution.TerminalDecision == decision)
        {
            var pendingKey = BuildExecutionKey(execution.CallId, execution.ExecutionId);
            if (execution.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending &&
                !_backgroundExecutions.ContainsKey(pendingKey))
            {
                if (!await EnsurePendingExecutionWatchdogAsync(execution, ctx, ct))
                    return true;
                await EnsureExecutionRecoveryWakeupAsync(execution, ctx, ct);
            }

            return true;
        }

        return await TryDrainPersistedCompletionAsync(
            state,
            resumed.RunId,
            resumed.StepId,
            resumed.ToolApproval.ToolCallId,
            resumed.ToolApproval.ExecutionId,
            ctx,
            ct,
            resumed.ToolApproval.ApprovalRequestId,
            decision);
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
            if (completion.ProtectedMaterialReference != null)
            {
                await ExecuteDurablePublicationAsync(
                    async () =>
                    {
                        if (!await RevokeOrConfirmProtectedMaterialUnavailableAsync(
                                completion.ProtectedMaterialReference,
                                ctx,
                                ct))
                        {
                            throw new InvalidOperationException(
                                "Protected tool-call material cleanup remains pending.");
                        }

                        completion.ProtectedMaterialReference = null;
                        await SaveStateAsync(state, ctx, ct);
                    },
                    "protected tool-call material cleanup",
                    ct);
            }

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
            terminalInvoked: false,
            retryable: false,
            protectedMaterialReference: null,
            ct: ct);

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
            terminalInvoked: false,
            retryable: false,
            protectedMaterialReference: null,
            ct: ct);

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
        bool terminalInvoked,
        bool retryable,
        RuntimeSecretReference? protectedMaterialReference,
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
            ProtectedMaterialReference = protectedMaterialReference?.Clone(),
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
                FailureOutcome = string.Equals(errorCode, ToolOutcomeUnknownCode, StringComparison.Ordinal)
                    ? WorkflowStepFailureOutcome.OutcomeUncertain
                    : WorkflowStepFailureOutcome.CalleeConfirmed,
                RetryDisposition = !terminalInvoked && retryable
                    ? WorkflowStepRetryDisposition.Allowed
                    : WorkflowStepRetryDisposition.Forbidden,
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
                    return true;
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

    private static StepRequestEvent ToStepRequest(
        PendingToolCallApprovalState pending,
        ToolCallProtectedMaterial? material) =>
        new()
        {
            StepId = pending.StepId,
            StepType = "tool_call",
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Input = material?.Input ?? string.Empty,
            IdempotencyKey = material?.IdempotencyKey ?? string.Empty,
            DisplayName = ResolveStepDisplayName(material?.DisplayName, pending.StepId),
            Parameters = { ["tool"] = pending.ToolName },
            InputFileRefs = { material?.InputFileRefs.Select(static fileRef => fileRef.Clone()) ?? [] },
            ExternalInvocation = material?.ExternalInvocation?.Clone(),
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

    private static string ResolveStepDisplayName(string? displayName, string? stepId)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? NormalizeRequired(stepId) : normalized;
    }

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
        ScrubLegacyPayloadFields(state);
        if (state.PendingApprovals.Count == 0 &&
            state.PendingExecutions.Count == 0 &&
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
