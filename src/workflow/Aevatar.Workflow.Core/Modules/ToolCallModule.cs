// ─────────────────────────────────────────────────────────────
// ToolCallModule — 工具调用模块
// 在工作流步骤中调用 Agent 的注册工具
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
    private const long MaxDurableOperationExecutionMs = 600_000;
    private const long DurableOperationReconciliationMarginMs = 120_000;
    private const long DurableOperationFallbackTimeoutMs =
        MaxDurableOperationExecutionMs + DurableOperationReconciliationMarginMs;
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
    private const string OperationPollCallbackPrefix = "workflow-tool-operation-poll";
    private const string StopCancellationCallbackPrefix = "workflow-tool-stop-cancellation";
    private const long DefaultOperationPollDelayMs = 1_000;
    private const long MaxOperationPollDelayMs = 30_000;
    private const long DefaultStopCancellationDelayMs = 1_000;
    private const long MaxStopCancellationDelayMs = 30_000;
    private static readonly TimeSpan PublicationRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopCancellationWindow = TimeSpan.FromMinutes(2);
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
        envelope.Payload?.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallOperationPollFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowToolCallStopCancellationFiredEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowStoppedEvent.Descriptor) == true ||
        envelope.Payload?.Is(WorkflowRunStoppedEvent.Descriptor) == true;

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

        if (payload.Is(WorkflowToolCallOperationPollFiredEvent.Descriptor))
        {
            await HandleOperationPollAsync(
                payload.Unpack<WorkflowToolCallOperationPollFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowToolCallStopCancellationFiredEvent.Descriptor))
        {
            await HandleStopCancellationAsync(
                payload.Unpack<WorkflowToolCallStopCancellationFiredEvent>(),
                envelope,
                ctx,
                ct);
            return;
        }

        if (payload.Is(WorkflowStoppedEvent.Descriptor) ||
            payload.Is(WorkflowRunStoppedEvent.Descriptor))
        {
            await HandleStopAsync(envelope, ctx, ct);
            return;
        }

        if (payload.Is(WorkflowResumedEvent.Descriptor))
        {
            await HandleResumeAsync(payload.Unpack<WorkflowResumedEvent>(), ctx, ct);
            return;
        }

        var preparationStartedAtTimestamp = ctx.GetTimestamp();
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
                    OutputProvenance = WorkflowStepOutputProvenance.Produced,
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
                failureOutcome: WorkflowStepFailureOutcome.CalleeConfirmed,
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
            preparationStartedAtTimestamp,
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

    internal static IReadOnlyList<PendingToolCallOperationState> PreparePendingOperationPollRecoveries(
        ToolCallModuleState state,
        DateTimeOffset utcNow,
        out bool stateChanged)
    {
        ArgumentNullException.ThrowIfNull(state);
        stateChanged = false;
        foreach (var pending in state.PendingOperations.Values)
        {
            if (EnsureOperationPollPrepared(pending, utcNow))
                stateChanged = true;
        }

        return state.PendingOperations.Values.ToArray();
    }

    internal static IReadOnlyList<PendingToolCallOperationState> PreparePendingStopCancellationRecoveries(
        ToolCallModuleState state,
        DateTimeOffset utcNow,
        out bool stateChanged)
    {
        ArgumentNullException.ThrowIfNull(state);
        stateChanged = false;
        if (state.StopCancellation == null)
            return [];

        foreach (var pending in state.PendingOperations.Values)
        {
            if (EnsureStopCancellationPrepared(pending, utcNow, state.StopCancellation.ExpiresAtUnixMs))
                stateChanged = true;
        }

        return state.PendingOperations.Values.ToArray();
    }

    internal static WorkflowToolCallOperationPollFiredEvent BuildOperationPollEvent(
        PendingToolCallOperationState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.ToolCallId,
            OperationId = pending.OperationId,
            PollAttempt = pending.PollAttempt,
            CallbackId = pending.PollCallbackId,
        };

    internal static TimeSpan BuildOperationPollDelay(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow)
    {
        var nowUnixMs = utcNow.ToUnixTimeMilliseconds();
        var nextPollUnixMs = pending.NextPollUnixMs;
        if (pending.ExpiresAtUnixMs > nowUnixMs)
            nextPollUnixMs = Math.Min(nextPollUnixMs, pending.ExpiresAtUnixMs);

        var remainingMs = nextPollUnixMs - nowUnixMs;
        return remainingMs <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Min(remainingMs, MaxOperationPollDelayMs));
    }

    internal static EventEnvelopePublishOptions BuildOperationPollOptions(
        PendingToolCallOperationState pending) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = pending.PollCallbackId,
            },
        };

    internal static WorkflowToolCallStopCancellationFiredEvent BuildStopCancellationEvent(
        PendingToolCallOperationState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.ToolCallId,
            OperationId = pending.OperationId,
            Attempt = pending.StopCancellationAttempt,
            CallbackId = pending.StopCancellationCallbackId,
        };

    internal static TimeSpan BuildStopCancellationDelay(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow,
        long stopDeadlineUnixMs)
    {
        var nowUnixMs = utcNow.ToUnixTimeMilliseconds();
        var nextUnixMs = pending.NextStopCancellationUnixMs;
        if (stopDeadlineUnixMs > nowUnixMs)
            nextUnixMs = Math.Min(nextUnixMs, stopDeadlineUnixMs);

        var remainingMs = nextUnixMs - nowUnixMs;
        return remainingMs <= 0
            ? TimeSpan.FromMilliseconds(1)
            : TimeSpan.FromMilliseconds(Math.Min(remainingMs, MaxStopCancellationDelayMs));
    }

    internal static EventEnvelopePublishOptions BuildStopCancellationOptions(
        PendingToolCallOperationState pending) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = pending.StopCancellationCallbackId,
            },
        };

    internal static IMessage? BuildPendingStopReleaseEvent(ToolCallModuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stop = state.StopCancellation;
        if (stop == null || state.PendingOperations.Count != 0)
            return null;

        return stop.StopKind switch
        {
            WorkflowToolStopKind.WorkflowStopped => new WorkflowStoppedEvent
            {
                WorkflowName = stop.WorkflowName,
                RunId = stop.RunId,
                Reason = stop.Reason,
                CompletedAtUtc = stop.CompletedAtUtc?.Clone(),
            },
            WorkflowToolStopKind.WorkflowRunStopped => new WorkflowRunStoppedEvent
            {
                RunId = stop.RunId,
                Reason = stop.Reason,
                CompletedAtUtc = stop.CompletedAtUtc?.Clone(),
            },
            _ => null,
        };
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

    private static async Task HandleStopAsync(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!string.Equals(
                envelope.Route?.PublisherActorId,
                ctx.AgentId,
                StringComparison.Ordinal))
        {
            return;
        }

        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var stopIntent = BuildStopIntent(envelope, ctx);
        if (stopIntent == null)
            return;

        if (state.StopCancellation != null && state.PendingOperations.Count == 0)
        {
            state.StopCancellation = null;
            await SaveStateAsync(state, ctx, ct);
            return;
        }

        if (state.PendingOperations.Count == 0)
            return;

        if (state.StopCancellation == null)
        {
            state.StopCancellation = stopIntent;
        }
        else if (!string.Equals(
                     state.StopCancellation.RunId,
                     stopIntent.RunId,
                     StringComparison.Ordinal))
        {
            return;
        }

        foreach (var pending in state.PendingOperations.Values)
        {
            EnsureStopCancellationPrepared(
                pending,
                ctx.UtcNow,
                state.StopCancellation.ExpiresAtUnixMs);
        }

        await SaveStateAsync(state, ctx, ct);
        foreach (var pending in state.PendingOperations.Values)
            await TryScheduleStopCancellationAsync(state, pending, ctx, ct);

        throw new WorkflowToolStopCancellationPendingException(
            "Workflow stop is waiting for admitted durable tool cancellation.");
    }

    private async Task HandleStopCancellationAsync(
        WorkflowToolCallStopCancellationFiredEvent fired,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var expectedCallbackId = BuildStopCancellationCallbackId(
            fired.RunId,
            fired.StepId,
            fired.CallId,
            fired.ExecutionId,
            fired.OperationId,
            fired.Attempt);
        if (fired.Attempt <= 0 ||
            !string.Equals(
                NormalizeRequired(fired.CallbackId),
                expectedCallbackId,
                StringComparison.Ordinal) ||
            !IsTrustedDurableSelfCallbackEnvelope(envelope, ctx, expectedCallbackId))
        {
            return;
        }

        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var stop = state.StopCancellation;
        if (stop == null ||
            !string.Equals(stop.RunId, NormalizeRequired(fired.RunId), StringComparison.Ordinal))
        {
            return;
        }

        if (state.PendingOperations.Count == 0)
        {
            await PublishPendingStopReleaseAsync(state, ctx, ct);
            return;
        }

        var operationKey = BuildCompletionKey(fired.CallId, fired.ExecutionId);
        if (!state.PendingOperations.TryGetValue(operationKey, out var pending))
        {
            return;
        }

        if (!MatchesStopCancellation(pending, fired))
        {
            if (MatchesRecoverableEarlierStopCancellation(pending, fired))
                await TryScheduleStopCancellationAsync(state, pending, ctx, ct);

            return;
        }

        if (pending.StopCancellationSettled)
        {
            await CompleteSettledStopCancellationAsync(state, operationKey, pending, ctx, ct);
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
        ToolCallProtectedMaterial? material = materialResolution.Material;
        if (!materialResolution.Resolved)
        {
            if (materialResolution.IsTransientFailure ||
                stop.ExpiresAtUnixMs > ctx.UtcNow.ToUnixTimeMilliseconds())
            {
                ctx.Logger.LogWarning(
                    "Durable workflow tool cancellation cannot resolve protected execution material; cancellation remains pending. tool={ToolName} code={FailureCode}",
                    pending.ToolName,
                    materialResolution.ErrorCode);
                await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                return;
            }

            if (pending.StopCancellationRecoveryIntent == null)
            {
                ctx.Logger.LogWarning(
                    "Durable workflow tool cancellation has no frozen recovery audit intent; cancellation remains pending. tool={ToolName} code={FailureCode}",
                    pending.ToolName,
                    materialResolution.ErrorCode);
                await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                return;
            }

            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation protected material is definitively unavailable after the stop deadline; finalizing the frozen outcome-uncertain audit. tool={ToolName} code={FailureCode}",
                pending.ToolName,
                materialResolution.ErrorCode);
            pending.StopCancellationTerminalIntent = pending.StopCancellationRecoveryIntent.Clone();
            pending.StopCancellationPhase = WorkflowToolStopCancellationPhase.FinalizingAudit;
            state.PendingOperations[operationKey] = pending;
            await SaveStateAsync(state, ctx, ct);
        }

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var discoveredTool) ||
            discoveredTool is not IWorkflowDurableOperationTool tool)
        {
            InvalidateToolIndex();
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation implementation is unavailable; cancellation remains pending. tool={ToolName}",
                pending.ToolName);
            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        var request = ToStepRequest(pending, material);
        WorkflowCapabilityInvocationAdmission? admission = null;
        if (material != null)
        {
            admission = ResolveInvocationAdmission(ctx, request, pending.ToolName, out var admissionError);
            if (admissionError != null)
            {
                ctx.Logger.LogWarning(
                    "Durable workflow tool cancellation admission cannot be reconstructed; cancellation remains pending. tool={ToolName} code={FailureCode}",
                    pending.ToolName,
                    "workflow_tool_operation_admission_invalid");
                await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                return;
            }
        }

        WorkflowToolCancellationResult result;
        try
        {
            var executionRequest = await BuildToolExecutionRequestAsync(
                material?.ArgumentsJson ?? "{}",
                request,
                pending.ToolCallId,
                pending.IssuedAtUnixMs,
                ctx,
                ct,
                admission: admission);
            result = await tool.CancelAsync(
                new WorkflowToolCancellationRequest(
                    executionRequest,
                    ToPendingOperation(pending),
                    stop.ExpiresAtUnixMs,
                    FromCancellationTerminalIntent(pending.StopCancellationTerminalIntent)),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation failed transiently; cancellation remains pending. exceptionType={ExceptionType}",
                ex.GetType().Name);
            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        if (result.Disposition == WorkflowToolCancellationDisposition.Pending)
        {
            if (result.PendingOperation is not { } refreshed ||
                !MatchesPendingOperationReceiptIdentity(pending, refreshed))
            {
                ctx.Logger.LogWarning(
                    "Durable workflow tool cancellation returned a mismatched pending receipt; cancellation remains pending. tool={ToolName}",
                    pending.ToolName);
                await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                return;
            }

            UpdatePendingOperationReceipt(pending, refreshed, ctx.UtcNow);
            if (result.PendingTerminalIntent is { } terminalIntentResult)
            {
                if (!IsValidCancellationTerminalIntent(terminalIntentResult))
                {
                    ctx.Logger.LogWarning(
                        "Durable workflow tool cancellation returned an invalid terminal audit intent; the existing cancellation state is retained. tool={ToolName}",
                        pending.ToolName);
                    await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                    return;
                }

                var terminalIntent = ToCancellationTerminalIntent(terminalIntentResult);
                if (pending.StopCancellationTerminalIntent is { } existingIntent &&
                    !MatchesCancellationTerminalIntent(existingIntent, terminalIntent))
                {
                    ctx.Logger.LogWarning(
                        "Durable workflow tool cancellation attempted to replace its persisted terminal audit intent; the original intent is retained. tool={ToolName}",
                        pending.ToolName);
                    await RescheduleStopCancellationAsync(state, pending, ctx, ct);
                    return;
                }

                pending.StopCancellationTerminalIntent = terminalIntent;
                pending.StopCancellationPhase = WorkflowToolStopCancellationPhase.FinalizingAudit;
            }

            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        if (!IsSettledStopCancellation(result))
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation has no valid audited terminal result; cancellation remains pending. tool={ToolName} disposition={Disposition}",
                pending.ToolName,
                result.Disposition);
            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        if (pending.StopCancellationTerminalIntent is { } persistedIntent &&
            !MatchesCancellationTerminalResult(persistedIntent, result.CompletedResult!))
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation completed with a result that conflicts with its persisted terminal audit intent; cancellation remains pending. tool={ToolName}",
                pending.ToolName);
            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        pending.StopCancellationSettled = true;
        state.PendingOperations[operationKey] = pending;
        await SaveStateAsync(state, ctx, ct);
        await CompleteSettledStopCancellationAsync(state, operationKey, pending, ctx, ct);
    }

    private static async Task CompleteSettledStopCancellationAsync(
        ToolCallModuleState state,
        string operationKey,
        PendingToolCallOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.ProtectedMaterialReference != null &&
            !await RevokeOrConfirmProtectedMaterialUnavailableAsync(
                pending.ProtectedMaterialReference,
                ctx,
                ct))
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation settled, but protected material cleanup remains pending. tool={ToolName}",
                pending.ToolName);
            await RescheduleStopCancellationAsync(state, pending, ctx, ct);
            return;
        }

        pending.ProtectedMaterialReference = null;
        pending.ProtectedMaterialDigestSha256 = string.Empty;
        state.PendingOperations.Remove(operationKey);
        await SaveStateAsync(state, ctx, ct);
        if (state.PendingOperations.Count == 0)
            await PublishPendingStopReleaseAsync(state, ctx, ct);
    }

    private static PendingWorkflowToolStopCancellation? BuildStopIntent(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx)
    {
        var completedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
        PendingWorkflowToolStopCancellation? stop = null;
        if (envelope.Payload?.Is(WorkflowStoppedEvent.Descriptor) == true)
        {
            var evt = envelope.Payload.Unpack<WorkflowStoppedEvent>();
            var runId = string.IsNullOrWhiteSpace(evt.RunId)
                ? NormalizeRequired(ctx.RunId)
                : NormalizeRequired(evt.RunId);
            stop = new PendingWorkflowToolStopCancellation
            {
                StopKind = WorkflowToolStopKind.WorkflowStopped,
                RunId = runId,
                WorkflowName = NormalizeRequired(evt.WorkflowName),
                Reason = evt.Reason ?? string.Empty,
                CompletedAtUtc = evt.CompletedAtUtc?.Clone() ?? completedAt,
            };
        }
        else if (envelope.Payload?.Is(WorkflowRunStoppedEvent.Descriptor) == true)
        {
            var evt = envelope.Payload.Unpack<WorkflowRunStoppedEvent>();
            var runId = string.IsNullOrWhiteSpace(evt.RunId)
                ? NormalizeRequired(ctx.RunId)
                : NormalizeRequired(evt.RunId);
            stop = new PendingWorkflowToolStopCancellation
            {
                StopKind = WorkflowToolStopKind.WorkflowRunStopped,
                RunId = runId,
                Reason = evt.Reason ?? string.Empty,
                CompletedAtUtc = evt.CompletedAtUtc?.Clone() ?? completedAt,
            };
        }

        if (stop == null ||
            !string.Equals(stop.RunId, NormalizeRequired(ctx.RunId), StringComparison.Ordinal))
        {
            return null;
        }

        stop.ExpiresAtUnixMs = ctx.UtcNow.Add(StopCancellationWindow).ToUnixTimeMilliseconds();
        return stop;
    }

    private static async Task RescheduleStopCancellationAsync(
        ToolCallModuleState state,
        PendingToolCallOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var stop = state.StopCancellation;
        if (stop == null)
            return;

        PrepareNextStopCancellation(pending, ctx.UtcNow, stop.ExpiresAtUnixMs);
        state.PendingOperations[BuildCompletionKey(pending.ToolCallId, pending.ExecutionId)] = pending;
        await SaveStateAsync(state, ctx, ct);
        await TryScheduleStopCancellationAsync(state, pending, ctx, ct);
    }

    private static async Task TryScheduleStopCancellationAsync(
        ToolCallModuleState state,
        PendingToolCallOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var stop = state.StopCancellation;
        if (stop == null)
            return;

        try
        {
            await ctx.ScheduleSelfDurableTimeoutAsync(
                pending.StopCancellationCallbackId,
                BuildStopCancellationDelay(pending, ctx.UtcNow, stop.ExpiresAtUnixMs),
                BuildStopCancellationEvent(pending),
                BuildStopCancellationOptions(pending),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception scheduleException)
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool cancellation scheduling failed; falling back to a typed self continuation. exceptionType={ExceptionType}",
                scheduleException.GetType().Name);
            try
            {
                await ctx.PublishAsync(
                    BuildStopCancellationEvent(pending),
                    TopologyAudience.Self,
                    ct,
                    BuildStopCancellationOptions(pending));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception continuationException)
            {
                throw new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
                    "Durable workflow tool cancellation continuation remains pending.",
                    continuationException);
            }
        }
    }

    private static async Task PublishPendingStopReleaseAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            switch (BuildPendingStopReleaseEvent(state))
            {
                case WorkflowStoppedEvent workflowStopped:
                    await ctx.PublishAsync(workflowStopped, TopologyAudience.Self, ct);
                    break;
                case WorkflowRunStoppedEvent workflowRunStopped:
                    await ctx.PublishAsync(workflowRunStopped, TopologyAudience.Self, ct);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Persisted workflow tool stop cancellation has no releasable stop event.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not IRuntimeEnvelopeRetryableException)
        {
            throw new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
                "Persisted workflow stop release publication remains pending.",
                ex);
        }
    }

    private static bool IsSettledStopCancellation(WorkflowToolCancellationResult result) =>
        result.Disposition == WorkflowToolCancellationDisposition.Completed &&
        result.CompletedResult is
        {
            PendingOperation: null,
            PendingApproval: null,
            ManagedHandoff: null,
        };

    private async Task HandleOperationPollAsync(
        WorkflowToolCallOperationPollFiredEvent poll,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (state.StopCancellation != null)
            return;

        var operationKey = BuildCompletionKey(poll.CallId, poll.ExecutionId);
        if (!state.PendingOperations.TryGetValue(operationKey, out var pending))
        {
            await TryDrainPersistedOperationPollCompletionAsync(
                state,
                poll,
                envelope,
                ctx,
                ct);
            return;
        }

        if (MatchesPendingOperationPoll(pending, poll))
        {
            if (!IsTrustedDurableSelfCallbackEnvelope(envelope, ctx, pending.PollCallbackId))
                return;
        }
        else
        {
            if (MatchesRecoverableEarlierOperationPoll(pending, poll) &&
                IsTrustedDurableSelfCallbackEnvelope(envelope, ctx, poll.CallbackId))
            {
                await TryScheduleOperationPollAsync(pending, ctx, ct);
            }

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
            if (materialResolution.IsTransientFailure)
            {
                ctx.Logger.LogWarning(
                    "Durable workflow tool protected material resolution failed transiently; operation remains pending. failureCode={FailureCode}",
                    materialResolution.ErrorCode);
                await ReschedulePendingOperationAsync(state, pending, ctx, ct);
                return;
            }

            await CompletePendingOperationWithFailureAsync(
                state,
                operationKey,
                pending,
                null,
                ctx,
                materialResolution.ErrorCode,
                "The durable operation could not be reconciled because its protected execution material is unavailable.",
                ct);
            return;
        }

        var material = materialResolution.Material!;

        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var discoveredTool) ||
            discoveredTool is not IWorkflowDurableOperationTool tool)
        {
            InvalidateToolIndex();
            ctx.Logger.LogWarning(
                "Durable workflow tool reconciliation implementation is unavailable; operation remains pending. tool={ToolName}",
                pending.ToolName);
            await ReschedulePendingOperationAsync(state, pending, ctx, ct);
            return;
        }

        var request = ToStepRequest(pending, material);
        var admission = ResolveInvocationAdmission(ctx, request, pending.ToolName, out var admissionError);
        if (admissionError != null)
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool admission cannot be reconstructed; operation remains pending. tool={ToolName}",
                pending.ToolName);
            await ReschedulePendingOperationAsync(state, pending, ctx, ct);
            return;
        }

        WorkflowToolExecutionResult result;
        try
        {
            var executionRequest = await BuildToolExecutionRequestAsync(
                material.ArgumentsJson,
                request,
                pending.ToolCallId,
                pending.IssuedAtUnixMs,
                ctx,
                ct,
                admission: admission);
            result = await tool.ReconcileAsync(
                executionRequest,
                ToPendingOperation(pending),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool reconciliation failed transiently; operation remains pending. exceptionType={ExceptionType}",
                ex.GetType().Name);
            await ReschedulePendingOperationAsync(state, pending, ctx, ct);
            return;
        }

        if (result.PendingOperation != null)
        {
            if (!IsValidPendingOperationResult(result) ||
                !MatchesPendingOperationReceiptIdentity(pending, result.PendingOperation))
            {
                await CompletePendingOperationWithFailureAsync(
                    state,
                    operationKey,
                    pending,
                    material,
                    ctx,
                    "workflow_tool_operation_identity_mismatch",
                    "The durable tool returned a pending receipt for a different operation.",
                    ct);
                return;
            }

            UpdatePendingOperationReceipt(pending, result.PendingOperation, ctx.UtcNow);
            await ReschedulePendingOperationAsync(state, pending, ctx, ct);
            return;
        }

        if (result.Failure is { TerminalInvoked: false, Retryable: true })
        {
            await ReschedulePendingOperationAsync(state, pending, ctx, ct);
            return;
        }

        if (result.PendingApproval != null)
        {
            await CompletePendingOperationWithFailureAsync(
                state,
                operationKey,
                pending,
                material,
                ctx,
                "workflow_tool_operation_invalid_terminal_outcome",
                "The durable tool requested approval after its operation was already admitted.",
                ct);
            return;
        }

        result = ApplyResponseProjection(
            request.ExternalInvocation?.ResponseProjection,
            result,
            ctx.Logger,
            request.RunId,
            request.StepId);
        state.PendingOperations.Remove(operationKey);
        await PersistAndPublishToolOutcomeAsync(
            state,
            ctx,
            request,
            pending.ToolName,
            pending.ToolCallId,
            result,
            pending.ApprovalRequestId,
            pending.TerminalDecision,
            pending.ProtectedMaterialReference,
            pending,
            ct);
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
        var usesDurableAgentKey =
            WorkflowRunExecutionContextStateAccess.TryGetDurableCallerCredential(
                ctx,
                out var durableCredential) &&
            WorkflowLlmExecutionIntentRuntimeContextAccess.IsDurableAgentKeyCredential(
                durableCredential.DurableCallerCredential);
        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
        if (usesDurableAgentKey && !credential.Found)
        {
            throw new InvalidOperationException(
                "The workflow Agent Key is unavailable for workflow tool execution.");
        }

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
            LlmControl: GetLlmControl(ctx, suppressSenderNyxIdAccessToken: usesDurableAgentKey),
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

    private static WorkflowLlmControlContext? GetLlmControl(
        IWorkflowExecutionContext ctx,
        bool suppressSenderNyxIdAccessToken)
    {
        var hasLlm = WorkflowRunExecutionContextStateAccess.TryGetLlm(ctx, out var llm);
        var senderToken = !suppressSenderNyxIdAccessToken &&
                          ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor
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

        var preparationStartedAtTimestamp = ctx.GetTimestamp();
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
                WorkflowStepFailureOutcome.CalleeConfirmed,
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
                WorkflowStepFailureOutcome.CalleeConfirmed,
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
                WorkflowStepFailureOutcome.CalleeConfirmed,
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
                WorkflowStepFailureOutcome.CalleeConfirmed,
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
                WorkflowStepFailureOutcome.CalleeConfirmed,
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
            pending.ContinuationId,
            checked(pending.Attempt + 1));
        await StartToolExecutionAsync(
            state,
            executionPending,
            tool,
            executionRequest,
            resumedRequest.ExternalInvocation?.ResponseProjection,
            preparationStartedAtTimestamp,
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

        if (result.PendingOperation is not null)
        {
            if (result.Failure is not null ||
                result.PendingApproval is not null ||
                result.ManagedHandoff is not null ||
                !string.IsNullOrEmpty(result.ResultJson))
            {
                logger.LogWarning(
                    "ToolCall: projected response returned an inconsistent pending operation run={RunId} step={StepId}",
                    runId,
                    stepId);
                return ProjectedToolFailure();
            }

            return result;
        }

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
        string continuationId,
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
            ContinuationId = NormalizeRequired(continuationId),
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

    private static async Task PersistPendingOperationAsync(
        ToolCallModuleState state,
        IWorkflowExecutionContext ctx,
        PendingToolCallExecutionState pendingExecution,
        ToolCallProtectedMaterial? material,
        WorkflowToolExecutionResult result,
        CancellationToken ct)
    {
        var request = ToStepRequest(pendingExecution, material);
        if (!IsValidPendingOperationResult(result))
        {
            await PersistAndPublishToolFailureAsync(
                state,
                ctx,
                request,
                pendingExecution.ToolName,
                "The tool returned an invalid durable pending-operation receipt.",
                "workflow_tool_pending_operation_invalid",
                string.Empty,
                pendingExecution.CallId,
                pendingExecution.ApprovalRequestId,
                pendingExecution.TerminalDecision,
                terminalInvoked: false,
                retryable: false,
                WorkflowStepFailureOutcome.CalleeConfirmed,
                pendingExecution.ProtectedMaterialReference,
                ct);
            return;
        }

        var operation = result.PendingOperation!;
        var pending = new PendingToolCallOperationState
        {
            RunId = pendingExecution.RunId,
            StepId = pendingExecution.StepId,
            ExecutionId = pendingExecution.ExecutionId,
            ToolName = pendingExecution.ToolName,
            ToolCallId = pendingExecution.CallId,
            IssuedAtUnixMs = pendingExecution.IssuedAtUnixMs,
            ApprovalRequestId = pendingExecution.ApprovalRequestId,
            TerminalDecision = pendingExecution.TerminalDecision,
            ExpiresAtUnixMs = ResolvePendingOperationDeadlineUnixMs(
                operation.ExpiresAtUnixMs,
                ctx.UtcNow),
            ProtectedMaterialReference = pendingExecution.ProtectedMaterialReference?.Clone(),
            ProtectedMaterialDigestSha256 = pendingExecution.ProtectedMaterialDigestSha256,
        };
        if (result.CancellationRecoveryIntent is not null)
        {
            pending.StopCancellationRecoveryIntent =
                ToCancellationTerminalIntent(result.CancellationRecoveryIntent);
        }
        UpdatePendingOperationReceipt(pending, operation, ctx.UtcNow);
        PrepareNextOperationPoll(pending, ctx.UtcNow);

        state.PendingExecutions.Remove(
            BuildExecutionKey(pendingExecution.CallId, pendingExecution.ExecutionId));
        state.PendingOperations[BuildCompletionKey(pending.ToolCallId, pending.ExecutionId)] = pending;
        await SaveStateAsync(state, ctx, ct);
        await TryScheduleOperationPollAsync(pending, ctx, ct);
    }

    private static bool IsValidPendingOperationResult(WorkflowToolExecutionResult result) =>
        result.PendingOperation is { } operation &&
        !string.IsNullOrWhiteSpace(operation.OperationId) &&
        !string.IsNullOrWhiteSpace(operation.ServiceSlug) &&
        HasValidProviderReceiptShape(operation) &&
        result.Failure == null &&
        result.PendingApproval == null &&
        result.ManagedHandoff == null &&
        string.IsNullOrEmpty(result.ResultJson);

    private static bool HasValidProviderReceiptShape(WorkflowToolPendingOperation operation)
    {
        if (HasProviderReceipt(operation))
            return true;

        var hasNoProviderReceipt =
            string.IsNullOrWhiteSpace(operation.ProviderOperationId) &&
            string.IsNullOrWhiteSpace(operation.StatusPath) &&
            string.IsNullOrWhiteSpace(operation.ResultPath) &&
            string.IsNullOrWhiteSpace(operation.CancelPath);
        return hasNoProviderReceipt &&
               operation.Status == WorkflowToolPendingOperationStatus.SubmissionUncertain;
    }

    private static bool HasProviderReceipt(WorkflowToolPendingOperation operation) =>
        !string.IsNullOrWhiteSpace(operation.ProviderOperationId) &&
        !string.IsNullOrWhiteSpace(operation.StatusPath) &&
        !string.IsNullOrWhiteSpace(operation.ResultPath) &&
        !string.IsNullOrWhiteSpace(operation.CancelPath);

    private static async Task ReschedulePendingOperationAsync(
        ToolCallModuleState state,
        PendingToolCallOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.ExpiresAtUnixMs > 0 &&
            pending.ExpiresAtUnixMs <= ctx.UtcNow.ToUnixTimeMilliseconds())
        {
            await CompletePendingOperationWithFailureAsync(
                state,
                BuildCompletionKey(pending.ToolCallId, pending.ExecutionId),
                pending,
                null,
                ctx,
                "workflow_tool_operation_reconciliation_expired",
                "The durable operation expired before its provider outcome could be reconciled.",
                ct,
                resolveMaterialIfMissing: false);
            return;
        }

        PrepareNextOperationPoll(pending, ctx.UtcNow);
        state.PendingOperations[BuildCompletionKey(pending.ToolCallId, pending.ExecutionId)] = pending;
        await SaveStateAsync(state, ctx, ct);
        await TryScheduleOperationPollAsync(pending, ctx, ct);
    }

    private static async Task TryScheduleOperationPollAsync(
        PendingToolCallOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        try
        {
            await ctx.ScheduleSelfDurableTimeoutAsync(
                pending.PollCallbackId,
                BuildOperationPollDelay(pending, ctx.UtcNow),
                BuildOperationPollEvent(pending),
                BuildOperationPollOptions(pending),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "Durable workflow tool poll scheduling failed; falling back to a typed self continuation. exceptionType={ExceptionType}",
                ex.GetType().Name);
            try
            {
                await ctx.PublishAsync(
                    BuildOperationPollEvent(pending),
                    TopologyAudience.Self,
                    ct,
                    BuildOperationPollOptions(pending));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception continuationException)
            {
                throw new WorkflowDurablePublicationPendingException(
                    "Durable workflow tool poll continuation remains pending.",
                    continuationException);
            }
        }
    }

    private static bool EnsureOperationPollPrepared(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow)
    {
        var changed = false;
        if (pending.PollAttempt <= 0)
        {
            pending.PollAttempt = 1;
            changed = true;
        }

        if (pending.NextPollUnixMs <= 0)
        {
            pending.NextPollUnixMs = AddPollDelay(
                utcNow,
                pending.RetryAfterMs,
                pending.ExpiresAtUnixMs);
            changed = true;
        }

        var callbackId = BuildOperationPollCallbackId(pending);
        if (!string.Equals(pending.PollCallbackId, callbackId, StringComparison.Ordinal))
        {
            pending.PollCallbackId = callbackId;
            changed = true;
        }

        return changed;
    }

    private static void PrepareNextOperationPoll(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow)
    {
        pending.PollAttempt = pending.PollAttempt == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, pending.PollAttempt) + 1;
        pending.NextPollUnixMs = AddPollDelay(
            utcNow,
            pending.RetryAfterMs,
            pending.ExpiresAtUnixMs);
        pending.PollCallbackId = BuildOperationPollCallbackId(pending);
    }

    private static bool EnsureStopCancellationPrepared(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow,
        long stopDeadlineUnixMs)
    {
        var changed = false;
        var expectedPhase = pending.StopCancellationTerminalIntent == null
            ? WorkflowToolStopCancellationPhase.Requested
            : WorkflowToolStopCancellationPhase.FinalizingAudit;
        if (pending.StopCancellationPhase != expectedPhase)
        {
            pending.StopCancellationPhase = expectedPhase;
            changed = true;
        }

        if (pending.StopCancellationAttempt <= 0)
        {
            pending.StopCancellationAttempt = 1;
            changed = true;
        }

        if (pending.NextStopCancellationUnixMs <= 0)
        {
            var nowUnixMs = utcNow.ToUnixTimeMilliseconds();
            pending.NextStopCancellationUnixMs = stopDeadlineUnixMs > 0
                ? Math.Min(nowUnixMs, stopDeadlineUnixMs)
                : nowUnixMs;
            changed = true;
        }

        var callbackId = BuildStopCancellationCallbackId(pending);
        if (!string.Equals(
                pending.StopCancellationCallbackId,
                callbackId,
                StringComparison.Ordinal))
        {
            pending.StopCancellationCallbackId = callbackId;
            changed = true;
        }

        return changed;
    }

    private static void PrepareNextStopCancellation(
        PendingToolCallOperationState pending,
        DateTimeOffset utcNow,
        long stopDeadlineUnixMs)
    {
        pending.StopCancellationPhase = pending.StopCancellationTerminalIntent == null
            ? WorkflowToolStopCancellationPhase.Requested
            : WorkflowToolStopCancellationPhase.FinalizingAudit;
        pending.StopCancellationAttempt = pending.StopCancellationAttempt == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, pending.StopCancellationAttempt) + 1;
        pending.NextStopCancellationUnixMs = AddStopCancellationDelay(
            utcNow,
            pending.RetryAfterMs,
            stopDeadlineUnixMs);
        pending.StopCancellationCallbackId = BuildStopCancellationCallbackId(pending);
    }

    private static long AddPollDelay(
        DateTimeOffset utcNow,
        long retryAfterMs,
        long expiresAtUnixMs)
    {
        var delayMs = retryAfterMs <= 0
            ? DefaultOperationPollDelayMs
            : Math.Min(retryAfterMs, MaxOperationPollDelayMs);
        var nowUnixMs = utcNow.ToUnixTimeMilliseconds();
        var nextPollUnixMs = nowUnixMs + delayMs;
        return expiresAtUnixMs > nowUnixMs
            ? Math.Min(nextPollUnixMs, expiresAtUnixMs)
            : nextPollUnixMs;
    }

    private static long AddStopCancellationDelay(
        DateTimeOffset utcNow,
        long retryAfterMs,
        long stopDeadlineUnixMs)
    {
        var delayMs = retryAfterMs <= 0
            ? DefaultStopCancellationDelayMs
            : Math.Min(retryAfterMs, MaxStopCancellationDelayMs);
        var nowUnixMs = utcNow.ToUnixTimeMilliseconds();
        var nextUnixMs = nowUnixMs + delayMs;
        return stopDeadlineUnixMs > nowUnixMs
            ? Math.Min(nextUnixMs, stopDeadlineUnixMs)
            : nextUnixMs;
    }

    private static string BuildOperationPollCallbackId(PendingToolCallOperationState pending) =>
        BuildOperationPollCallbackId(pending, pending.PollAttempt);

    private static string BuildOperationPollCallbackId(
        PendingToolCallOperationState pending,
        int pollAttempt) =>
        BuildOperationPollCallbackId(
            pending.RunId,
            pending.StepId,
            pending.ToolCallId,
            pending.ExecutionId,
            pending.OperationId,
            pollAttempt);

    private static string BuildOperationPollCallbackId(
        string runId,
        string stepId,
        string callId,
        string executionId,
        string operationId,
        int pollAttempt)
    {
        var identity = RuntimeCallbackKeyComposer.BuildKey(
            '\n',
            runId,
            stepId,
            callId,
            executionId,
            operationId,
            pollAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return RuntimeCallbackKeyComposer.BuildCallbackId(OperationPollCallbackPrefix, digest);
    }

    private static string BuildStopCancellationCallbackId(PendingToolCallOperationState pending) =>
        BuildStopCancellationCallbackId(pending, pending.StopCancellationAttempt);

    private static string BuildStopCancellationCallbackId(
        PendingToolCallOperationState pending,
        int stopCancellationAttempt) =>
        BuildStopCancellationCallbackId(
            pending.RunId,
            pending.StepId,
            pending.ToolCallId,
            pending.ExecutionId,
            pending.OperationId,
            stopCancellationAttempt);

    private static string BuildStopCancellationCallbackId(
        string runId,
        string stepId,
        string callId,
        string executionId,
        string operationId,
        int stopCancellationAttempt)
    {
        var identity = RuntimeCallbackKeyComposer.BuildKey(
            '\n',
            runId,
            stepId,
            callId,
            executionId,
            operationId,
            stopCancellationAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return RuntimeCallbackKeyComposer.BuildCallbackId(StopCancellationCallbackPrefix, digest);
    }

    private static bool MatchesPendingOperationPoll(
        PendingToolCallOperationState pending,
        WorkflowToolCallOperationPollFiredEvent poll) =>
        MatchesPendingOperationIdentity(
            pending,
            poll.RunId,
            poll.StepId,
            poll.ExecutionId,
            poll.CallId,
            poll.OperationId) &&
        pending.PollAttempt == poll.PollAttempt &&
        string.Equals(pending.PollCallbackId, NormalizeRequired(poll.CallbackId), StringComparison.Ordinal) &&
        string.Equals(pending.PollCallbackId, BuildOperationPollCallbackId(pending), StringComparison.Ordinal);

    private static bool MatchesPersistedOperationPoll(
        WorkflowToolCallCompletionOutboxEntry completion,
        WorkflowToolCallOperationPollFiredEvent poll) =>
        MatchesCallIdentity(
            completion.RunId,
            completion.StepId,
            completion.CallId,
            completion.ExecutionId,
            poll.RunId,
            poll.StepId,
            poll.CallId,
            poll.ExecutionId) &&
        !string.IsNullOrWhiteSpace(completion.OperationId) &&
        completion.OperationPollAttempt > 0 &&
        !string.IsNullOrWhiteSpace(completion.OperationPollCallbackId) &&
        string.Equals(
            completion.OperationId,
            NormalizeRequired(poll.OperationId),
            StringComparison.Ordinal) &&
        completion.OperationPollAttempt == poll.PollAttempt &&
        string.Equals(
            completion.OperationPollCallbackId,
            NormalizeRequired(poll.CallbackId),
            StringComparison.Ordinal) &&
        string.Equals(
            completion.OperationPollCallbackId,
            BuildOperationPollCallbackId(
                completion.RunId,
                completion.StepId,
                completion.CallId,
                completion.ExecutionId,
                completion.OperationId,
                completion.OperationPollAttempt),
            StringComparison.Ordinal);

    private static bool IsTrustedDurableSelfCallbackEnvelope(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        string? expectedCallbackId)
    {
        var callbackId = Normalize(expectedCallbackId);
        if (callbackId == null ||
            envelope.Route.GetTopologyAudience() != TopologyAudience.Self ||
            !string.Equals(envelope.Route?.PublisherActorId, ctx.AgentId, StringComparison.Ordinal))
        {
            return false;
        }

        var deliveryOperationId = Normalize(envelope.Runtime?.DeliveryIdentity?.OperationId);
        var hasCallbackIdentity = RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callback);
        if (deliveryOperationId == null && !hasCallbackIdentity)
            return false;

        return (deliveryOperationId == null ||
                string.Equals(deliveryOperationId, callbackId, StringComparison.Ordinal)) &&
               (!hasCallbackIdentity ||
                string.Equals(callback.CallbackId, callbackId, StringComparison.Ordinal));
    }

    private static bool MatchesRecoverableEarlierOperationPoll(
        PendingToolCallOperationState pending,
        WorkflowToolCallOperationPollFiredEvent poll) =>
        MatchesPendingOperationIdentity(
            pending,
            poll.RunId,
            poll.StepId,
            poll.ExecutionId,
            poll.CallId,
            poll.OperationId) &&
        poll.PollAttempt > 0 &&
        poll.PollAttempt < pending.PollAttempt &&
        string.Equals(
            NormalizeRequired(poll.CallbackId),
            BuildOperationPollCallbackId(pending, poll.PollAttempt),
            StringComparison.Ordinal) &&
        string.Equals(pending.PollCallbackId, BuildOperationPollCallbackId(pending), StringComparison.Ordinal);

    private static bool MatchesPendingOperationIdentity(
        PendingToolCallOperationState pending,
        string? runId,
        string? stepId,
        string? executionId,
        string? callId,
        string? operationId) =>
        string.Equals(pending.RunId, NormalizeRequired(runId), StringComparison.Ordinal) &&
        string.Equals(pending.StepId, NormalizeRequired(stepId), StringComparison.Ordinal) &&
        string.Equals(pending.ExecutionId, NormalizeRequired(executionId), StringComparison.Ordinal) &&
        string.Equals(pending.ToolCallId, NormalizeRequired(callId), StringComparison.Ordinal) &&
        string.Equals(pending.OperationId, NormalizeRequired(operationId), StringComparison.Ordinal);

    private static bool MatchesStopCancellation(
        PendingToolCallOperationState pending,
        WorkflowToolCallStopCancellationFiredEvent fired) =>
        (pending.StopCancellationPhase is WorkflowToolStopCancellationPhase.Requested or
            WorkflowToolStopCancellationPhase.FinalizingAudit) &&
        MatchesPendingOperationIdentity(
            pending,
            fired.RunId,
            fired.StepId,
            fired.ExecutionId,
            fired.CallId,
            fired.OperationId) &&
        pending.StopCancellationAttempt == fired.Attempt &&
        string.Equals(
            pending.StopCancellationCallbackId,
            NormalizeRequired(fired.CallbackId),
            StringComparison.Ordinal) &&
        string.Equals(
            pending.StopCancellationCallbackId,
            BuildStopCancellationCallbackId(pending),
            StringComparison.Ordinal);

    private static bool MatchesRecoverableEarlierStopCancellation(
        PendingToolCallOperationState pending,
        WorkflowToolCallStopCancellationFiredEvent fired) =>
        (pending.StopCancellationPhase is WorkflowToolStopCancellationPhase.Requested or
            WorkflowToolStopCancellationPhase.FinalizingAudit) &&
        MatchesPendingOperationIdentity(
            pending,
            fired.RunId,
            fired.StepId,
            fired.ExecutionId,
            fired.CallId,
            fired.OperationId) &&
        fired.Attempt > 0 &&
        fired.Attempt < pending.StopCancellationAttempt &&
        string.Equals(
            NormalizeRequired(fired.CallbackId),
            BuildStopCancellationCallbackId(pending, fired.Attempt),
            StringComparison.Ordinal) &&
        string.Equals(
            pending.StopCancellationCallbackId,
            BuildStopCancellationCallbackId(pending),
            StringComparison.Ordinal);

    private static bool MatchesPendingOperationCall(
        PendingToolCallOperationState pending,
        StepRequestEvent request,
        string callId) =>
        MatchesCallIdentity(
            pending.RunId,
            pending.StepId,
            pending.ToolCallId,
            pending.ExecutionId,
            request.RunId,
            request.StepId,
            callId,
            request.ExecutionId) &&
        string.Equals(
            pending.ToolName,
            NormalizeRequired(request.Parameters.GetValueOrDefault("tool", string.Empty)),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesPendingOperationReceiptIdentity(
        PendingToolCallOperationState pending,
        WorkflowToolPendingOperation operation)
    {
        if (!string.Equals(pending.OperationId, NormalizeRequired(operation.OperationId), StringComparison.Ordinal) ||
            !HasValidProviderReceiptShape(operation))
        {
            return false;
        }

        var pendingHasProviderReceipt =
            !string.IsNullOrEmpty(pending.ProviderOperationId) &&
            !string.IsNullOrEmpty(pending.StatusPath) &&
            !string.IsNullOrEmpty(pending.ResultPath) &&
            !string.IsNullOrEmpty(pending.CancelPath);
        var pendingHasNoProviderReceipt =
            string.IsNullOrEmpty(pending.ProviderOperationId) &&
            string.IsNullOrEmpty(pending.StatusPath) &&
            string.IsNullOrEmpty(pending.ResultPath) &&
            string.IsNullOrEmpty(pending.CancelPath);
        if (!pendingHasProviderReceipt && !pendingHasNoProviderReceipt)
            return false;

        var routeMatches =
            string.Equals(pending.ServiceSlug, NormalizeRequired(operation.ServiceSlug), StringComparison.Ordinal) &&
            string.Equals(pending.UserServiceId, NormalizeRequired(operation.UserServiceId), StringComparison.Ordinal) &&
            pending.RouteIdentitySource == operation.RouteIdentitySource;
        if (!routeMatches && !IsAllowedCodeExecutionRouteRefinement(pending, operation, pendingHasNoProviderReceipt))
            return false;

        if (pendingHasNoProviderReceipt)
            return true;

        return string.Equals(pending.ProviderOperationId, NormalizeRequired(operation.ProviderOperationId), StringComparison.Ordinal) &&
               string.Equals(pending.StatusPath, NormalizeRequired(operation.StatusPath), StringComparison.Ordinal) &&
               string.Equals(pending.ResultPath, NormalizeRequired(operation.ResultPath), StringComparison.Ordinal) &&
               string.Equals(pending.CancelPath, NormalizeRequired(operation.CancelPath), StringComparison.Ordinal);
    }

    private static bool IsAllowedCodeExecutionRouteRefinement(
        PendingToolCallOperationState pending,
        WorkflowToolPendingOperation operation,
        bool pendingHasNoProviderReceipt) =>
        pendingHasNoProviderReceipt &&
        pending.Status == WorkflowToolPendingOperationStatus.SubmissionUncertain &&
        string.Equals(
            pending.ToolName,
            WorkflowAuthorizationDependencyEvaluator.CodeExecuteToolName,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(pending.ServiceSlug, CodeExecutionContract.ServiceSlug, StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(pending.UserServiceId) &&
        pending.RouteIdentitySource == WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract &&
        HasProviderReceipt(operation) &&
        CodeExecutionContract.IsSupportedServiceSlug(operation.ServiceSlug) &&
        operation.RouteIdentitySource == WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog &&
        !string.IsNullOrWhiteSpace(operation.UserServiceId);

    private static void UpdatePendingOperationReceipt(
        PendingToolCallOperationState pending,
        WorkflowToolPendingOperation operation,
        DateTimeOffset receiptAcceptedAt)
    {
        var existingExpiresAtUnixMs = pending.ExpiresAtUnixMs;
        var hadProviderReceipt = !string.IsNullOrWhiteSpace(pending.ProviderOperationId);
        var receivesProviderReceipt = !string.IsNullOrWhiteSpace(operation.ProviderOperationId);
        pending.OperationId = NormalizeRequired(operation.OperationId);
        pending.ProviderOperationId = NormalizeRequired(operation.ProviderOperationId);
        pending.StatusPath = NormalizeRequired(operation.StatusPath);
        pending.ResultPath = NormalizeRequired(operation.ResultPath);
        pending.CancelPath = NormalizeRequired(operation.CancelPath);
        pending.Status = operation.Status;
        pending.Etag = NormalizeRequired(operation.ETag);
        pending.RetryAfterMs = operation.RetryAfterMilliseconds;
        pending.ExpiresAtUnixMs = !hadProviderReceipt && receivesProviderReceipt
            ? ResolvePendingOperationDeadlineUnixMs(
                operation.ExpiresAtUnixMs,
                receiptAcceptedAt)
            : MinPositiveDeadline(existingExpiresAtUnixMs, operation.ExpiresAtUnixMs);
        pending.ServiceSlug = NormalizeRequired(operation.ServiceSlug);
        pending.UserServiceId = NormalizeRequired(operation.UserServiceId);
        pending.RouteIdentitySource = operation.RouteIdentitySource;
    }

    private static long MinPositiveDeadline(long currentUnixMs, long incomingUnixMs)
    {
        if (currentUnixMs <= 0)
            return incomingUnixMs;
        if (incomingUnixMs <= 0)
            return currentUnixMs;
        return Math.Min(currentUnixMs, incomingUnixMs);
    }

    internal static long ResolvePendingOperationDeadlineUnixMs(
        long providerExpiresAtUnixMs,
        DateTimeOffset receiptAcceptedAt)
    {
        if (providerExpiresAtUnixMs > 0)
            return providerExpiresAtUnixMs;

        // A missing provider expiry must remain finite while still covering the admitted
        // 600-second execution plus bounded submit/status/result reconciliation.
        return checked(
            receiptAcceptedAt.ToUnixTimeMilliseconds() + DurableOperationFallbackTimeoutMs);
    }

    private static bool IsValidCancellationTerminalIntent(
        WorkflowToolCancellationTerminalAuditIntent intent) =>
        intent.Result.PendingOperation == null &&
        intent.Result.PendingApproval == null &&
        intent.Result.ManagedHandoff == null &&
        IsSha256Digest(intent.ArgumentsSha256);

    private static WorkflowToolCancellationTerminalIntent ToCancellationTerminalIntent(
        WorkflowToolCancellationTerminalAuditIntent intent)
    {
        var result = intent.Result;
        var failure = result.Failure;
        var persisted = new WorkflowToolCancellationTerminalIntent
        {
            ResultJson = result.ResultJson ?? string.Empty,
            HasFailure = failure != null,
            FailureCode = failure?.ErrorCode ?? string.Empty,
            SafeMessage = failure?.ErrorMessage ?? string.Empty,
            TerminalInvoked = failure?.TerminalInvoked ?? true,
            Retryable = failure?.Retryable ?? false,
            FailureOutcome = failure?.FailureOutcome ?? WorkflowStepFailureOutcome.Unspecified,
            ArgumentsSha256 = intent.ArgumentsSha256,
        };
        if (intent.ToolOwnedAuditIntent != null)
            persisted.ToolOwnedAuditIntent = intent.ToolOwnedAuditIntent.Clone();
        return persisted;
    }

    private static WorkflowToolCancellationTerminalAuditIntent? FromCancellationTerminalIntent(
        WorkflowToolCancellationTerminalIntent? intent)
    {
        if (intent == null)
            return null;

        var result = intent.HasFailure
            ? WorkflowToolExecutionResult.Failed(
                intent.ResultJson,
                intent.FailureCode,
                intent.SafeMessage,
                intent.TerminalInvoked,
                intent.Retryable,
                NormalizeFailureOutcome(intent.FailureOutcome, intent.FailureCode))
            : WorkflowToolExecutionResult.Success(intent.ResultJson);
        return new WorkflowToolCancellationTerminalAuditIntent(
            result,
            intent.ToolOwnedAuditIntent?.Clone(),
            intent.ArgumentsSha256);
    }

    private static bool MatchesCancellationTerminalIntent(
        WorkflowToolCancellationTerminalIntent left,
        WorkflowToolCancellationTerminalIntent right) =>
        string.Equals(left.ResultJson, right.ResultJson, StringComparison.Ordinal) &&
        left.HasFailure == right.HasFailure &&
        string.Equals(left.FailureCode, right.FailureCode, StringComparison.Ordinal) &&
        string.Equals(left.SafeMessage, right.SafeMessage, StringComparison.Ordinal) &&
        left.TerminalInvoked == right.TerminalInvoked &&
        left.Retryable == right.Retryable &&
        left.FailureOutcome == right.FailureOutcome &&
        string.Equals(left.ArgumentsSha256, right.ArgumentsSha256, StringComparison.Ordinal) &&
        Equals(left.ToolOwnedAuditIntent, right.ToolOwnedAuditIntent);

    private static bool MatchesCancellationTerminalResult(
        WorkflowToolCancellationTerminalIntent intent,
        WorkflowToolExecutionResult result)
    {
        var failure = result.Failure;
        return string.Equals(intent.ResultJson, result.ResultJson, StringComparison.Ordinal) &&
               intent.HasFailure == (failure != null) &&
               string.Equals(intent.FailureCode, failure?.ErrorCode ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(intent.SafeMessage, failure?.ErrorMessage ?? string.Empty, StringComparison.Ordinal) &&
               intent.TerminalInvoked == (failure?.TerminalInvoked ?? true) &&
               intent.Retryable == (failure?.Retryable ?? false) &&
               NormalizeFailureOutcome(intent.FailureOutcome, intent.FailureCode) ==
                   (failure?.FailureOutcome ?? WorkflowStepFailureOutcome.Unspecified) &&
               result.PendingOperation == null &&
               result.PendingApproval == null &&
               result.ManagedHandoff == null;
    }

    private static WorkflowToolPendingOperation ToPendingOperation(
        PendingToolCallOperationState pending) =>
        new(
            pending.OperationId,
            pending.ProviderOperationId,
            pending.StatusPath,
            pending.ResultPath,
            pending.CancelPath,
            pending.Status,
            Normalize(pending.Etag),
            pending.RetryAfterMs,
            pending.ExpiresAtUnixMs,
            pending.ServiceSlug,
            Normalize(pending.UserServiceId),
            pending.RouteIdentitySource);

    private static async Task CompletePendingOperationWithFailureAsync(
        ToolCallModuleState state,
        string operationKey,
        PendingToolCallOperationState pending,
        ToolCallProtectedMaterial? material,
        IWorkflowExecutionContext ctx,
        string errorCode,
        string errorMessage,
        CancellationToken ct,
        bool resolveMaterialIfMissing = true)
    {
        if (material is null && resolveMaterialIfMissing)
        {
            var resolution = await ResolveAndVerifyProtectedMaterialAsync(
                pending.ProtectedMaterialReference,
                pending.ProtectedMaterialDigestSha256,
                pending.RunId,
                pending.StepId,
                pending.ExecutionId,
                pending.ToolCallId,
                ctx,
                ct);
            material = resolution.Material;
        }

        state.PendingOperations.Remove(operationKey);
        await PersistAndPublishToolOutcomeAsync(
            state,
            ctx,
            ToStepRequest(pending, material),
            pending.ToolName,
            pending.ToolCallId,
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                errorCode,
                errorMessage,
                terminalInvoked: true,
                retryable: false,
                WorkflowStepFailureOutcome.OutcomeUncertain),
            pending.ApprovalRequestId,
            pending.TerminalDecision == WorkflowToolCallTerminalDecision.Unspecified
                ? WorkflowToolCallTerminalDecision.NoApproval
                : pending.TerminalDecision,
            pending.ProtectedMaterialReference,
            pending,
            ct);
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
        => PersistAndPublishToolOutcomeAsync(
            state,
            ctx,
            request,
            toolName,
            callId,
            result,
            approvalRequestId,
            terminalDecision,
            protectedMaterialReference,
            operationPollProvenance: null,
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
        PendingToolCallOperationState? operationPollProvenance,
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
                result.Failure.FailureOutcome,
                protectedMaterialReference,
                operationPollProvenance,
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
            OperationId = operationPollProvenance?.OperationId ?? string.Empty,
            OperationPollAttempt = operationPollProvenance?.PollAttempt ?? 0,
            OperationPollCallbackId = operationPollProvenance?.PollCallbackId ?? string.Empty,
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
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
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
        if (pending != null)
        {
            await TrySchedulePublicationRecoveryAsync(ctx, BuildSuspensionRetry(pending), ct);
            await PublishPendingSuspensionAsync(state, pending, ctx, ct);
            return true;
        }

        var operationKey = BuildCompletionKey(callId, request.ExecutionId);
        if (!state.PendingOperations.TryGetValue(operationKey, out var pendingOperation))
            return false;

        if (!MatchesPendingOperationCall(pendingOperation, request, callId))
        {
            await CompletePendingOperationWithFailureAsync(
                state,
                operationKey,
                pendingOperation,
                null,
                ctx,
                "workflow_tool_operation_identity_mismatch",
                "The durable tool operation no longer matches its workflow call identity.",
                ct);
            return true;
        }

        await TryScheduleOperationPollAsync(pendingOperation, ctx, ct);
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

    private static async Task TryDrainPersistedOperationPollCompletionAsync(
        ToolCallModuleState state,
        WorkflowToolCallOperationPollFiredEvent poll,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completion = FindCompletion(
            state,
            poll.RunId,
            poll.StepId,
            poll.CallId,
            poll.ExecutionId);
        if (completion == null ||
            !MatchesPersistedOperationPoll(completion, poll) ||
            !IsTrustedDurableSelfCallbackEnvelope(
                envelope,
                ctx,
                completion.OperationPollCallbackId))
        {
            return;
        }

        await TrySchedulePublicationRecoveryAsync(ctx, BuildCompletionRetry(completion), ct);
        await PublishUnpublishedCompletionEventsAsync(state, completion, ctx, ct);
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
        if (pending != null)
        {
            await TrySchedulePublicationRecoveryAsync(ctx, BuildSuspensionRetry(pending), ct);
            await PublishPendingSuspensionAsync(state, pending, ctx, ct);
            return true;
        }

        var operationKey = BuildCompletionKey(callId, executionId);
        if (!state.PendingOperations.TryGetValue(operationKey, out var operation) ||
            !MatchesCallIdentity(
                operation.RunId,
                operation.StepId,
                operation.ToolCallId,
                operation.ExecutionId,
                runId,
                stepId,
                callId,
                executionId))
        {
            return false;
        }

        await TryScheduleOperationPollAsync(operation, ctx, ct);
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

        if (await TryDrainPersistedCompletionAsync(
                state,
                resumed.RunId,
                resumed.StepId,
                resumed.ToolApproval.ToolCallId,
                resumed.ToolApproval.ExecutionId,
                ctx,
                ct,
                resumed.ToolApproval.ApprovalRequestId,
                decision))
        {
            return true;
        }

        var operationKey = BuildCompletionKey(
            resumed.ToolApproval.ToolCallId,
            resumed.ToolApproval.ExecutionId);
        if (!state.PendingOperations.TryGetValue(operationKey, out var pendingOperation) ||
            !MatchesCallIdentity(
                pendingOperation.RunId,
                pendingOperation.StepId,
                pendingOperation.ToolCallId,
                pendingOperation.ExecutionId,
                resumed.RunId,
                resumed.StepId,
                resumed.ToolApproval.ToolCallId,
                resumed.ToolApproval.ExecutionId) ||
            !string.Equals(
                pendingOperation.ApprovalRequestId,
                NormalizeRequired(resumed.ToolApproval.ApprovalRequestId),
                StringComparison.Ordinal) ||
            pendingOperation.TerminalDecision != decision)
        {
            return false;
        }

        await TryScheduleOperationPollAsync(pendingOperation, ctx, ct);
        return true;
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
        if (!CanMergeCompletionCheckpoints(completion))
        {
            await PublishCompletionEventsWithLegacyCheckpointsAsync(state, completion, ctx, ct);
            return;
        }

        if (completion.ToolCompletion != null && !completion.ToolCompletionPublished)
        {
            await ExecuteDurablePublicationAsync(
                () => ctx.PublishAsync(
                    completion.ToolCompletion.Clone(),
                    TopologyAudience.Self,
                    ct,
                    BuildPublicationOptions("workflow-tool-call-completed", completion)),
                "tool completion",
                ct);
        }

        if (completion.StepCompletion != null && !completion.StepCompletionPublished)
        {
            await ExecuteDurablePublicationAsync(
                () => ctx.PublishAsync(
                    completion.StepCompletion.Clone(),
                    TopologyAudience.Self,
                    ct,
                    BuildPublicationOptions("workflow-tool-step-completed", completion)),
                "step completion",
                ct);
        }

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
                },
                "protected tool-call material cleanup",
                ct);
        }

        // Step-less outcomes have no StepCompleted fact to duplicate. Typed step completions are
        // deduplicated by the WorkflowRun consumer, so one final checkpoint can atomically replace
        // the outbox entry with its terminal tombstone after publication and cleanup succeed.
        await ExecuteDurablePublicationAsync(
            () => CompressCompletionToTombstoneAsync(state, completion, ctx, ct),
            "completion tombstone checkpoint",
            ct);
    }

    private static bool CanMergeCompletionCheckpoints(
        WorkflowToolCallCompletionOutboxEntry completion)
    {
        var stepCompletion = completion.StepCompletion;
        if (stepCompletion == null)
            return true;

        return !string.IsNullOrWhiteSpace(completion.RunId) &&
               !string.IsNullOrWhiteSpace(completion.StepId) &&
               !string.IsNullOrWhiteSpace(completion.ExecutionId) &&
               string.Equals(
                   NormalizeRequired(completion.RunId),
                   NormalizeRequired(stepCompletion.RunId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   NormalizeRequired(completion.StepId),
                   NormalizeRequired(stepCompletion.StepId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   NormalizeRequired(completion.ExecutionId),
                   NormalizeRequired(stepCompletion.ExecutionId),
                   StringComparison.Ordinal);
    }

    private static async Task PublishCompletionEventsWithLegacyCheckpointsAsync(
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
            failureOutcome: WorkflowStepFailureOutcome.CalleeConfirmed,
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
            failureOutcome: WorkflowStepFailureOutcome.CalleeConfirmed,
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
        WorkflowStepFailureOutcome failureOutcome,
        RuntimeSecretReference? protectedMaterialReference,
        CancellationToken ct)
        => PersistAndPublishToolFailureAsync(
            state,
            ctx,
            request,
            toolName,
            error,
            errorCode,
            resultJson,
            callId,
            approvalRequestId,
            terminalDecision,
            terminalInvoked,
            retryable,
            failureOutcome,
            protectedMaterialReference,
            operationPollProvenance: null,
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
        bool terminalInvoked,
        bool retryable,
        WorkflowStepFailureOutcome failureOutcome,
        RuntimeSecretReference? protectedMaterialReference,
        PendingToolCallOperationState? operationPollProvenance,
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
            OperationId = operationPollProvenance?.OperationId ?? string.Empty,
            OperationPollAttempt = operationPollProvenance?.PollAttempt ?? 0,
            OperationPollCallbackId = operationPollProvenance?.PollCallbackId ?? string.Empty,
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
                FailureOutcome = NormalizeFailureOutcome(failureOutcome, errorCode),
                RetryDisposition = !terminalInvoked && retryable
                    ? WorkflowStepRetryDisposition.Allowed
                    : WorkflowStepRetryDisposition.Forbidden,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
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

    private static StepRequestEvent ToStepRequest(
        PendingToolCallOperationState pending,
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

    private static bool IsSha256Digest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
            state.CompletionTombstones.Count == 0 &&
            state.PendingOperations.Count == 0 &&
            state.StopCancellation == null)
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

    private void InvalidateToolIndex() => Interlocked.Exchange(ref _toolIndex, null);

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

    private sealed class WorkflowToolStopCancellationPendingException(string message)
        : Exception(message), IRuntimeEnvelopeRetryableException;

    private sealed class WorkflowToolProtectedMaterialResolutionPendingException(string errorCode)
        : Exception($"Protected workflow tool execution material resolution remains pending: {errorCode}."),
            IRuntimeEnvelopeRetryableException;

}
