using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ConnectorCallModule
{
    private const string ApprovalStatusCallbackPrefix = "workflow-connector-approval-status";

    private async Task BeginConnectorApprovalAsync(
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        IConnector connector,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var actionId = BuildApprovalActionId(
            runId,
            request.StepId,
            request.ExecutionId,
            request.IdempotencyKey);
        var initialState = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        initialState.ApprovalsByActionId.TryGetValue(actionId, out var existingCoordination);

        ConnectorApprovalMaterialBundle bundle;
        try
        {
            bundle = await BuildApprovalMaterialAsync(
                request,
                runId,
                connectorName,
                operation,
                connector,
                attempts,
                timeoutMs,
                onErrorContinue,
                isSecureStep,
                existingCoordination?.Snapshot?.Plan,
                ctx,
                ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall approval preflight failed. run={RunId} step={StepId} failure_type={FailureType}",
                runId,
                request.StepId,
                ex.GetType().Name);
            await PublishFailureAsync(ctx, request, "connector approval preflight failed", ct);
            return;
        }

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (state.ApprovalsByActionId.TryGetValue(actionId, out existingCoordination))
        {
            await HandleExistingApprovalAsync(state, existingCoordination, bundle, ctx, ct);
            return;
        }

        var stepKey = BuildStepKey(runId, request.StepId);
        if (state.ApprovalActionIdByStepId.TryGetValue(stepKey, out var previousActionId) &&
            state.ApprovalsByActionId.TryGetValue(previousActionId, out var previousCoordination))
        {
            await CancelSupersededApprovalAsync(state, previousCoordination, ctx, ct);
            state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        }

        RuntimeSecretReference materialReference;
        try
        {
            materialReference = await StoreApprovalMaterialAsync(bundle, ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall approval material store failed. run={RunId} step={StepId} failure_type={FailureType}",
                runId,
                request.StepId,
                ex.GetType().Name);
            await PublishFailureAsync(ctx, request, "connector approval material is unavailable", ct);
            return;
        }

        var options = request.StepParameters!.ConnectorApproval;
        var coordination = new ConnectorApprovalCoordinationState
        {
            Snapshot = new WorkflowExternalActionApprovalSnapshot
            {
                Plan = bundle.Plan.Clone(),
                LifecycleStatus = WorkflowExternalActionLifecycleStatus.WaitingApproval,
                ApprovalStatus = WorkflowExternalActionApprovalStatus.Pending,
                ExecutionStatus = WorkflowExternalActionExecutionStatus.NotStarted,
            },
            MaterialReference = materialReference,
            Attempts = attempts,
            TimeoutMs = timeoutMs,
            OnErrorContinue = onErrorContinue,
            SecureStep = isSecureStep,
            ConnectorType = connector.Type,
            StatusCheckIntervalSeconds = options.StatusCheckIntervalSeconds,
        };
        state.ApprovalsByActionId[actionId] = coordination;
        state.ApprovalActionIdByStepId[stepKey] = actionId;
        await SaveStateAsync(state, ctx, ct);

        if (_remoteToolApprovalPort == null)
        {
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_path_unavailable",
                bundle.Material,
                ctx,
                ct);
            return;
        }

        RemoteToolApprovalSubmission submission;
        try
        {
            var remoteRequest = BuildRemoteApprovalRequest(bundle.Plan);
            submission = await WithRemoteApprovalContextAsync(
                bundle.Plan,
                ctx,
                () => _remoteToolApprovalPort.SubmitAsync(remoteRequest, ct),
                ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall remote approval submission is indeterminate. action={ActionId} failure_type={FailureType}",
                actionId,
                ex.GetType().Name);
            state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
            if (state.ApprovalsByActionId.TryGetValue(actionId, out coordination))
            {
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Failed,
                    WorkflowExternalActionLifecycleStatus.Failed,
                    "approval_submission_indeterminate",
                    bundle.Material,
                    ctx,
                    ct);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(submission.RemoteApprovalId) ||
            submission.ExpiresAt == null ||
            submission.ExpiresAt <= ctx.UtcNow)
        {
            state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
            if (state.ApprovalsByActionId.TryGetValue(actionId, out coordination))
            {
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Failed,
                    WorkflowExternalActionLifecycleStatus.Failed,
                    "approval_remote_binding_invalid",
                    bundle.Material,
                    ctx,
                    ct);
            }
            return;
        }

        state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.ApprovalsByActionId.TryGetValue(actionId, out coordination) ||
            coordination.Snapshot?.LifecycleStatus != WorkflowExternalActionLifecycleStatus.WaitingApproval)
        {
            return;
        }

        coordination.Snapshot.RemoteApprovalId = submission.RemoteApprovalId.Trim();
        coordination.Snapshot.RemoteExpiresAt = Timestamp.FromDateTimeOffset(submission.ExpiresAt.Value);
        await SaveStateAsync(state, ctx, ct);
        await ScheduleApprovalStatusCheckAsync(state, coordination, checkNumber: 1, ctx, ct);
    }

    private async Task HandleExistingApprovalAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        ConnectorApprovalMaterialBundle bundle,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var snapshot = coordination.Snapshot;
        var plan = snapshot?.Plan;
        if (snapshot == null ||
            plan == null ||
            !FixedTimeDigestEquals(plan.MaterialDigestSha256 ?? string.Empty, bundle.Plan.MaterialDigestSha256))
        {
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_plan_mismatch",
                bundle.Material,
                ctx,
                ct);
            return;
        }

        switch (snapshot.LifecycleStatus)
        {
            case WorkflowExternalActionLifecycleStatus.WaitingApproval:
                if (string.IsNullOrWhiteSpace(snapshot.RemoteApprovalId))
                {
                    await CompleteApprovalWithoutExecutionAsync(
                        state,
                        coordination,
                        WorkflowExternalActionApprovalStatus.Failed,
                        WorkflowExternalActionLifecycleStatus.Failed,
                        "approval_submission_indeterminate",
                        bundle.Material,
                        ctx,
                        ct);
                }
                else if (coordination.StatusCheckLease == null)
                {
                    await ScheduleApprovalStatusCheckAsync(
                        state,
                        coordination,
                        Math.Max(1, coordination.StatusCheckCount),
                        ctx,
                        ct);
                }
                return;

            case WorkflowExternalActionLifecycleStatus.Approved:
                await StartApprovedExecutionAsync(state, coordination, attempt: 1, ctx, ct);
                return;

            case WorkflowExternalActionLifecycleStatus.Executing:
                var pending = FindPendingApprovalExecution(state, plan.ActionId);
                if (pending == null)
                {
                    await StartApprovedExecutionAsync(
                        state,
                        coordination,
                        Math.Max(1, coordination.CurrentAttempt),
                        ctx,
                        ct);
                }
                else if (!pending.RequestDispatched)
                {
                    await ResumeUndispatchedApprovedExecutionAsync(
                        pending,
                        state,
                        coordination,
                        ctx,
                        ct);
                }
                return;
        }

        if (!coordination.StepCompletionPublished)
        {
            if (coordination.CompletionReference != null ||
                snapshot.ExecutionStatus is WorkflowExternalActionExecutionStatus.Succeeded or
                    WorkflowExternalActionExecutionStatus.Failed)
            {
                await RecoverUnpublishedApprovalTerminalAsync(
                    state,
                    coordination,
                    bundle.Material,
                    ctx,
                    ct);
            }
            else
            {
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    snapshot.ApprovalStatus,
                    snapshot.LifecycleStatus,
                    snapshot.ApprovalReasonCode,
                    bundle.Material,
                    ctx,
                    ct);
            }
            return;
        }

        if (coordination.MaterialReference != null || coordination.CompletionReference != null)
        {
            await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
            await SaveStateAsync(state, ctx, ct);
        }
    }

    private async Task HandleApprovalStatusCheckAsync(
        WorkflowConnectorApprovalStatusCheckFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ActionId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.ApprovalsByActionId.TryGetValue(evt.ActionId, out var coordination) ||
            !MatchesApprovalStatusCheck(evt, envelope, coordination))
        {
            return;
        }

        var snapshot = coordination.Snapshot;
        var plan = snapshot.Plan;
        if (IsTerminalLifecycle(snapshot.LifecycleStatus))
        {
            if (!coordination.StepCompletionPublished)
            {
                var terminalMaterial = await ResolveAndVerifyMaterialAsync(
                    coordination,
                    ctx,
                    ct,
                    requireUnexpired: false);
                await RecoverUnpublishedApprovalTerminalAsync(
                    state,
                    coordination,
                    terminalMaterial.Material,
                    ctx,
                    ct);
            }
            else if (coordination.MaterialReference != null || coordination.CompletionReference != null)
            {
                await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
                await SaveStateAsync(state, ctx, ct);
            }
            return;
        }

        if (snapshot.LifecycleStatus != WorkflowExternalActionLifecycleStatus.WaitingApproval ||
            snapshot.ApprovalStatus != WorkflowExternalActionApprovalStatus.Pending)
        {
            return;
        }

        if (!MatchesCurrentApprovalAuthority(plan, ctx))
        {
            var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_authority_mismatch",
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        if (ResolveEffectiveExpiry(snapshot) <= ctx.UtcNow)
        {
            var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Expired,
                WorkflowExternalActionLifecycleStatus.Expired,
                "approval_expired",
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        if (_remoteToolApprovalPort == null)
        {
            var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_path_unavailable",
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        RemoteToolApprovalStatusSnapshot remoteStatus;
        try
        {
            remoteStatus = await WithRemoteApprovalContextAsync(
                plan,
                ctx,
                () => _remoteToolApprovalPort.GetStatusAsync(
                    new RemoteToolApprovalStatusQuery(plan.ActionId, snapshot.RemoteApprovalId),
                    ct),
                ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall remote approval status is unavailable. action={ActionId} failure_type={FailureType}",
                plan.ActionId,
                ex.GetType().Name);
            var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_status_unavailable",
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        if (remoteStatus.ExpiresAt == null ||
            remoteStatus.ExpiresAt.Value != snapshot.RemoteExpiresAt.ToDateTimeOffset())
        {
            var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Failed,
                WorkflowExternalActionLifecycleStatus.Failed,
                "approval_remote_expiry_mismatch",
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        switch (remoteStatus.Status)
        {
            case RemoteToolApprovalStatus.Approved:
                if (ResolveEffectiveExpiry(snapshot) <= ctx.UtcNow)
                {
                    var expiredMaterial = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
                    await CompleteApprovalWithoutExecutionAsync(
                        state,
                        coordination,
                        WorkflowExternalActionApprovalStatus.Expired,
                        WorkflowExternalActionLifecycleStatus.Expired,
                        "approval_expired",
                        expiredMaterial.Material,
                        ctx,
                        ct);
                    return;
                }

                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    coordination.StatusCheckLease,
                    "connector approval status callback",
                    ct);
                coordination.StatusCheckLease = null;
                snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Approved;
                snapshot.ApprovalReasonCode = "approval_approved";
                snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
                snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Approved;
                await SaveStateAsync(state, ctx, ct);
                await StartApprovedExecutionAsync(state, coordination, attempt: 1, ctx, ct);
                return;

            case RemoteToolApprovalStatus.Rejected:
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Denied,
                    WorkflowExternalActionLifecycleStatus.Denied,
                    "approval_denied",
                    (await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false)).Material,
                    ctx,
                    ct);
                return;

            case RemoteToolApprovalStatus.Expired:
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Expired,
                    WorkflowExternalActionLifecycleStatus.Expired,
                    "approval_expired",
                    (await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false)).Material,
                    ctx,
                    ct);
                return;

            case RemoteToolApprovalStatus.Cancelled:
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Cancelled,
                    WorkflowExternalActionLifecycleStatus.Cancelled,
                    "approval_cancelled",
                    (await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false)).Material,
                    ctx,
                    ct);
                return;

            case RemoteToolApprovalStatus.Pending:
                await ScheduleApprovalStatusCheckAsync(state, coordination, evt.CheckNumber + 1, ctx, ct);
                return;

            case RemoteToolApprovalStatus.Unknown:
                await CompleteApprovalWithoutExecutionAsync(
                    state,
                    coordination,
                    WorkflowExternalActionApprovalStatus.Failed,
                    WorkflowExternalActionLifecycleStatus.Failed,
                    "approval_status_unknown",
                    (await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false)).Material,
                    ctx,
                    ct);
                return;
        }
    }

    private async Task ScheduleApprovalStatusCheckAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        int checkNumber,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var snapshot = coordination.Snapshot;
        var plan = snapshot.Plan;
        var remaining = ResolveEffectiveExpiry(snapshot) - ctx.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            var material = (await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false)).Material;
            await CompleteApprovalWithoutExecutionAsync(
                state,
                coordination,
                WorkflowExternalActionApprovalStatus.Expired,
                WorkflowExternalActionLifecycleStatus.Expired,
                "approval_expired",
                material,
                ctx,
                ct);
            return;
        }

        var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
            ApprovalStatusCallbackPrefix,
            plan.ActionId,
            snapshot.RemoteApprovalId,
            checkNumber.ToString());
        coordination.StatusCheckCount = checkNumber;
        coordination.StatusCheckCallbackId = callbackId;
        coordination.StatusCheckLease = null;
        await SaveStateAsync(state, ctx, ct);

        var configuredDelay = TimeSpan.FromSeconds(coordination.StatusCheckIntervalSeconds);
        var dueTime = configuredDelay <= remaining ? configuredDelay : remaining;
        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            dueTime,
            new WorkflowConnectorApprovalStatusCheckFiredEvent
            {
                ActionId = plan.ActionId,
                RemoteApprovalId = snapshot.RemoteApprovalId,
                MaterialDigestSha256 = plan.MaterialDigestSha256,
                PrincipalSubject = plan.Provenance.PrincipalSubject,
                ScopeId = plan.Provenance.ScopeId,
                NodeId = plan.NodeId,
                CheckNumber = checkNumber,
                ServiceRef = plan.ServiceRef,
                PermissionScope = plan.PermissionScope,
                RemoteExpiresAt = snapshot.RemoteExpiresAt.Clone(),
            },
            ct: ct);

        state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.ApprovalsByActionId.TryGetValue(plan.ActionId, out coordination) ||
            coordination.StatusCheckCount != checkNumber ||
            !string.Equals(coordination.StatusCheckCallbackId, callbackId, StringComparison.Ordinal) ||
            coordination.Snapshot?.LifecycleStatus != WorkflowExternalActionLifecycleStatus.WaitingApproval)
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                lease,
                "stale connector approval status callback",
                ct);
            return;
        }

        coordination.StatusCheckLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        await SaveStateAsync(state, ctx, ct);
    }

    private static bool MatchesApprovalStatusCheck(
        WorkflowConnectorApprovalStatusCheckFiredEvent evt,
        EventEnvelope envelope,
        ConnectorApprovalCoordinationState coordination)
    {
        var snapshot = coordination.Snapshot;
        var plan = snapshot?.Plan;
        if (snapshot == null || plan == null || plan.Provenance == null || snapshot.RemoteExpiresAt == null)
            return false;

        var leaseMatches = coordination.StatusCheckLease != null
            ? WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, coordination.StatusCheckLease)
            : RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
              string.Equals(callbackState.CallbackId, coordination.StatusCheckCallbackId, StringComparison.Ordinal);
        return leaseMatches &&
               coordination.StatusCheckCount == evt.CheckNumber &&
               string.Equals(evt.ActionId, plan.ActionId, StringComparison.Ordinal) &&
               string.Equals(evt.RemoteApprovalId, snapshot.RemoteApprovalId, StringComparison.Ordinal) &&
               FixedTimeDigestEquals(evt.MaterialDigestSha256 ?? string.Empty, plan.MaterialDigestSha256 ?? string.Empty) &&
               string.Equals(evt.PrincipalSubject, plan.Provenance.PrincipalSubject, StringComparison.Ordinal) &&
               string.Equals(evt.ScopeId, plan.Provenance.ScopeId, StringComparison.Ordinal) &&
               string.Equals(evt.NodeId, plan.NodeId, StringComparison.Ordinal) &&
               string.Equals(evt.ServiceRef, plan.ServiceRef, StringComparison.Ordinal) &&
               string.Equals(evt.PermissionScope, plan.PermissionScope, StringComparison.Ordinal) &&
               evt.RemoteExpiresAt != null &&
               evt.RemoteExpiresAt.Equals(snapshot.RemoteExpiresAt);
    }

    private async Task CompleteApprovalWithoutExecutionAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        WorkflowExternalActionApprovalStatus approvalStatus,
        WorkflowExternalActionLifecycleStatus lifecycleStatus,
        string reasonCode,
        ConnectorCallProtectedMaterial? material,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            coordination.StatusCheckLease,
            "connector approval status callback",
            ct);
        coordination.StatusCheckLease = null;
        coordination.Snapshot.ApprovalStatus = approvalStatus;
        coordination.Snapshot.ApprovalReasonCode = reasonCode;
        coordination.Snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
        coordination.Snapshot.LifecycleStatus = lifecycleStatus;
        coordination.Snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.NotStarted;
        await SaveStateAsync(state, ctx, ct);

        if (!coordination.StepCompletionPublished)
        {
            await PublishApprovalStepFailureAsync(coordination, material, reasonCode, ctx, ct);
            coordination.StepCompletionPublished = true;
            await SaveStateAsync(state, ctx, ct);
        }

        await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task RecoverUnpublishedApprovalTerminalAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        ConnectorCallProtectedMaterial? material,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var snapshot = coordination.Snapshot;
        if (coordination.CompletionReference != null)
        {
            var completion = await ResolveApprovedCompletionAsync(coordination, ctx, ct);
            await ctx.PublishAsync(completion, TopologyAudience.Self, ct);
        }
        else
        {
            if (snapshot.ExecutionStatus == WorkflowExternalActionExecutionStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    "Approved connector succeeded without durable completion material.");
            }

            var reasonCode = snapshot.ExecutionStatus == WorkflowExternalActionExecutionStatus.Failed
                ? snapshot.ExecutionReasonCode
                : snapshot.ApprovalReasonCode;
            await PublishApprovalStepFailureAsync(coordination, material, reasonCode, ctx, ct);
        }

        coordination.StepCompletionPublished = true;
        foreach (var pending in state.PendingByOperationId.Values
                     .Where(candidate => string.Equals(
                         candidate.ApprovalActionId,
                         snapshot.Plan.ActionId,
                         StringComparison.Ordinal))
                     .ToList())
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pending.TimeoutLease,
                "recovered approved connector terminal timeout",
                ct);
            RemovePending(state, pending);
        }

        await SaveStateAsync(state, ctx, ct);
        await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private static async Task PublishApprovalStepFailureAsync(
        ConnectorApprovalCoordinationState coordination,
        ConnectorCallProtectedMaterial? material,
        string reasonCode,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var plan = coordination.Snapshot.Plan;
        var continueOnError = material?.OnErrorContinue == true;
        var completed = new StepCompletedEvent
        {
            StepId = plan.Provenance.StepId,
            RunId = plan.Provenance.RunId,
            ExecutionId = plan.Provenance.ExecutionId,
            Success = continueOnError,
            Output = continueOnError ? material?.Input ?? string.Empty : string.Empty,
            Error = continueOnError ? string.Empty : DescribeApprovalFailure(reasonCode),
            OutputProvenance = continueOnError
                ? WorkflowStepOutputProvenance.ForwardedInput
                : WorkflowStepOutputProvenance.Produced,
        };
        completed.Annotations["connector.name"] = plan.ConnectorName;
        completed.Annotations["connector.type"] = plan.ConnectorType;
        completed.Annotations["connector.operation"] = plan.Operation;
        completed.Annotations["connector.approval.action_id"] = plan.ActionId;
        completed.Annotations["connector.approval.status"] = coordination.Snapshot.ApprovalStatus.ToString();
        completed.Annotations["connector.approval.reason_code"] = reasonCode;
        if (continueOnError)
        {
            completed.Annotations["connector.continued_on_error"] = "true";
            completed.Annotations["connector.error"] = DescribeApprovalFailure(reasonCode);
        }

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }

    private async Task CancelSupersededApprovalAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            coordination.StatusCheckLease,
            "superseded connector approval status callback",
            ct);
        foreach (var pending in state.PendingByOperationId.Values
                     .Where(pending => string.Equals(
                         pending.ApprovalActionId,
                         coordination.Snapshot.Plan.ActionId,
                         StringComparison.Ordinal))
                     .ToList())
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                pending.TimeoutLease,
                "superseded approved connector timeout",
                ct);
            RemovePending(state, pending);
        }

        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Cancelled;
        if (coordination.Snapshot.ApprovalStatus == WorkflowExternalActionApprovalStatus.Pending)
        {
            coordination.Snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Cancelled;
            coordination.Snapshot.ApprovalReasonCode = "approval_superseded";
            coordination.Snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
        }
        if (coordination.Snapshot.ExecutionStatus == WorkflowExternalActionExecutionStatus.Executing)
        {
            coordination.Snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.Failed;
            coordination.Snapshot.ExecutionReasonCode = "approval_superseded";
            coordination.Snapshot.ExecutionCompletedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
        }
        coordination.StepCompletionPublished = true;
        await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleApprovalRunTerminatedAsync(
        string runId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        var changed = false;
        foreach (var coordination in state.ApprovalsByActionId.Values.ToList())
        {
            var snapshot = coordination.Snapshot;
            if (snapshot?.Plan?.Provenance == null ||
                !string.Equals(snapshot.Plan.Provenance.RunId, runId, StringComparison.Ordinal))
            {
                continue;
            }

            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                ctx,
                coordination.StatusCheckLease,
                "terminated connector approval status callback",
                ct);
            foreach (var pending in state.PendingByOperationId.Values
                         .Where(pending => string.Equals(
                             pending.ApprovalActionId,
                             snapshot.Plan.ActionId,
                             StringComparison.Ordinal))
                         .ToList())
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    ctx,
                    pending.TimeoutLease,
                    "terminated approved connector timeout",
                    ct);
                RemovePending(state, pending);
            }

            if (!IsTerminalLifecycle(snapshot.LifecycleStatus))
            {
                snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Cancelled;
                if (snapshot.ApprovalStatus == WorkflowExternalActionApprovalStatus.Pending)
                {
                    snapshot.ApprovalStatus = WorkflowExternalActionApprovalStatus.Cancelled;
                    snapshot.ApprovalReasonCode = "workflow_run_terminated";
                    snapshot.ApprovalResolvedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
                }
                if (snapshot.ExecutionStatus == WorkflowExternalActionExecutionStatus.Executing)
                {
                    snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.Failed;
                    snapshot.ExecutionReasonCode = "workflow_run_terminated";
                    snapshot.ExecutionCompletedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
                }
            }

            coordination.StepCompletionPublished = true;
            await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
            changed = true;
        }

        if (changed)
            await SaveStateAsync(state, ctx, ct);
    }

    private static bool IsTerminalLifecycle(WorkflowExternalActionLifecycleStatus status) =>
        status is WorkflowExternalActionLifecycleStatus.Denied or
            WorkflowExternalActionLifecycleStatus.Expired or
            WorkflowExternalActionLifecycleStatus.Cancelled or
            WorkflowExternalActionLifecycleStatus.Succeeded or
            WorkflowExternalActionLifecycleStatus.Failed;

    private static string DescribeApprovalFailure(string reasonCode) =>
        reasonCode switch
        {
            "approval_denied" => "Connector action approval was denied.",
            "approval_expired" => "Connector action approval expired.",
            "approval_cancelled" => "Connector action approval was cancelled.",
            "approval_path_unavailable" => "Connector action approval path is unavailable.",
            "approval_status_unavailable" => "Connector action approval status is unavailable.",
            "approval_submission_indeterminate" => "Connector action approval submission was indeterminate.",
            "approval_authority_mismatch" => "Connector action approval authority no longer matches.",
            "approval_plan_mismatch" => "Connector action approval plan no longer matches.",
            "approval_material_digest_mismatch" => "Connector action material failed digest verification.",
            _ => "Connector action approval failed closed.",
        };
}
