using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ToolCallModule
{
    internal sealed record PendingExecutionWatchdogRecovery(
        string PendingKey,
        string CallbackId,
        TimeSpan DueTime,
        WorkflowToolCallTimeoutFiredEvent Timeout,
        EventEnvelopePublishOptions Options);

    internal sealed record PendingExecutionRetryRecovery(
        string PendingKey,
        string CallbackId,
        TimeSpan DueTime,
        WorkflowToolCallRetryFiredEvent Retry,
        EventEnvelopePublishOptions Options);

    internal sealed record PendingApprovalWatchdogRecovery(
        string PendingKey,
        string CallbackId,
        WorkflowRuntimeCallbackLeaseState? ExpectedTimeoutLease,
        TimeSpan DueTime,
        WorkflowToolCallTimeoutFiredEvent Timeout,
        EventEnvelopePublishOptions Options);

    internal sealed record PendingExecutionRecovery(
        string PendingKey,
        string CallbackId,
        WorkflowToolCallExecutionRecoveryFiredEvent Recovery,
        EventEnvelopePublishOptions Options);

    internal static IReadOnlyList<PendingExecutionWatchdogRecovery> BuildPendingExecutionWatchdogRecoveries(
        ToolCallModuleState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        return state.PendingExecutions
            .Where(static item =>
                item.Value.TimeoutLease == null ||
                item.Value.TimeoutLease.Backend == WorkflowRuntimeCallbackBackendState.InMemory)
            .Select(item =>
            {
                var pending = item.Value;
                var remainingMs = Math.Max(1, pending.TimeoutDeadlineUnixMs - nowUnixMs);
                return new PendingExecutionWatchdogRecovery(
                    item.Key,
                    pending.TimeoutCallbackId,
                    TimeSpan.FromMilliseconds(remainingMs),
                    BuildTimeoutEvent(pending),
                    BuildExecutionCallbackOptions(pending.TimeoutCallbackId));
            })
            .ToList();
    }

    internal static bool MatchesPendingExecution(
        PendingToolCallExecutionState pending,
        PendingExecutionWatchdogRecovery recovery) =>
        string.Equals(BuildExecutionKey(pending.CallId, pending.ExecutionId), recovery.PendingKey, StringComparison.Ordinal) &&
        string.Equals(pending.TimeoutCallbackId, recovery.CallbackId, StringComparison.Ordinal) &&
        MatchesCallIdentity(
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            recovery.Timeout.RunId,
            recovery.Timeout.StepId,
            recovery.Timeout.CallId,
            recovery.Timeout.ExecutionId) &&
        MatchesContinuationId(pending, recovery.Timeout.ContinuationId);

    internal static IReadOnlyList<PendingApprovalWatchdogRecovery> BuildPendingApprovalWatchdogRecoveries(
        ToolCallModuleState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        return state.PendingApprovals
            .Where(static item =>
                (item.Value.ExecutionPhase == WorkflowToolCallExecutionPhase.Unspecified ||
                 item.Value.ExecutionPhase == WorkflowToolCallExecutionPhase.ApprovalPending) &&
                (item.Value.TimeoutLease == null ||
                 item.Value.TimeoutLease.Backend == WorkflowRuntimeCallbackBackendState.InMemory))
            .Select(item =>
            {
                var pending = item.Value;
                var callbackId = string.IsNullOrWhiteSpace(pending.TimeoutCallbackId)
                    ? BuildToolTimeoutCallbackId(pending)
                    : pending.TimeoutCallbackId;
                var remainingMs = Math.Max(1, pending.TimeoutDeadlineUnixMs - nowUnixMs);
                return new PendingApprovalWatchdogRecovery(
                    item.Key,
                    callbackId,
                    pending.TimeoutLease?.Clone(),
                    TimeSpan.FromMilliseconds(remainingMs),
                    BuildTimeoutEvent(pending),
                    BuildExecutionCallbackOptions(callbackId));
            })
            .ToList();
    }

    internal static bool MatchesPendingApproval(
        PendingToolCallApprovalState pending,
        PendingApprovalWatchdogRecovery recovery)
    {
        var callbackId = string.IsNullOrWhiteSpace(pending.TimeoutCallbackId)
            ? BuildToolTimeoutCallbackId(pending)
            : pending.TimeoutCallbackId;
        return string.Equals(BuildPendingKey(pending), recovery.PendingKey, StringComparison.Ordinal) &&
               (pending.ExecutionPhase == WorkflowToolCallExecutionPhase.Unspecified ||
                pending.ExecutionPhase == WorkflowToolCallExecutionPhase.ApprovalPending) &&
               string.Equals(callbackId, recovery.CallbackId, StringComparison.Ordinal) &&
               Equals(pending.TimeoutLease, recovery.ExpectedTimeoutLease) &&
               MatchesCallIdentity(
                   pending.RunId,
                   pending.StepId,
                   pending.ToolCallId,
                   pending.ExecutionId,
                   recovery.Timeout.RunId,
                   recovery.Timeout.StepId,
                   recovery.Timeout.CallId,
                   recovery.Timeout.ExecutionId) &&
               string.Equals(pending.ContinuationId, recovery.Timeout.ContinuationId, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<PendingExecutionRetryRecovery> BuildPendingExecutionRetryRecoveries(
        ToolCallModuleState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        return state.PendingExecutions
            .Where(static item =>
                item.Value.ExecutionPhase == WorkflowToolCallExecutionPhase.RetryPending &&
                !string.IsNullOrWhiteSpace(item.Value.RetryCallbackId) &&
                (item.Value.RetryLease == null ||
                 item.Value.RetryLease.Backend == WorkflowRuntimeCallbackBackendState.InMemory))
            .Select(item =>
            {
                var pending = item.Value;
                var remainingMs = Math.Max(1, pending.RetryDueUnixMs - nowUnixMs);
                return new PendingExecutionRetryRecovery(
                    item.Key,
                    pending.RetryCallbackId,
                    TimeSpan.FromMilliseconds(remainingMs),
                    BuildRetryEvent(pending),
                    BuildExecutionCallbackOptions(pending.RetryCallbackId));
            })
            .ToList();
    }

    internal static IReadOnlyList<PendingExecutionRecovery> BuildPendingExecutionRecoveries(
        ToolCallModuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingExecutions
            .Where(static item =>
                item.Value.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending)
            .Select(item =>
            {
                var pending = item.Value;
                var callbackId = BuildToolExecutionRecoveryCallbackId(pending);
                return new PendingExecutionRecovery(
                    item.Key,
                    callbackId,
                    BuildExecutionRecoveryEvent(pending),
                    BuildExecutionCallbackOptions(callbackId));
            })
            .ToList();
    }

    internal static bool MatchesPendingExecution(
        PendingToolCallExecutionState pending,
        PendingExecutionRecovery recovery) =>
        string.Equals(BuildExecutionKey(pending.CallId, pending.ExecutionId), recovery.PendingKey, StringComparison.Ordinal) &&
        pending.Attempt == recovery.Recovery.Attempt &&
        MatchesExecutionIdentity(
            pending,
            recovery.Recovery.RunId,
            recovery.Recovery.StepId,
            recovery.Recovery.CallId,
            recovery.Recovery.ExecutionId) &&
        MatchesContinuationId(pending, recovery.Recovery.ContinuationId);

    private static async Task EnsureExecutionRecoveryWakeupAsync(
        PendingToolCallExecutionState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var callbackId = BuildToolExecutionRecoveryCallbackId(pending);
        var recovery = BuildExecutionRecoveryEvent(pending);
        var options = BuildExecutionCallbackOptions(callbackId);
        Exception? scheduleFailure = null;
        try
        {
            await ctx.ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromMilliseconds(1),
                recovery,
                options,
                ct);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            scheduleFailure = exception;
            ctx.Logger.LogWarning(
                exception,
                "ToolCall: pending execution recovery scheduling failed run={RunId} step={StepId} attempt={Attempt} failure_type={FailureType}; attempting self continuation",
                pending.RunId,
                pending.StepId,
                pending.Attempt,
                exception.GetType().Name);
        }

        try
        {
            await ctx.PublishAsync(
                recovery,
                TopologyAudience.Self,
                ct,
                options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception publishFailure)
        {
            throw new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
                "Pending tool execution has no confirmed durable recovery wakeup.",
                new AggregateException(scheduleFailure!, publishFailure));
        }
    }

    private async Task<bool> EnsurePendingExecutionWatchdogAsync(
        PendingToolCallExecutionState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (pending.TimeoutLease != null)
            return true;

        var reconciliationStartedAtTimestamp = ctx.GetTimestamp();
        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: false,
                reconciliationStartedAtTimestamp,
                ctx,
                ct);
            return false;
        }

        RuntimeCallbackLease lease;
        try
        {
            var remainingMs = Math.Max(
                1,
                pending.TimeoutDeadlineUnixMs - ctx.UtcNow.ToUnixTimeMilliseconds());
            lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                pending.TimeoutCallbackId,
                TimeSpan.FromMilliseconds(remainingMs),
                BuildTimeoutEvent(pending),
                BuildExecutionCallbackOptions(pending.TimeoutCallbackId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ctx.Logger.LogWarning(
                exception,
                "ToolCall: pending execution watchdog recovery failed run={RunId} step={StepId} failure_type={FailureType}",
                pending.RunId,
                pending.StepId,
                exception.GetType().Name);
            await FailBeforeToolDispatchAsync(pending, reconciliationStartedAtTimestamp, ctx, ct);
            return false;
        }

        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(pending.CallId, pending.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var persistedPending) ||
            !MatchesExecutionIdentity(
                persistedPending,
                pending.RunId,
                pending.StepId,
                pending.CallId,
                pending.ExecutionId) ||
            persistedPending.Attempt != pending.Attempt ||
            !MatchesContinuationId(persistedPending, pending.ContinuationId))
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                lease,
                "orphaned recovered tool execution watchdog",
                ct);
            return false;
        }

        persistedPending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        state.PendingExecutions[pendingKey] = persistedPending;
        await SaveStateAsync(state, ctx, ct);
        return true;
    }

    internal static bool MatchesPendingExecution(
        PendingToolCallExecutionState pending,
        PendingExecutionRetryRecovery recovery) =>
        string.Equals(BuildExecutionKey(pending.CallId, pending.ExecutionId), recovery.PendingKey, StringComparison.Ordinal) &&
        string.Equals(pending.RetryCallbackId, recovery.CallbackId, StringComparison.Ordinal) &&
        pending.Attempt == recovery.Retry.Attempt &&
        MatchesExecutionIdentity(
            pending,
            recovery.Retry.RunId,
            recovery.Retry.StepId,
            recovery.Retry.CallId,
            recovery.Retry.ExecutionId) &&
        MatchesContinuationId(pending, recovery.Retry.ContinuationId);

    private static PendingToolCallExecutionState BuildPendingExecution(
        StepRequestEvent request,
        string toolName,
        string callId,
        long issuedAtUnixMs,
        int timeoutMs,
        DateTimeOffset now,
        string approvalRequestId,
        WorkflowToolCallTerminalDecision terminalDecision,
        RuntimeSecretReference protectedMaterialReference,
        string protectedMaterialDigestSha256,
        long absoluteTimeoutDeadlineUnixMs = 0,
        string continuationId = "",
        int initialAttempt = 1)
    {
        var timeoutDeadlineUnixMs = absoluteTimeoutDeadlineUnixMs > 0
            ? absoluteTimeoutDeadlineUnixMs
            : checked(now.ToUnixTimeMilliseconds() + timeoutMs);
        var pending = new PendingToolCallExecutionState
        {
            RunId = NormalizeRequired(request.RunId),
            StepId = NormalizeRequired(request.StepId),
            ExecutionId = NormalizeRequired(request.ExecutionId),
            ToolName = NormalizeRequired(toolName),
            CallId = NormalizeRequired(callId),
            IssuedAtUnixMs = issuedAtUnixMs,
            TimeoutMs = timeoutMs,
            TimeoutDeadlineUnixMs = timeoutDeadlineUnixMs,
            ApprovalRequestId = NormalizeRequired(approvalRequestId),
            TerminalDecision = terminalDecision,
            Attempt = Math.Max(1, initialAttempt),
            ContinuationId = string.IsNullOrWhiteSpace(continuationId)
                ? Guid.NewGuid().ToString("N")
                : continuationId.Trim(),
            ProtectedMaterialReference = protectedMaterialReference.Clone(),
            ProtectedMaterialDigestSha256 = protectedMaterialDigestSha256,
            ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending,
        };
        pending.TimeoutCallbackId = BuildToolTimeoutCallbackId(pending);
        return pending;
    }

    /// <param name="preparationStartedAtTimestamp">
    /// Actor-side preparation start for this attempt (the owning handler's entry timestamp,
    /// so admission / discovery / request build / protected-material store are measured).
    /// Telemetry only; deadlines are never derived from it.
    /// </param>
    private async Task StartToolExecutionAsync(
        ToolCallModuleState state,
        PendingToolCallExecutionState pending,
        IWorkflowTool tool,
        WorkflowToolExecutionRequest executionRequest,
        WorkflowToolResponseProjection? responseProjection,
        long preparationStartedAtTimestamp,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var pendingKey = BuildExecutionKey(pending.CallId, pending.ExecutionId);
        StampAttemptPreparationStartedAtUtc(pending, ctx, preparationStartedAtTimestamp);
        state.PendingExecutions[pendingKey] = pending;
        try
        {
            await SaveStateAsync(state, ctx, ct);
        }
        catch (Exception saveException)
        {
            await TryCleanupUnownedProtectedMaterialAfterStateSaveFailureAsync(
                pending.ProtectedMaterialReference,
                ctx,
                saveException);
            throw;
        }

        RuntimeCallbackLease lease;
        try
        {
            var remainingMs = Math.Max(
                1,
                pending.TimeoutDeadlineUnixMs - ctx.UtcNow.ToUnixTimeMilliseconds());
            lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                pending.TimeoutCallbackId,
                TimeSpan.FromMilliseconds(remainingMs),
                BuildTimeoutEvent(pending),
                BuildExecutionCallbackOptions(pending.TimeoutCallbackId),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ctx.Logger.LogWarning(
                exception,
                "ToolCall: watchdog scheduling failed run={RunId} step={StepId} failure_type={FailureType}",
                pending.RunId,
                pending.StepId,
                exception.GetType().Name);
            await FailBeforeToolDispatchAsync(pending, preparationStartedAtTimestamp, ctx, ct);
            return;
        }

        state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var persistedPending) ||
            !MatchesExecutionIdentity(persistedPending, pending.RunId, pending.StepId, pending.CallId, pending.ExecutionId) ||
            persistedPending.Attempt != pending.Attempt)
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                lease,
                "orphaned tool execution watchdog",
                ct);
            return;
        }

        persistedPending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        state.PendingExecutions[pendingKey] = persistedPending;
        await SaveStateAsync(state, ctx, ct);

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= persistedPending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingDeadlineAsync(
                persistedPending,
                outcomeMayBeUnknown: false,
                preparationStartedAtTimestamp,
                ctx,
                ct);
            return;
        }

        try
        {
            await ctx.PublishAsync(
                new WorkflowToolCallStartedEvent
                {
                    ToolName = persistedPending.ToolName,
                    CallId = persistedPending.CallId,
                    RunId = persistedPending.RunId,
                    StepId = persistedPending.StepId,
                },
                TopologyAudience.Self,
                ct,
                BuildExecutionCallbackOptions(BuildToolStartedOperationId(persistedPending)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ctx.Logger.LogWarning(
                "ToolCall: observational started event publication failed run={RunId} step={StepId} failure_type={FailureType}",
                persistedPending.RunId,
                persistedPending.StepId,
                exception.GetType().Name);
        }

        if (DispatchToolExecution(
            ctx,
            tool,
            executionRequest,
            responseProjection?.Clone(),
            persistedPending.Attempt,
            persistedPending.ContinuationId,
            persistedPending.TimeoutDeadlineUnixMs,
            preparationStartedAtTimestamp))
        {
            return;
        }

        await CompletePendingDeadlineAsync(
            persistedPending,
            outcomeMayBeUnknown: false,
            preparationStartedAtTimestamp,
            ctx,
            ct);
    }

    private async Task FailBeforeToolDispatchAsync(
        PendingToolCallExecutionState pending,
        long reconciliationStartedAtTimestamp,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(pending.CallId, pending.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var persistedPending) ||
            !MatchesExecutionIdentity(persistedPending, pending.RunId, pending.StepId, pending.CallId, pending.ExecutionId))
        {
            return;
        }

        var resolved = await ResolvePendingExecutionRequestAsync(persistedPending, ctx, ct);
        await CompletePendingExecutionFailureAsync(
            persistedPending,
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "tool_watchdog_unavailable",
                "The tool call was not dispatched because durable timeout protection is unavailable.",
                terminalInvoked: false,
                retryable: true),
            resolved.Material,
            ctx,
            ct);
        RecordPendingReconciliation(
            ctx,
            persistedPending,
            reconciliationStartedAtTimestamp,
            WorkflowToolCallReconciliationDisposition.PreDispatchFailed);
    }

    private async Task ExecuteToolAndSignalAsync(
        IWorkflowExecutionContext ctx,
        IWorkflowTool tool,
        WorkflowToolExecutionRequest executionRequest,
        WorkflowToolResponseProjection? responseProjection,
        int attempt,
        string continuationId,
        string dispatchId,
        long timeoutDeadlineUnixMs,
        long preparationStartedAtTimestamp,
        BackgroundExecutionRegistration registration,
        CancellationToken executionToken)
    {
        try
        {
            var providerStartedAtTimestamp = ctx.GetTimestamp();
            var providerStartedAtUtc = ctx.UtcNow;
            var dispatchStarted = CreateToolCallTimingObservation(
                ctx,
                executionRequest.RunId,
                executionRequest.StepId,
                executionRequest.CallId,
                executionRequest.ExecutionId,
                continuationId,
                attempt,
                WorkflowToolCallAttemptWaterline.ExternalDispatchStarted,
                dispatchId);
            dispatchStarted.PreparationElapsedMs = ElapsedMilliseconds(ctx, preparationStartedAtTimestamp);
            RecordToolCallTiming(dispatchStarted);

            WorkflowToolExecutionResult result;
            WorkflowToolCallProviderDisposition providerDisposition;
            try
            {
                result = await tool.ExecuteAsync(executionRequest, executionToken);
                providerDisposition = ResolveProviderDisposition(result);
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
            {
                RecordProviderReturned(
                    ctx,
                    executionRequest,
                    attempt,
                    continuationId,
                    dispatchId,
                    providerStartedAtTimestamp,
                    providerStartedAtUtc,
                    WorkflowToolCallProviderDisposition.Cancelled);
                return;
            }
            catch (Exception exception)
            {
                providerDisposition = WorkflowToolCallProviderDisposition.Threw;
                if (responseProjection is null)
                {
                    _logger.LogWarning(
                        exception,
                        "ToolCall: step={StepId} tool={Tool} execution failed",
                        executionRequest.StepId,
                        tool.Name);
                    result = WorkflowToolExecutionResult.Failed(string.Empty, string.Empty, exception.Message);
                }
                else
                {
                    _logger.LogWarning(
                        "ToolCall: step={StepId} tool={Tool} execution failed before response projection",
                        executionRequest.StepId,
                        tool.Name);
                    result = ProjectedToolFailure();
                }
            }

            var providerTiming = RecordProviderReturned(
                ctx,
                executionRequest,
                attempt,
                continuationId,
                dispatchId,
                providerStartedAtTimestamp,
                providerStartedAtUtc,
                providerDisposition);
            var providerReturnedAtTimestamp = ctx.GetTimestamp();
            result = ApplyResponseProjection(
                responseProjection,
                result,
                _logger,
                executionRequest.RunId,
                executionRequest.StepId);
            var completed = BuildAttemptCompletedSignal(
                executionRequest,
                attempt,
                continuationId,
                result);
            completed.ProviderTiming = providerTiming;
            await PublishCompletionSignalOrDeferToWatchdogAsync(
                ctx,
                completed,
                timeoutDeadlineUnixMs,
                providerReturnedAtTimestamp,
                executionToken);
        }
        finally
        {
            registration.MarkWorkerCompleted();
        }
    }

    private async Task PublishCompletionSignalOrDeferToWatchdogAsync(
        IWorkflowExecutionContext ctx,
        WorkflowToolCallAttemptCompletedEvent completed,
        long timeoutDeadlineUnixMs,
        long providerReturnedAtTimestamp,
        CancellationToken ct)
    {
        var operationId = BuildCompletionSignalOperationId(completed);
        var callbackId = BuildCompletionSignalCallbackId(completed);
        for (var transportAttempt = 1;
             transportAttempt <= MaxCompletionSignalTransportAttempts &&
             !ct.IsCancellationRequested &&
             ctx.UtcNow.ToUnixTimeMilliseconds() < timeoutDeadlineUnixMs;
             transportAttempt++)
        {
            try
            {
                await ctx.PublishAsync(
                    completed,
                    TopologyAudience.Self,
                    ct,
                    BuildExecutionCallbackOptions(operationId));
                var delivered = CreateToolCallTimingObservation(
                    ctx,
                    completed.RunId,
                    completed.StepId,
                    completed.CallId,
                    completed.ExecutionId,
                    completed.ContinuationId,
                    completed.Attempt,
                    WorkflowToolCallAttemptWaterline.CompletionDeliveryProducerConfirmed,
                    completed.ProviderTiming?.DispatchId ?? string.Empty);
                delivered.TransportAttempt = transportAttempt;
                delivered.DeliveryMethod = WorkflowToolCallCompletionDeliveryMethod.SelfPublish;
                delivered.DeliveryAcceptance = WorkflowToolCallCompletionDeliveryAcceptance.Confirmed;
                delivered.CompletionDeliveryElapsedMs = ElapsedMilliseconds(ctx, providerReturnedAtTimestamp);
                RecordToolCallTiming(delivered);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception publishException)
            {
                var deliveryUnknown = CreateToolCallTimingObservation(
                    ctx,
                    completed.RunId,
                    completed.StepId,
                    completed.CallId,
                    completed.ExecutionId,
                    completed.ContinuationId,
                    completed.Attempt,
                    WorkflowToolCallAttemptWaterline.CompletionDeliveryProducerConfirmed,
                    completed.ProviderTiming?.DispatchId ?? string.Empty);
                deliveryUnknown.TransportAttempt = transportAttempt;
                deliveryUnknown.DeliveryMethod = WorkflowToolCallCompletionDeliveryMethod.SelfPublish;
                deliveryUnknown.DeliveryAcceptance = WorkflowToolCallCompletionDeliveryAcceptance.Unknown;
                deliveryUnknown.CompletionDeliveryElapsedMs = ElapsedMilliseconds(ctx, providerReturnedAtTimestamp);
                RecordToolCallTiming(deliveryUnknown);
                _logger.LogWarning(
                    publishException,
                    "ToolCall: completion signal publication failed run={RunId} step={StepId} attempt={Attempt} failure_type={FailureType}; scheduling durable continuation",
                    completed.RunId,
                    completed.StepId,
                    completed.Attempt,
                    publishException.GetType().Name);
            }

            try
            {
                await ctx.ScheduleSelfDurableTimeoutAsync(
                    callbackId,
                    TimeSpan.FromMilliseconds(1),
                    completed,
                    BuildExecutionCallbackOptions(operationId),
                    ct);
                var scheduled = CreateToolCallTimingObservation(
                    ctx,
                    completed.RunId,
                    completed.StepId,
                    completed.CallId,
                    completed.ExecutionId,
                    completed.ContinuationId,
                    completed.Attempt,
                    WorkflowToolCallAttemptWaterline.CompletionDeliveryProducerConfirmed,
                    completed.ProviderTiming?.DispatchId ?? string.Empty);
                scheduled.TransportAttempt = transportAttempt;
                scheduled.DeliveryMethod = WorkflowToolCallCompletionDeliveryMethod.DurableCallback;
                scheduled.DeliveryAcceptance = WorkflowToolCallCompletionDeliveryAcceptance.Confirmed;
                scheduled.CompletionDeliveryElapsedMs = ElapsedMilliseconds(ctx, providerReturnedAtTimestamp);
                RecordToolCallTiming(scheduled);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception scheduleException)
            {
                var scheduleUnknown = CreateToolCallTimingObservation(
                    ctx,
                    completed.RunId,
                    completed.StepId,
                    completed.CallId,
                    completed.ExecutionId,
                    completed.ContinuationId,
                    completed.Attempt,
                    WorkflowToolCallAttemptWaterline.CompletionDeliveryProducerConfirmed,
                    completed.ProviderTiming?.DispatchId ?? string.Empty);
                scheduleUnknown.TransportAttempt = transportAttempt;
                scheduleUnknown.DeliveryMethod = WorkflowToolCallCompletionDeliveryMethod.DurableCallback;
                scheduleUnknown.DeliveryAcceptance = WorkflowToolCallCompletionDeliveryAcceptance.Unknown;
                scheduleUnknown.CompletionDeliveryElapsedMs = ElapsedMilliseconds(ctx, providerReturnedAtTimestamp);
                RecordToolCallTiming(scheduleUnknown);
                _logger.LogWarning(
                    scheduleException,
                    "ToolCall: completion continuation unavailable run={RunId} step={StepId} attempt={Attempt} transport_attempt={TransportAttempt} failure_type={FailureType}; retrying before authored deadline",
                    completed.RunId,
                    completed.StepId,
                    completed.Attempt,
                    transportAttempt,
                    scheduleException.GetType().Name);
            }

            if (transportAttempt == MaxCompletionSignalTransportAttempts ||
                ctx.UtcNow.ToUnixTimeMilliseconds() >= timeoutDeadlineUnixMs)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private bool DispatchToolExecution(
        IWorkflowExecutionContext ctx,
        IWorkflowTool tool,
        WorkflowToolExecutionRequest executionRequest,
        WorkflowToolResponseProjection? responseProjection,
        int attempt,
        string continuationId,
        long timeoutDeadlineUnixMs,
        long preparationStartedAtTimestamp)
    {
        var backgroundExecutionKey = BuildExecutionKey(executionRequest.CallId, executionRequest.ExecutionId);
        var remainingMs = timeoutDeadlineUnixMs - ctx.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
            return false;

        var registration = new BackgroundExecutionRegistration(TimeSpan.FromMilliseconds(remainingMs));
        var executionToken = registration.Token;
        if (!_backgroundExecutions.TryAdd(backgroundExecutionKey, registration))
        {
            registration.Dispose();
            return true;
        }

        var dispatchId = Guid.NewGuid().ToString("N");
        _ = Task.Run(
            () => ExecuteToolAndSignalAsync(
                ctx,
                tool,
                executionRequest,
                responseProjection,
                attempt,
                continuationId,
                dispatchId,
                timeoutDeadlineUnixMs,
                preparationStartedAtTimestamp,
                registration,
                executionToken),
            CancellationToken.None);
        return true;
    }

    void IWorkflowExecutionBackgroundWorkOwner.CancelBackgroundWork() =>
        CancelAllBackgroundExecutions();

    private void CancelAllBackgroundExecutions()
    {
        foreach (var key in _backgroundExecutions.Keys)
            CancelBackgroundExecutionByKey(key);
    }

    private void ReleaseBackgroundExecutionAfterDurableSuccessor(
        string pendingKey,
        int expectedAttempt,
        string expectedContinuationId,
        IWorkflowExecutionContext ctx)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        if (state.PendingExecutions.TryGetValue(pendingKey, out var current) &&
            current.ExecutionPhase == WorkflowToolCallExecutionPhase.ExecutionPending &&
            current.Attempt == expectedAttempt &&
            string.Equals(
                current.ContinuationId,
                NormalizeRequired(expectedContinuationId),
                StringComparison.Ordinal))
        {
            return;
        }

        CancelBackgroundExecutionByKey(pendingKey);
    }

    private void CancelBackgroundExecutionByKey(string backgroundExecutionKey)
    {
        if (!_backgroundExecutions.TryRemove(backgroundExecutionKey, out var registration))
            return;

        registration.CancelAndDisposeWhenCompleted();
    }

    private sealed class BackgroundExecutionRegistration : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source;
        private int _sourceOperations;
        private bool _workerCompleted;
        private bool _removalRequested;

        public BackgroundExecutionRegistration(TimeSpan timeout)
        {
            _source = new CancellationTokenSource(timeout);
        }

        public CancellationToken Token
        {
            get
            {
                lock (_gate)
                {
                    return (_source ?? throw new ObjectDisposedException(nameof(BackgroundExecutionRegistration)))
                        .Token;
                }
            }
        }

        public void MarkWorkerCompleted()
        {
            CancellationTokenSource? sourceToDispose;
            lock (_gate)
            {
                _workerCompleted = true;
                sourceToDispose = DetachSourceWhenReady();
            }

            sourceToDispose?.Dispose();
        }

        public void CancelAndDisposeWhenCompleted()
        {
            CancellationTokenSource? source;
            lock (_gate)
            {
                _removalRequested = true;
                source = _source;
                if (source != null)
                    _sourceOperations++;
            }

            if (source == null)
                return;

            try
            {
                source.Cancel();
            }
            finally
            {
                CancellationTokenSource? sourceToDispose;
                lock (_gate)
                {
                    _sourceOperations--;
                    sourceToDispose = DetachSourceWhenReady();
                }

                sourceToDispose?.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _workerCompleted = true;
            CancelAndDisposeWhenCompleted();
        }

        private CancellationTokenSource? DetachSourceWhenReady()
        {
            if (!_removalRequested || !_workerCompleted || _sourceOperations != 0)
                return null;

            var source = _source;
            _source = null;
            return source;
        }
    }

    private static async Task<(ToolCallProtectedMaterial? Material, StepRequestEvent Request)>
        ResolvePendingExecutionRequestAsync(
            PendingToolCallExecutionState pending,
            IWorkflowExecutionContext ctx,
            CancellationToken ct)
    {
        var resolution = await ResolveAndVerifyProtectedMaterialAsync(
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.CallId,
            ctx,
            ct);
        return (
            resolution.Material,
            ToStepRequest(pending, resolution.Material));
    }

    private async Task CompletePendingExecutionFailureAsync(
        PendingToolCallExecutionState expected,
        WorkflowToolExecutionResult failure,
        ToolCallProtectedMaterial? material,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(expected.CallId, expected.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var pending) ||
            !MatchesExecutionIdentity(
                pending,
                expected.RunId,
                expected.StepId,
                expected.CallId,
                expected.ExecutionId) ||
            !MatchesContinuationId(pending, expected.ContinuationId) ||
            pending.Attempt != expected.Attempt)
        {
            return;
        }

        var expectedAttempt = pending.Attempt;
        var expectedContinuationId = pending.ContinuationId;
        try
        {
            state.PendingExecutions.Remove(pendingKey);
            await PersistAndPublishToolOutcomeAsync(
                state,
                ctx,
                ToStepRequest(pending, material),
                pending.ToolName,
                pending.CallId,
                failure,
                pending.ApprovalRequestId,
                pending.TerminalDecision,
                pending.ProtectedMaterialReference,
                ct);
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pending.TimeoutLease,
                "terminal tool execution watchdog",
                ct);
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pending.RetryLease,
                "terminal tool execution retry",
                ct);
        }
        finally
        {
            ReleaseBackgroundExecutionAfterDurableSuccessor(
                pendingKey,
                expectedAttempt,
                expectedContinuationId,
                ctx);
        }
    }

    private async Task CompletePendingDeadlineAsync(
        PendingToolCallExecutionState pending,
        bool outcomeMayBeUnknown,
        long reconciliationStartedAtTimestamp,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var resolved = await ResolvePendingExecutionRequestAsync(pending, ctx, ct);
        var failure = outcomeMayBeUnknown
            ? UnknownToolOutcomeFailure(ToolOutcomeUnknownMessage)
            : WorkflowToolExecutionResult.Failed(
                string.Empty,
                "tool_retry_deadline_exceeded",
                "The tool call retry deadline elapsed before another external execution was dispatched.",
                terminalInvoked: false,
                retryable: false);
        await CompletePendingExecutionFailureAsync(
            pending,
            failure,
            resolved.Material,
            ctx,
            ct);
        RecordPendingReconciliation(
            ctx,
            pending,
            reconciliationStartedAtTimestamp,
            ResolveDeadlineReconciliationDisposition(outcomeMayBeUnknown));
    }

    private async Task HandleToolAttemptCompletedAsync(
        WorkflowToolCallAttemptCompletedEvent completed,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var reconciliationStartedAtTimestamp = ctx.GetTimestamp();
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(completed.CallId, completed.ExecutionId);
        if (!IsTrustedCompletionEnvelope(envelope, ctx, completed))
        {
            RecordCompletionReconciliation(
                ctx,
                completed,
                reconciliationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Untrusted);
            return;
        }

        if (!state.PendingExecutions.TryGetValue(pendingKey, out var pending))
        {
            var duplicate = await TryDrainPersistedAttemptSuccessorAsync(
                state,
                completed.RunId,
                completed.StepId,
                completed.CallId,
                completed.ExecutionId,
                ctx,
                ct);
            RecordCompletionReconciliation(
                ctx,
                completed,
                reconciliationStartedAtTimestamp,
                duplicate
                    ? WorkflowToolCallReconciliationDisposition.Duplicate
                    : WorkflowToolCallReconciliationDisposition.NoPendingExecution);
            return;
        }

        if (
            !MatchesExecutionIdentity(
                pending,
                completed.RunId,
                completed.StepId,
                completed.CallId,
                completed.ExecutionId) ||
            !MatchesContinuationId(pending, completed.ContinuationId) ||
            pending.Attempt != completed.Attempt ||
            pending.ExecutionPhase != WorkflowToolCallExecutionPhase.ExecutionPending)
        {
            RecordCompletionReconciliation(
                ctx,
                completed,
                reconciliationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Stale);
            return;
        }

        var expectedAttempt = pending.Attempt;
        var expectedContinuationId = pending.ContinuationId;
        try
        {
            var approvalRequired =
                completed.OutcomeCase == WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.ApprovalRequired;
            var decodedResult = ToExecutionResult(
                completed,
                retryableOverride: completed.OutcomeCase == WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure &&
                                   completed.Failure is { TerminalInvoked: false, Retryable: true }
                    ? false
                    : null);
            var pendingOperationAccepted =
                completed.OutcomeCase == WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.PendingOperation &&
                decodedResult.PendingOperation != null;

            ToolCallProtectedMaterial? material = null;
            if (!pendingOperationAccepted || !IsValidPendingOperationResult(decodedResult))
            {
                var materialResolution = await ResolveAndVerifyProtectedMaterialAsync(
                    pending.ProtectedMaterialReference,
                    pending.ProtectedMaterialDigestSha256,
                    pending.RunId,
                    pending.StepId,
                    pending.ExecutionId,
                    pending.CallId,
                    ctx,
                    ct);
                if (!materialResolution.Resolved)
                {
                    if (materialResolution.IsTransientFailure)
                    {
                        throw new WorkflowToolProtectedMaterialResolutionPendingException(
                            materialResolution.ErrorCode);
                    }

                    await CompletePendingExecutionFailureAsync(
                        pending,
                        WorkflowToolExecutionResult.Failed(
                            string.Empty,
                            materialResolution.ErrorCode,
                            "The tool result was rejected because its protected execution material is unavailable.",
                            terminalInvoked: false,
                            retryable: false,
                            failureOutcome: WorkflowStepFailureOutcome.OutcomeUncertain),
                        material: null,
                        ctx,
                        ct);
                    RecordCompletionReconciliation(
                        ctx,
                        completed,
                        reconciliationStartedAtTimestamp,
                        WorkflowToolCallReconciliationDisposition.ProtectedMaterialUnavailable);
                    return;
                }

                material = materialResolution.Material!;
            }

            if (completed.OutcomeCase == WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure &&
                completed.Failure is { TerminalInvoked: false, Retryable: true } &&
                pending.Attempt < MaxToolExecutionAttempts)
            {
                await ScheduleToolRetryAsync(
                    state,
                    pending,
                    material!,
                    reconciliationStartedAtTimestamp,
                    ctx,
                    ct);
                var retryState = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
                var retryDisposition = retryState.PendingExecutions.TryGetValue(pendingKey, out var retryPending) &&
                                       retryPending.ExecutionPhase == WorkflowToolCallExecutionPhase.RetryPending
                    ? WorkflowToolCallReconciliationDisposition.RetryScheduled
                    : WorkflowToolCallReconciliationDisposition.RetryDeadlineExceeded;
                RecordCompletionReconciliation(
                    ctx,
                    completed,
                    reconciliationStartedAtTimestamp,
                    retryDisposition);
                return;
            }

            if (pendingOperationAccepted)
            {
                await PersistPendingOperationAsync(
                    state,
                    ctx,
                    pending,
                    material,
                    decodedResult,
                    ct);
            }
            else if (approvalRequired)
            {
                state.PendingExecutions.Remove(pendingKey);
                var request = ToStepRequest(pending, material);
                var approval = completed.ApprovalRequired;
                await SuspendForApprovalAsync(
                    state,
                    ctx,
                    request,
                    pending.ToolName,
                    pending.CallId,
                    pending.IssuedAtUnixMs,
                    pending.TimeoutMs,
                    pending.TimeoutDeadlineUnixMs,
                    pending.TimeoutCallbackId,
                    pending.TimeoutLease,
                    pending.ContinuationId,
                    pending.Attempt,
                    new WorkflowToolApprovalPendingOutcome(
                        approval.ApprovalRequestId,
                        approval.ToolName,
                        approval.ToolCallId,
                        material!.ArgumentsJson,
                        approval.ApprovalMode,
                        approval.IsReadOnly,
                        approval.IsDestructive),
                    pending.ProtectedMaterialReference,
                    pending.ProtectedMaterialDigestSha256,
                    ct);
            }
            else
            {
                state.PendingExecutions.Remove(pendingKey);
                var request = ToStepRequest(pending, material);
                await PersistAndPublishToolOutcomeAsync(
                    state,
                    ctx,
                    request,
                    pending.ToolName,
                    pending.CallId,
                    decodedResult,
                    pending.ApprovalRequestId,
                    pending.TerminalDecision,
                    pending.ProtectedMaterialReference,
                    ct);
            }

            RecordCompletionReconciliation(
                ctx,
                completed,
                reconciliationStartedAtTimestamp,
                ResolveCompletionReconciliationDisposition(completed));

            if (!approvalRequired)
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    pending.TimeoutLease,
                    "completed tool execution watchdog",
                    ct);
            }
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pending.RetryLease,
                "completed tool execution retry",
                ct);
        }
        finally
        {
            ReleaseBackgroundExecutionAfterDurableSuccessor(
                pendingKey,
                expectedAttempt,
                expectedContinuationId,
                ctx);
        }
    }

    private async Task HandleToolTimeoutFiredAsync(
        WorkflowToolCallTimeoutFiredEvent timeout,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var reconciliationStartedAtTimestamp = ctx.GetTimestamp();
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(timeout.CallId, timeout.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var pending))
        {
            var approval = state.PendingApprovals.FirstOrDefault(item =>
                MatchesCallIdentity(
                    item.Value.RunId,
                    item.Value.StepId,
                    item.Value.ToolCallId,
                    item.Value.ExecutionId,
                    timeout.RunId,
                    timeout.StepId,
                    timeout.CallId,
                    timeout.ExecutionId));
            if (!string.IsNullOrEmpty(approval.Key) &&
                MatchesApprovalTimeout(envelope, approval.Value, timeout))
            {
                RecordTimeoutAccepted(
                    ctx,
                    timeout,
                    approval.Value.Attempt,
                    WorkflowToolCallReconciliationDisposition.ApprovalDeadlineExceeded);
                await CompletePendingApprovalDeadlineAsync(
                    state,
                    approval.Key,
                    approval.Value,
                    ctx,
                    ct);
                RecordTimeoutReconciliation(
                    ctx,
                    timeout,
                    reconciliationStartedAtTimestamp,
                    WorkflowToolCallReconciliationDisposition.ApprovalDeadlineExceeded,
                    approval.Value.Attempt);
                return;
            }

            var duplicate = await TryDrainPersistedCompletionAsync(
                state,
                timeout.RunId,
                timeout.StepId,
                timeout.CallId,
                timeout.ExecutionId,
                ctx,
                ct);
            RecordTimeoutReconciliation(
                ctx,
                timeout,
                reconciliationStartedAtTimestamp,
                duplicate
                    ? WorkflowToolCallReconciliationDisposition.Duplicate
                    : WorkflowToolCallReconciliationDisposition.NoPendingExecution);
            return;
        }

        if (!MatchesExecutionIdentity(
                pending,
                timeout.RunId,
                timeout.StepId,
                timeout.CallId,
                timeout.ExecutionId) ||
            !MatchesContinuationId(pending, timeout.ContinuationId))
        {
            // Rejected wake-up: observe it under the callback's own identity (including its
            // attempt), not the persisted pending it failed to match.
            RecordTimeoutReconciliation(
                ctx,
                timeout,
                reconciliationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Stale);
            return;
        }

        if (!MatchesExecutionTimeout(envelope, pending))
        {
            RecordTimeoutReconciliation(
                ctx,
                timeout,
                reconciliationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Untrusted);
            return;
        }

        var outcomeMayBeUnknown = pending.ExecutionPhase != WorkflowToolCallExecutionPhase.RetryPending;
        RecordTimeoutAccepted(
            ctx,
            timeout,
            pending.Attempt,
            ResolveDeadlineReconciliationDisposition(outcomeMayBeUnknown));
        // actor_reconciliation_completed[timeout_outcome_unknown|retry_deadline_exceeded] is
        // recorded by CompletePendingDeadlineAsync on the matched pending identity.
        await CompletePendingDeadlineAsync(
            pending,
            outcomeMayBeUnknown,
            reconciliationStartedAtTimestamp,
            ctx,
            ct);
    }

    private async Task CompletePendingApprovalDeadlineAsync(
        ToolCallModuleState state,
        string pendingKey,
        PendingToolCallApprovalState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var materialResolution = await ResolveAndVerifyProtectedMaterialAsync(
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.ToolCallId,
            ctx,
            ct);
        state.PendingApprovals.Remove(pendingKey);
        await PersistAndPublishToolFailureAsync(
            state,
            ctx,
            ToStepRequest(pending, materialResolution.Material),
            pending.ToolName,
            "The tool call approval deadline elapsed before a decision was received.",
            "tool_approval_deadline_exceeded",
            string.Empty,
            pending.ToolCallId,
            pending.ApprovalRequestId,
            WorkflowToolCallTerminalDecision.NoApproval,
            terminalInvoked: false,
            retryable: false,
            WorkflowStepFailureOutcome.CalleeConfirmed,
            pending.ProtectedMaterialReference,
            ct);
    }

    /// <param name="preparationStartedAtTimestamp">
    /// Entry timestamp of the completion handler that reconciled attempt N-1's retryable
    /// failure. The actor starts preparing attempt N at that point, so the
    /// <c>pending_state_persisted</c> waterline for attempt N reports <c>actor_preparation</c>
    /// elapsed from it. Telemetry only; retry timing is never derived from it.
    /// </param>
    private async Task ScheduleToolRetryAsync(
        ToolCallModuleState state,
        PendingToolCallExecutionState pending,
        ToolCallProtectedMaterial material,
        long preparationStartedAtTimestamp,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var nextAttempt = pending.Attempt + 1;
        var retryDelay = ResolveToolRetryDelay(nextAttempt);
        var retryDueUnixMs = checked(
            ctx.UtcNow.ToUnixTimeMilliseconds() + (long)retryDelay.TotalMilliseconds);
        if (retryDueUnixMs >= pending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                WorkflowToolExecutionResult.Failed(
                    string.Empty,
                    "tool_retry_deadline_exceeded",
                    "The tool call retry deadline elapsed before another external execution was dispatched.",
                    terminalInvoked: false,
                    retryable: false),
                material,
                ctx,
                ct);
            return;
        }

        pending.Attempt = nextAttempt;
        pending.RetryCallbackId = BuildToolRetryCallbackId(pending);
        pending.RetryDueUnixMs = retryDueUnixMs;
        pending.RetryLease = null;
        pending.ExecutionPhase = WorkflowToolCallExecutionPhase.RetryPending;
        StampAttemptPreparationStartedAtUtc(pending, ctx, preparationStartedAtTimestamp);
        state.PendingExecutions[BuildExecutionKey(pending.CallId, pending.ExecutionId)] = pending;
        await SaveStateAsync(state, ctx, ct);

        try
        {
            var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
                pending.RetryCallbackId,
                retryDelay,
                BuildRetryEvent(pending),
                BuildExecutionCallbackOptions(pending.RetryCallbackId),
                ct);
            state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
            var pendingKey = BuildExecutionKey(pending.CallId, pending.ExecutionId);
            if (!state.PendingExecutions.TryGetValue(pendingKey, out var persistedPending) ||
                persistedPending.Attempt != pending.Attempt)
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    lease,
                    "orphaned approved tool retry",
                    ct);
                return;
            }

            persistedPending.RetryLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            state.PendingExecutions[pendingKey] = persistedPending;
            await SaveStateAsync(state, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ctx.Logger.LogWarning(
                exception,
                "ToolCall: pre-terminal retry scheduling failed run={RunId} step={StepId} attempt={Attempt} failure_type={FailureType}; attempting immediate continuation",
                pending.RunId,
                pending.StepId,
                pending.Attempt,
                exception.GetType().Name);
            try
            {
                await ctx.PublishAsync(
                    BuildRetryEvent(pending),
                    TopologyAudience.Self,
                    ct,
                    BuildExecutionCallbackOptions(pending.RetryCallbackId));
            }
            catch (Exception continuationException)
            {
                ctx.Logger.LogWarning(
                    continuationException,
                    "ToolCall: immediate retry continuation failed run={RunId} step={StepId} attempt={Attempt} failure_type={FailureType}; activation recovery or watchdog remains authoritative",
                    pending.RunId,
                    pending.StepId,
                    pending.Attempt,
                    continuationException.GetType().Name);
            }
        }
    }

    private static void StampAttemptPreparationStartedAtUtc(
        PendingToolCallExecutionState pending,
        IWorkflowExecutionContext ctx,
        long preparationStartedAtTimestamp)
    {
        var elapsed = ctx.GetElapsedTime(preparationStartedAtTimestamp);
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        pending.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(ctx.UtcNow - elapsed);
    }

    private async Task HandleApprovedToolRetryFiredAsync(
        WorkflowToolCallRetryFiredEvent retry,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var preparationStartedAtTimestamp = ctx.GetTimestamp();
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(retry.CallId, retry.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var pending))
        {
            await TryDrainPersistedCompletionAsync(
                state,
                retry.RunId,
                retry.StepId,
                retry.CallId,
                retry.ExecutionId,
                ctx,
                ct);
            return;
        }

        if (pending.Attempt != retry.Attempt ||
            pending.ExecutionPhase != WorkflowToolCallExecutionPhase.RetryPending ||
            !MatchesExecutionIdentity(pending, retry.RunId, retry.StepId, retry.CallId, retry.ExecutionId) ||
            !MatchesContinuationId(pending, retry.ContinuationId))
        {
            // Rejected wake-up: observe it under the callback's own identity, not the newer
            // persisted attempt it failed to match.
            RecordRetryCallbackReconciliation(
                ctx,
                retry,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Stale);
            return;
        }

        if (!MatchesExecutionRetry(envelope, pending))
        {
            RecordRetryCallbackReconciliation(
                ctx,
                retry,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Untrusted);
            return;
        }

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: false,
                preparationStartedAtTimestamp,
                ctx,
                ct);
            return;
        }

        var materialResolution = await ResolveAndVerifyProtectedMaterialAsync(
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.CallId,
            ctx,
            ct);
        if (!materialResolution.Resolved)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                WorkflowToolExecutionResult.Failed(
                    string.Empty,
                    materialResolution.ErrorCode,
                    "The tool retry was not dispatched because its protected material is unavailable.",
                    terminalInvoked: false,
                    retryable: false),
                material: null,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.ProtectedMaterialUnavailable);
            return;
        }

        var material = materialResolution.Material!;
        var request = ToStepRequest(pending, material);
        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var tool))
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                WorkflowToolExecutionResult.Failed(
                    string.Empty,
                    string.Empty,
                    "tool not found or no tool sources configured",
                    terminalInvoked: false,
                    retryable: false),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.PreDispatchFailed);
            return;
        }

        var admission = ResolveInvocationAdmission(ctx, request, pending.ToolName, out var admissionError);
        if (admissionError != null)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                WorkflowToolExecutionResult.Failed(
                    string.Empty,
                    string.Empty,
                    admissionError,
                    terminalInvoked: false,
                    retryable: false),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.PreDispatchFailed);
            return;
        }

        WorkflowToolExecutionRequest executionRequest;
        try
        {
            executionRequest = await BuildToolExecutionRequestAsync(
                material.ArgumentsJson,
                request,
                pending.CallId,
                pending.IssuedAtUnixMs,
                ctx,
                ct,
                pending.TerminalDecision == WorkflowToolCallTerminalDecision.Approved
                    ? new ToolApprovalGrant(pending.ApprovalRequestId, pending.ToolName, pending.CallId)
                    : null,
                admission);
        }
        catch (Exception exception)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                WorkflowToolExecutionResult.Failed(
                    string.Empty,
                    request.ExternalInvocation?.ResponseProjection is null
                        ? string.Empty
                        : ProjectedToolFailureCode,
                    request.ExternalInvocation?.ResponseProjection is null
                        ? exception.Message
                        : ProjectedToolFailureMessage,
                    terminalInvoked: false,
                    retryable: false),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.PreDispatchFailed);
            return;
        }

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: false,
                preparationStartedAtTimestamp,
                ctx,
                ct);
            return;
        }

        pending.RetryLease = null;
        pending.RetryCallbackId = string.Empty;
        pending.RetryDueUnixMs = 0;
        pending.ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending;
        state.PendingExecutions[pendingKey] = pending;
        // Same attempt, phase flip only: pending_state_persisted for this attempt was already
        // recorded by ScheduleToolRetryAsync when the attempt number was first persisted.
        await SaveStateAsync(state, ctx, ct);

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs ||
            !DispatchToolExecution(
            ctx,
            tool,
            executionRequest,
            request.ExternalInvocation?.ResponseProjection?.Clone(),
            pending.Attempt,
            pending.ContinuationId,
            pending.TimeoutDeadlineUnixMs,
            preparationStartedAtTimestamp))
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: false,
                preparationStartedAtTimestamp,
                ctx,
                ct);
        }
    }

    private async Task HandleToolExecutionRecoveryFiredAsync(
        WorkflowToolCallExecutionRecoveryFiredEvent recovery,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var preparationStartedAtTimestamp = ctx.GetTimestamp();
        var state = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
        var pendingKey = BuildExecutionKey(recovery.CallId, recovery.ExecutionId);
        if (!state.PendingExecutions.TryGetValue(pendingKey, out var pending))
        {
            await TryDrainPersistedCompletionAsync(
                state,
                recovery.RunId,
                recovery.StepId,
                recovery.CallId,
                recovery.ExecutionId,
                ctx,
                ct);
            return;
        }

        if (pending.ExecutionPhase != WorkflowToolCallExecutionPhase.ExecutionPending ||
            pending.Attempt != recovery.Attempt ||
            !MatchesExecutionIdentity(
                pending,
                recovery.RunId,
                recovery.StepId,
                recovery.CallId,
                recovery.ExecutionId) ||
            !MatchesContinuationId(pending, recovery.ContinuationId))
        {
            // Rejected wake-up: observe it under the callback's own identity, not the newer
            // persisted attempt it failed to match.
            RecordRecoveryCallbackReconciliation(
                ctx,
                recovery,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Stale);
            return;
        }

        if (!IsTrustedExecutionRecoveryEnvelope(envelope, ctx, pending))
        {
            RecordRecoveryCallbackReconciliation(
                ctx,
                recovery,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.Untrusted);
            return;
        }

        if (_backgroundExecutions.ContainsKey(pendingKey))
            return;

        if (!await EnsurePendingExecutionWatchdogAsync(pending, ctx, ct))
            return;

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs)
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: true,
                preparationStartedAtTimestamp,
                ctx,
                ct);
            return;
        }

        var materialResolution = await ResolveAndVerifyProtectedMaterialAsync(
            pending.ProtectedMaterialReference,
            pending.ProtectedMaterialDigestSha256,
            pending.RunId,
            pending.StepId,
            pending.ExecutionId,
            pending.CallId,
            ctx,
            ct);
        if (!materialResolution.Resolved)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                UnknownToolOutcomeFailure(
                    "The tool execution could not be reconciled because its protected material is unavailable."),
                material: null,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown);
            return;
        }

        var material = materialResolution.Material!;
        var request = ToStepRequest(pending, material);
        var toolIndex = await GetOrDiscoverAsync(ct);
        if (!toolIndex.TryGetValue(pending.ToolName, out var tool))
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                UnknownToolOutcomeFailure(
                    "The tool execution could not be reconciled because the tool is unavailable."),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown);
            return;
        }

        if (tool.RecoverySafety is not (
                WorkflowToolRecoverySafety.ReplayableReadOnly or
                WorkflowToolRecoverySafety.DurableStartOnceRedispatch))
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                UnknownToolOutcomeFailure(
                    "The tool execution outcome is unknown and this tool does not permit uncertain recovery redispatch."),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown);
            return;
        }

        var admission = ResolveInvocationAdmission(ctx, request, pending.ToolName, out var admissionError);
        if (admissionError != null)
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                UnknownToolOutcomeFailure(
                    "The tool execution could not be reconciled because admission is unavailable."),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown);
            return;
        }

        WorkflowToolExecutionRequest executionRequest;
        try
        {
            executionRequest = await BuildToolExecutionRequestAsync(
                material.ArgumentsJson,
                request,
                pending.CallId,
                pending.IssuedAtUnixMs,
                ctx,
                ct,
                pending.TerminalDecision == WorkflowToolCallTerminalDecision.Approved
                    ? new ToolApprovalGrant(pending.ApprovalRequestId, pending.ToolName, pending.CallId)
                    : null,
                admission);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await CompletePendingExecutionFailureAsync(
                pending,
                UnknownToolOutcomeFailure(
                    "The tool execution could not be reconciled after actor recovery."),
                material,
                ctx,
                ct);
            RecordPendingReconciliation(
                ctx,
                pending,
                preparationStartedAtTimestamp,
                WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown);
            return;
        }

        if (ctx.UtcNow.ToUnixTimeMilliseconds() >= pending.TimeoutDeadlineUnixMs ||
            !DispatchToolExecution(
                ctx,
                tool,
                executionRequest,
                request.ExternalInvocation?.ResponseProjection?.Clone(),
                pending.Attempt,
                pending.ContinuationId,
                pending.TimeoutDeadlineUnixMs,
                preparationStartedAtTimestamp))
        {
            await CompletePendingDeadlineAsync(
                pending,
                outcomeMayBeUnknown: true,
                preparationStartedAtTimestamp,
                ctx,
                ct);
        }
    }

    private static WorkflowToolCallAttemptCompletedEvent BuildAttemptCompletedSignal(
        WorkflowToolExecutionRequest request,
        int attempt,
        string continuationId,
        WorkflowToolExecutionResult result)
    {
        var completed = new WorkflowToolCallAttemptCompletedEvent
        {
            RunId = request.RunId,
            StepId = request.StepId,
            ExecutionId = request.ExecutionId,
            CallId = request.CallId,
            Attempt = attempt,
            ContinuationId = continuationId,
        };
        if (result.PendingOperation is { } pendingOperation)
        {
            if (TryBuildAttemptPendingOperationOutcome(
                    result,
                    pendingOperation,
                    out var pendingOutcome))
            {
                completed.PendingOperation = pendingOutcome;
            }
            else
            {
                completed.Failure = BuildInvalidPendingOperationAttemptFailure();
            }
        }
        else if (result.PendingApproval is { } approval)
        {
            completed.ApprovalRequired = new WorkflowToolCallAttemptApprovalRequiredOutcome
            {
                ApprovalRequestId = approval.ApprovalRequestId,
                ToolName = approval.ToolName,
                ToolCallId = approval.ToolCallId,
                ApprovalMode = approval.ApprovalMode,
                IsReadOnly = approval.IsReadOnly,
                IsDestructive = approval.IsDestructive,
            };
        }
        else if (result.Failure is { } failure)
        {
            completed.Failure = new WorkflowToolCallAttemptFailureOutcome
            {
                ResultJson = result.ResultJson,
                ErrorCode = failure.ErrorCode,
                ErrorMessage = failure.ErrorMessage,
                TerminalInvoked = failure.TerminalInvoked,
                Retryable = failure.Retryable,
                FailureOutcome = failure.FailureOutcome,
            };
        }
        else
        {
            completed.Success = new WorkflowToolCallAttemptSuccessOutcome
            {
                ResultJson = result.ResultJson,
                ManagedHandoff = result.ManagedHandoff?.Clone(),
            };
        }

        return completed;
    }

    private static WorkflowToolExecutionResult ToExecutionResult(
        WorkflowToolCallAttemptCompletedEvent completed,
        bool? retryableOverride = null) =>
        completed.OutcomeCase switch
        {
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Success =>
                WorkflowToolExecutionResult.Success(
                    completed.Success.ResultJson,
                    completed.Success.ManagedHandoff?.Clone()),
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure =>
                WorkflowToolExecutionResult.Failed(
                    completed.Failure.ResultJson,
                    completed.Failure.ErrorCode,
                    completed.Failure.ErrorMessage,
                    completed.Failure.TerminalInvoked,
                    retryableOverride ?? completed.Failure.Retryable,
                    NormalizeFailureOutcome(
                        completed.Failure.FailureOutcome,
                        completed.Failure.ErrorCode)),
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.PendingOperation =>
                DecodeAttemptPendingOperationOutcome(completed.PendingOperation),
            _ => WorkflowToolExecutionResult.Failed(
                string.Empty,
                "tool_completion_invalid",
                "The tool completion signal did not contain a terminal outcome.",
                terminalInvoked: true,
                retryable: false),
        };

    private static WorkflowStepFailureOutcome NormalizeFailureOutcome(
        WorkflowStepFailureOutcome failureOutcome,
        string? errorCode) =>
        failureOutcome switch
        {
            WorkflowStepFailureOutcome.CalleeConfirmed => WorkflowStepFailureOutcome.CalleeConfirmed,
            WorkflowStepFailureOutcome.OutcomeUncertain => WorkflowStepFailureOutcome.OutcomeUncertain,
            _ when string.Equals(errorCode, ToolOutcomeUnknownCode, StringComparison.Ordinal) =>
                WorkflowStepFailureOutcome.OutcomeUncertain,
            _ => WorkflowStepFailureOutcome.CalleeConfirmed,
        };

    private static WorkflowToolExecutionResult UnknownToolOutcomeFailure(string message) =>
        WorkflowToolExecutionResult.Failed(
            string.Empty,
            ToolOutcomeUnknownCode,
            message,
            terminalInvoked: true,
            retryable: false,
            WorkflowStepFailureOutcome.OutcomeUncertain);

    private static bool TryBuildAttemptPendingOperationOutcome(
        WorkflowToolExecutionResult result,
        WorkflowToolPendingOperation operation,
        out WorkflowToolCallAttemptPendingOperationOutcome outcome)
    {
        outcome = new WorkflowToolCallAttemptPendingOperationOutcome();
        if (!IsPurePendingOperationResult(result, operation) ||
            !TryMapAttemptPendingOperationStatus(operation.Status, out var status) ||
            !TryMapAttemptPendingOperationRouteIdentitySource(
                operation.RouteIdentitySource,
                out var routeIdentitySource))
        {
            return false;
        }

        outcome = new WorkflowToolCallAttemptPendingOperationOutcome
        {
            OperationId = operation.OperationId,
            ProviderOperationId = operation.ProviderOperationId,
            StatusPath = operation.StatusPath,
            ResultPath = operation.ResultPath,
            CancelPath = operation.CancelPath,
            Status = status,
            Etag = operation.ETag ?? string.Empty,
            RetryAfterMilliseconds = operation.RetryAfterMilliseconds,
            ExpiresAtUnixMs = operation.ExpiresAtUnixMs,
            ServiceSlug = operation.ServiceSlug,
            UserServiceId = operation.UserServiceId ?? string.Empty,
            RouteIdentitySource = routeIdentitySource,
        };
        if (result.CancellationRecoveryIntent is { } recoveryIntent)
        {
            if (!IsValidCancellationTerminalIntent(recoveryIntent))
                return false;

            outcome.CancellationRecoveryIntent =
                ToAttemptCancellationRecoveryIntent(recoveryIntent);
        }
        return true;
    }

    private static WorkflowToolExecutionResult DecodeAttemptPendingOperationOutcome(
        WorkflowToolCallAttemptPendingOperationOutcome outcome)
    {
        if (!TryMapPendingOperationStatus(outcome.Status, out var status) ||
            !TryMapPendingOperationRouteIdentitySource(
                outcome.RouteIdentitySource,
                out var routeIdentitySource))
        {
            return InvalidPendingOperationResult();
        }

        var operation = new WorkflowToolPendingOperation(
            outcome.OperationId,
            outcome.ProviderOperationId,
            outcome.StatusPath,
            outcome.ResultPath,
            outcome.CancelPath,
            status,
            string.IsNullOrEmpty(outcome.Etag) ? null : outcome.Etag,
            outcome.RetryAfterMilliseconds,
            outcome.ExpiresAtUnixMs,
            outcome.ServiceSlug,
            string.IsNullOrEmpty(outcome.UserServiceId) ? null : outcome.UserServiceId,
            routeIdentitySource);
        var result = new WorkflowToolExecutionResult(
            string.Empty,
            PendingOperation: operation,
            CancellationRecoveryIntent: FromAttemptCancellationRecoveryIntent(
                outcome.CancellationRecoveryIntent));
        return IsPurePendingOperationResult(result, operation)
            ? result
            : InvalidPendingOperationResult();
    }

    private static WorkflowToolCallAttemptCancellationRecoveryIntent ToAttemptCancellationRecoveryIntent(
        WorkflowToolCancellationTerminalAuditIntent intent)
    {
        var result = intent.Result;
        var failure = result.Failure;
        var transported = new WorkflowToolCallAttemptCancellationRecoveryIntent
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
            transported.ToolOwnedAuditIntent = intent.ToolOwnedAuditIntent.Clone();
        return transported;
    }

    private static WorkflowToolCancellationTerminalAuditIntent? FromAttemptCancellationRecoveryIntent(
        WorkflowToolCallAttemptCancellationRecoveryIntent? intent)
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

    private static bool IsPurePendingOperationResult(
        WorkflowToolExecutionResult result,
        WorkflowToolPendingOperation operation)
    {
        if (result.PendingOperation == null ||
            result.PendingApproval != null ||
            result.Failure != null ||
            result.ManagedHandoff != null ||
            !string.IsNullOrEmpty(result.ResultJson) ||
            result.CancellationRecoveryIntent is { } recoveryIntent &&
            !IsValidCancellationTerminalIntent(recoveryIntent) ||
            string.IsNullOrWhiteSpace(operation.OperationId) ||
            string.IsNullOrWhiteSpace(operation.ServiceSlug) ||
            operation.RetryAfterMilliseconds < 0)
        {
            return false;
        }

        var hasProviderReceipt =
            !string.IsNullOrWhiteSpace(operation.ProviderOperationId) &&
            !string.IsNullOrWhiteSpace(operation.StatusPath) &&
            !string.IsNullOrWhiteSpace(operation.ResultPath) &&
            !string.IsNullOrWhiteSpace(operation.CancelPath);
        if (hasProviderReceipt)
            return true;

        return string.IsNullOrWhiteSpace(operation.ProviderOperationId) &&
               string.IsNullOrWhiteSpace(operation.StatusPath) &&
               string.IsNullOrWhiteSpace(operation.ResultPath) &&
               string.IsNullOrWhiteSpace(operation.CancelPath) &&
               operation.Status == WorkflowToolPendingOperationStatus.SubmissionUncertain;
    }

    private static WorkflowToolCallAttemptFailureOutcome BuildInvalidPendingOperationAttemptFailure() =>
        new()
        {
            ErrorCode = "workflow_tool_pending_operation_invalid",
            ErrorMessage = "The tool returned an invalid durable pending-operation receipt.",
            TerminalInvoked = true,
            Retryable = false,
            FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
        };

    private static WorkflowToolExecutionResult InvalidPendingOperationResult() =>
        WorkflowToolExecutionResult.Failed(
            string.Empty,
            "workflow_tool_pending_operation_invalid",
            "The tool returned an invalid durable pending-operation receipt.",
            terminalInvoked: true,
            retryable: false,
            WorkflowStepFailureOutcome.OutcomeUncertain);

    private static bool TryMapAttemptPendingOperationStatus(
        WorkflowToolPendingOperationStatus status,
        out WorkflowToolCallAttemptPendingOperationStatus mapped)
    {
        mapped = status switch
        {
            WorkflowToolPendingOperationStatus.Unspecified =>
                WorkflowToolCallAttemptPendingOperationStatus.Unspecified,
            WorkflowToolPendingOperationStatus.SubmissionUncertain =>
                WorkflowToolCallAttemptPendingOperationStatus.SubmissionUncertain,
            WorkflowToolPendingOperationStatus.Queued =>
                WorkflowToolCallAttemptPendingOperationStatus.Queued,
            WorkflowToolPendingOperationStatus.Provisioning =>
                WorkflowToolCallAttemptPendingOperationStatus.Provisioning,
            WorkflowToolPendingOperationStatus.Preparing =>
                WorkflowToolCallAttemptPendingOperationStatus.Preparing,
            WorkflowToolPendingOperationStatus.Running =>
                WorkflowToolCallAttemptPendingOperationStatus.Running,
            WorkflowToolPendingOperationStatus.Collecting =>
                WorkflowToolCallAttemptPendingOperationStatus.Collecting,
            WorkflowToolPendingOperationStatus.Succeeded =>
                WorkflowToolCallAttemptPendingOperationStatus.Succeeded,
            WorkflowToolPendingOperationStatus.Failed =>
                WorkflowToolCallAttemptPendingOperationStatus.Failed,
            WorkflowToolPendingOperationStatus.Cancelled =>
                WorkflowToolCallAttemptPendingOperationStatus.Cancelled,
            WorkflowToolPendingOperationStatus.OutcomeUncertain =>
                WorkflowToolCallAttemptPendingOperationStatus.OutcomeUncertain,
            _ => (WorkflowToolCallAttemptPendingOperationStatus)(-1),
        };
        return (int)mapped >= 0;
    }

    private static bool TryMapPendingOperationStatus(
        WorkflowToolCallAttemptPendingOperationStatus status,
        out WorkflowToolPendingOperationStatus mapped)
    {
        mapped = status switch
        {
            WorkflowToolCallAttemptPendingOperationStatus.Unspecified =>
                WorkflowToolPendingOperationStatus.Unspecified,
            WorkflowToolCallAttemptPendingOperationStatus.SubmissionUncertain =>
                WorkflowToolPendingOperationStatus.SubmissionUncertain,
            WorkflowToolCallAttemptPendingOperationStatus.Queued =>
                WorkflowToolPendingOperationStatus.Queued,
            WorkflowToolCallAttemptPendingOperationStatus.Provisioning =>
                WorkflowToolPendingOperationStatus.Provisioning,
            WorkflowToolCallAttemptPendingOperationStatus.Preparing =>
                WorkflowToolPendingOperationStatus.Preparing,
            WorkflowToolCallAttemptPendingOperationStatus.Running =>
                WorkflowToolPendingOperationStatus.Running,
            WorkflowToolCallAttemptPendingOperationStatus.Collecting =>
                WorkflowToolPendingOperationStatus.Collecting,
            WorkflowToolCallAttemptPendingOperationStatus.Succeeded =>
                WorkflowToolPendingOperationStatus.Succeeded,
            WorkflowToolCallAttemptPendingOperationStatus.Failed =>
                WorkflowToolPendingOperationStatus.Failed,
            WorkflowToolCallAttemptPendingOperationStatus.Cancelled =>
                WorkflowToolPendingOperationStatus.Cancelled,
            WorkflowToolCallAttemptPendingOperationStatus.OutcomeUncertain =>
                WorkflowToolPendingOperationStatus.OutcomeUncertain,
            _ => (WorkflowToolPendingOperationStatus)(-1),
        };
        return (int)mapped >= 0;
    }

    private static bool TryMapAttemptPendingOperationRouteIdentitySource(
        WorkflowToolPendingOperationRouteIdentitySource source,
        out WorkflowToolCallAttemptPendingOperationRouteIdentitySource mapped)
    {
        mapped = source switch
        {
            WorkflowToolPendingOperationRouteIdentitySource.Unspecified =>
                WorkflowToolCallAttemptPendingOperationRouteIdentitySource.Unspecified,
            WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract =>
                WorkflowToolCallAttemptPendingOperationRouteIdentitySource.CodeExecutionContract,
            WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog =>
                WorkflowToolCallAttemptPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog,
            WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission =>
                WorkflowToolCallAttemptPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission,
            _ => (WorkflowToolCallAttemptPendingOperationRouteIdentitySource)(-1),
        };
        return (int)mapped >= 0;
    }

    private static bool TryMapPendingOperationRouteIdentitySource(
        WorkflowToolCallAttemptPendingOperationRouteIdentitySource source,
        out WorkflowToolPendingOperationRouteIdentitySource mapped)
    {
        mapped = source switch
        {
            WorkflowToolCallAttemptPendingOperationRouteIdentitySource.Unspecified =>
                WorkflowToolPendingOperationRouteIdentitySource.Unspecified,
            WorkflowToolCallAttemptPendingOperationRouteIdentitySource.CodeExecutionContract =>
                WorkflowToolPendingOperationRouteIdentitySource.CodeExecutionContract,
            WorkflowToolCallAttemptPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog =>
                WorkflowToolPendingOperationRouteIdentitySource.NyxIdUserServiceCatalog,
            WorkflowToolCallAttemptPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission =>
                WorkflowToolPendingOperationRouteIdentitySource.WorkflowCapabilityAdmission,
            _ => (WorkflowToolPendingOperationRouteIdentitySource)(-1),
        };
        return (int)mapped >= 0;
    }

    private static StepRequestEvent ToStepRequest(
        PendingToolCallExecutionState pending,
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
            TimeoutMs = pending.TimeoutMs,
            Parameters = { ["tool"] = pending.ToolName },
            InputFileRefs = { material?.InputFileRefs.Select(static fileRef => fileRef.Clone()) ?? [] },
            ExternalInvocation = material?.ExternalInvocation?.Clone(),
        };

    private static int ResolveToolTimeoutMs(StepRequestEvent request)
    {
        int? configuredTimeoutMs = null;
        foreach (var key in new[] { "tool_timeout_ms", "timeout_ms" })
        {
            if (request.Parameters.TryGetValue(key, out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
                timeoutMs > 0)
            {
                configuredTimeoutMs = timeoutMs;
                break;
            }
        }

        var effectiveTimeoutMs = request.TimeoutMs > 0
            ? configuredTimeoutMs.HasValue
                ? Math.Min(configuredTimeoutMs.Value, request.TimeoutMs)
                : request.TimeoutMs
            : configuredTimeoutMs ?? DefaultToolTimeoutMs;
        return Math.Clamp(effectiveTimeoutMs, MinToolTimeoutMs, MaxToolTimeoutMs);
    }

    private static WorkflowToolCallTimeoutFiredEvent BuildTimeoutEvent(PendingToolCallExecutionState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.CallId,
            TimeoutMs = pending.TimeoutMs,
            ContinuationId = pending.ContinuationId,
            Attempt = pending.Attempt,
        };

    private static WorkflowToolCallTimeoutFiredEvent BuildTimeoutEvent(PendingToolCallApprovalState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.ToolCallId,
            TimeoutMs = pending.TimeoutMs,
            ContinuationId = pending.ContinuationId,
            Attempt = pending.Attempt,
        };

    private static WorkflowToolCallRetryFiredEvent BuildRetryEvent(PendingToolCallExecutionState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.CallId,
            Attempt = pending.Attempt,
            ContinuationId = pending.ContinuationId,
        };

    private static WorkflowToolCallExecutionRecoveryFiredEvent BuildExecutionRecoveryEvent(
        PendingToolCallExecutionState pending) =>
        new()
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            ExecutionId = pending.ExecutionId,
            CallId = pending.CallId,
            Attempt = pending.Attempt,
            ContinuationId = pending.ContinuationId,
        };

    private static bool MatchesExecutionTimeout(
        EventEnvelope envelope,
        PendingToolCallExecutionState pending)
    {
        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callback) &&
               string.Equals(callback.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static bool MatchesApprovalTimeout(
        EventEnvelope envelope,
        PendingToolCallApprovalState pending,
        WorkflowToolCallTimeoutFiredEvent timeout)
    {
        if (!string.Equals(pending.ContinuationId, timeout.ContinuationId, StringComparison.Ordinal))
            return false;

        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        var callbackId = string.IsNullOrWhiteSpace(pending.TimeoutCallbackId)
            ? BuildToolTimeoutCallbackId(pending)
            : pending.TimeoutCallbackId;
        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callback) &&
               string.Equals(callback.CallbackId, callbackId, StringComparison.Ordinal);
    }

    private static bool MatchesExecutionRetry(
        EventEnvelope envelope,
        PendingToolCallExecutionState pending)
    {
        if (pending.RetryLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.RetryLease);

        if (RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callback) &&
            string.Equals(callback.CallbackId, pending.RetryCallbackId, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(
            envelope.Runtime?.DeliveryIdentity?.OperationId,
            pending.RetryCallbackId,
            StringComparison.Ordinal);
    }

    private static bool MatchesExecutionIdentity(
        PendingToolCallExecutionState pending,
        string? runId,
        string? stepId,
        string? callId,
        string? executionId) =>
        MatchesCallIdentity(
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            runId,
            stepId,
            callId,
            executionId);

    private static bool MatchesContinuationId(
        PendingToolCallExecutionState pending,
        string? continuationId) =>
        !string.IsNullOrWhiteSpace(pending.ContinuationId) &&
        string.Equals(pending.ContinuationId, continuationId?.Trim(), StringComparison.Ordinal);

    private static bool IsTrustedCompletionEnvelope(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        WorkflowToolCallAttemptCompletedEvent completed) =>
        envelope.Route.GetTopologyAudience() == TopologyAudience.Self &&
        string.Equals(envelope.Route?.PublisherActorId, ctx.AgentId, StringComparison.Ordinal) &&
        string.Equals(
            envelope.Runtime?.DeliveryIdentity?.OperationId,
            BuildCompletionSignalOperationId(completed),
            StringComparison.Ordinal);

    private static bool IsTrustedExecutionRecoveryEnvelope(
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        PendingToolCallExecutionState pending,
        bool requirePublisher = true)
    {
        var callbackId = BuildToolExecutionRecoveryCallbackId(pending);
        var trustedRoute = envelope.Route.GetTopologyAudience() == TopologyAudience.Self &&
                           (!requirePublisher || string.Equals(
                               envelope.Route?.PublisherActorId,
                               ctx.AgentId,
                               StringComparison.Ordinal));
        if (!trustedRoute)
            return false;

        if (string.Equals(
                envelope.Runtime?.DeliveryIdentity?.OperationId,
                callbackId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callback) &&
               string.Equals(callback.CallbackId, callbackId, StringComparison.Ordinal);
    }

    private static TimeSpan ResolveToolRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 2, 0, 4);
        return TimeSpan.FromMilliseconds(
            Math.Min(
                MaxToolRetryDelay.TotalMilliseconds,
                ToolRetryBaseDelay.TotalMilliseconds * (1 << exponent)));
    }

    private static string BuildToolTimeoutCallbackId(PendingToolCallExecutionState pending) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            ToolTimeoutCallbackPrefix,
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId);

    private static string BuildToolTimeoutCallbackId(PendingToolCallApprovalState pending) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            ToolTimeoutCallbackPrefix,
            pending.RunId,
            pending.StepId,
            pending.ToolCallId,
            pending.ExecutionId);

    private static string BuildToolRetryCallbackId(PendingToolCallExecutionState pending) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            ToolRetryCallbackPrefix,
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            pending.Attempt.ToString(CultureInfo.InvariantCulture));

    private static string BuildToolExecutionRecoveryCallbackId(PendingToolCallExecutionState pending) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            ToolExecutionRecoveryCallbackPrefix,
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            pending.Attempt.ToString(CultureInfo.InvariantCulture),
            pending.ContinuationId);

    private static string BuildToolStartedOperationId(PendingToolCallExecutionState pending) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "workflow-tool-call-started",
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId);

    private static string BuildCompletionSignalOperationId(WorkflowToolCallAttemptCompletedEvent completed) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "workflow-tool-attempt-completed",
            completed.RunId,
            completed.StepId,
            completed.CallId,
            completed.ExecutionId,
            completed.Attempt.ToString(CultureInfo.InvariantCulture),
            completed.ContinuationId);

    private static string BuildCompletionSignalCallbackId(WorkflowToolCallAttemptCompletedEvent completed) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "workflow-tool-completion-continuation",
            completed.RunId,
            completed.StepId,
            completed.CallId,
            completed.ExecutionId,
            completed.Attempt.ToString(CultureInfo.InvariantCulture),
            completed.ContinuationId);

    private static string BuildExecutionKey(string? callId, string? executionId) =>
        BuildCompletionKey(callId, executionId);

    private static EventEnvelopePublishOptions BuildExecutionCallbackOptions(string operationId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = operationId,
            },
        };
}
