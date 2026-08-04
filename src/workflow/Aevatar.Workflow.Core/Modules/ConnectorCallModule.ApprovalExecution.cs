using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ConnectorCallModule
{
    private async Task StartApprovedExecutionAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        int attempt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var snapshot = coordination.Snapshot;
        var plan = snapshot.Plan;
        var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: true);
        if (materialResult.Material == null || ResolveEffectiveExpiry(snapshot) <= ctx.UtcNow)
        {
            await FailApprovedBeforeDispatchAsync(
                state,
                coordination,
                materialResult.ReasonCode.Length == 0 ? "approval_expired" : materialResult.ReasonCode,
                materialResult.Material,
                ctx,
                ct);
            return;
        }

        var material = materialResult.Material;
        var connector = await _connectorResolver.ResolveAsync(ctx, material.ConnectorName, ct);
        if (connector == null || !string.Equals(connector.Type, material.ConnectorType, StringComparison.Ordinal))
        {
            await FailApprovedBeforeDispatchAsync(
                state,
                coordination,
                connector == null ? "connector_unavailable" : "connector_binding_mismatch",
                material,
                ctx,
                ct);
            return;
        }

        var request = BuildApprovedStepRequest(material);
        var pending = await RegisterPendingAsync(
            new EventEnvelope { Id = plan.ActionId },
            request,
            material.RunId,
            material.ConnectorName,
            material.Operation,
            material.ConnectorType,
            attempt,
            material.Attempts,
            material.TimeoutMs,
            material.OnErrorContinue,
            material.SecureStep,
            plan.CreatedAt?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0,
            ctx,
            ct,
            plan.ActionId);

        state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.ApprovalsByActionId.TryGetValue(plan.ActionId, out coordination))
            return;

        var verified = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: true);
        if (verified.Material == null || ResolveEffectiveExpiry(coordination.Snapshot) <= ctx.UtcNow)
        {
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector material failed verification before dispatch.",
                durationMs: 0,
                responseAnnotations: new Dictionary<string, string>(),
                reasonCode: verified.ReasonCode.Length == 0 ? "approval_expired" : verified.ReasonCode,
                ctx,
                ct);
            return;
        }

        material = verified.Material;
        coordination.CurrentAttempt = attempt;
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Executing;
        coordination.Snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.Executing;
        coordination.Snapshot.ExecutionReasonCode = "connector_executing";
        coordination.Snapshot.ExecutionStartedAt ??= Timestamp.FromDateTimeOffset(ctx.UtcNow);
        await SaveStateAsync(state, ctx, ct);

        await DispatchApprovedConnectorAsync(
            pending,
            state,
            coordination,
            material,
            connector,
            ctx,
            ct);
    }

    private async Task ResumeUndispatchedApprovedExecutionAsync(
        PendingConnectorCallState pending,
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        pending = await EnsurePendingTimeoutScheduledAsync(pending, ctx, ct);
        state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.ApprovalsByActionId.TryGetValue(pending.ApprovalActionId, out coordination))
            return;

        var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: true);
        if (materialResult.Material == null || ResolveEffectiveExpiry(coordination.Snapshot) <= ctx.UtcNow)
        {
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector material failed verification before resumed dispatch.",
                durationMs: 0,
                responseAnnotations: new Dictionary<string, string>(),
                reasonCode: materialResult.ReasonCode.Length == 0 ? "approval_expired" : materialResult.ReasonCode,
                ctx,
                ct);
            return;
        }

        var connector = await _connectorResolver.ResolveAsync(ctx, materialResult.Material.ConnectorName, ct);
        if (connector == null ||
            !string.Equals(connector.Type, materialResult.Material.ConnectorType, StringComparison.Ordinal))
        {
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector binding is unavailable before resumed dispatch.",
                durationMs: 0,
                responseAnnotations: new Dictionary<string, string>(),
                reasonCode: connector == null ? "connector_unavailable" : "connector_binding_mismatch",
                ctx,
                ct);
            return;
        }

        await DispatchApprovedConnectorAsync(
            pending,
            state,
            coordination,
            materialResult.Material,
            connector,
            ctx,
            ct);
    }

    private async Task DispatchApprovedConnectorAsync(
        PendingConnectorCallState pending,
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        ConnectorCallProtectedMaterial material,
        IConnector connector,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var plan = coordination.Snapshot.Plan;

        string httpAuthorization;
        try
        {
            httpAuthorization = await ReconstructConnectorHttpAuthorizationAsync(ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall approved execution authorization failed. action={ActionId} failure_type={FailureType}",
                plan.ActionId,
                ex.GetType().Name);
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector authorization is unavailable.",
                durationMs: 0,
                responseAnnotations: new Dictionary<string, string>(),
                reasonCode: "connector_authorization_unavailable",
                ctx,
                ct);
            return;
        }

        if (!MatchesCurrentApprovalAuthority(plan, ctx) ||
            ResolveEffectiveExpiry(coordination.Snapshot) <= ctx.UtcNow ||
            IsExpired(plan.ExpiresAt, ctx.UtcNow))
        {
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector authority or expiry changed before dispatch.",
                durationMs: 0,
                responseAnnotations: new Dictionary<string, string>(),
                reasonCode: MatchesCurrentApprovalAuthority(plan, ctx)
                    ? "approval_expired"
                    : "approval_authority_mismatch",
                ctx,
                ct);
            return;
        }

        var connectorRequest = new ConnectorRequest
        {
            HttpAuthorization = httpAuthorization,
            RunId = material.RunId,
            StepId = material.StepId,
            Connector = material.ConnectorName,
            Operation = material.Operation,
            Payload = material.Payload,
            Parameters = material.Parameters.ToDictionary(
                static parameter => parameter.Key,
                static parameter => parameter.Value,
                StringComparer.Ordinal),
            IdempotencyKey = material.IdempotencyKey,
            IssuedAtUnixMs = pending.IssuedAtUnixMs,
        };
        _ = ExecuteConnectorAndSignalAsync(ctx, connector, connectorRequest, pending);
        await MarkConnectorRequestDispatchedAsync(pending, ctx, ct);
    }

    private static StepRequestEvent BuildApprovedStepRequest(ConnectorCallProtectedMaterial material)
    {
        var request = new StepRequestEvent
        {
            StepId = material.StepId,
            StepType = material.SecureStep ? "secure_connector_call" : "connector_call",
            RunId = material.RunId,
            Input = material.Input,
            ExecutionId = material.ExecutionId,
            IdempotencyKey = material.IdempotencyKey,
        };
        foreach (var parameter in material.Parameters)
            request.Parameters[parameter.Key] = parameter.Value;
        return request;
    }

    private static PendingConnectorCallState? FindPendingApprovalExecution(
        ConnectorCallModuleState state,
        string actionId) =>
        state.PendingByOperationId.Values.FirstOrDefault(pending =>
            string.Equals(pending.ApprovalActionId, actionId, StringComparison.Ordinal));

    private async Task HandleApprovedAttemptCompletedAsync(
        WorkflowConnectorAttemptCompletedEvent evt,
        PendingConnectorCallState pending,
        ConnectorCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.ApprovalsByActionId.TryGetValue(pending.ApprovalActionId, out var coordination))
            return;
        if (coordination.StepCompletionPublished)
        {
            if (coordination.MaterialReference != null || coordination.CompletionReference != null)
            {
                await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
                await SaveStateAsync(state, ctx, ct);
            }
            return;
        }
        if (IsTerminalLifecycle(coordination.Snapshot.LifecycleStatus))
        {
            await RecoverUnpublishedApprovalTerminalAsync(state, coordination, null, ctx, ct);
            return;
        }

        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            pending.TimeoutLease,
            "approved connector timeout",
            ct);
        var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
        var material = materialResult.Material;
        if (material == null)
        {
            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: false,
                output: string.Empty,
                error: "Approved connector material is unavailable.",
                ParseDuration(evt),
                evt.Annotations,
                materialResult.ReasonCode,
                ctx,
                ct);
            return;
        }

        var durationMs = ParseDuration(evt);
        if (evt.Success)
        {
            var resolvedOutput = evt.Output ?? string.Empty;
            if (!TryAssertResponseOutput(
                    material.Parameters.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal),
                    resolvedOutput,
                    out var assertionError))
            {
                await FinalizeApprovedExecutionAsync(
                    pending,
                    state,
                    coordination,
                    success: false,
                    output: string.Empty,
                    error: assertionError,
                    durationMs,
                    evt.Annotations,
                    "connector_response_assertion_failed",
                    ctx,
                    ct);
                return;
            }

            if (ParseBool(material.Parameters.FirstOrDefault(static x => x.Key == "pass_through_input")?.Value ?? "false"))
                resolvedOutput = material.Input;

            await FinalizeApprovedExecutionAsync(
                pending,
                state,
                coordination,
                success: true,
                output: resolvedOutput,
                error: string.Empty,
                durationMs,
                evt.Annotations,
                "connector_succeeded",
                ctx,
                ct);
            return;
        }

        var error = string.IsNullOrWhiteSpace(evt.Error) ? "connector call failed" : evt.Error;
        if (pending.Attempt < pending.Attempts &&
            CanRetry(evt) &&
            ResolveEffectiveExpiry(coordination.Snapshot) > ctx.UtcNow &&
            !IsExpired(coordination.Snapshot.Plan.ExpiresAt, ctx.UtcNow))
        {
            await StartApprovedExecutionAsync(state, coordination, pending.Attempt + 1, ctx, ct);
            return;
        }

        await FinalizeApprovedExecutionAsync(
            pending,
            state,
            coordination,
            success: false,
            output: string.Empty,
            error,
            durationMs,
            evt.Annotations,
            "connector_failed",
            ctx,
            ct);
    }

    private async Task HandleApprovedTimeoutAsync(
        PendingConnectorCallState pending,
        WorkflowConnectorTimeoutFiredEvent evt,
        ConnectorCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!state.ApprovalsByActionId.TryGetValue(pending.ApprovalActionId, out var coordination) ||
            coordination.StepCompletionPublished)
        {
            if (coordination != null &&
                (coordination.MaterialReference != null || coordination.CompletionReference != null))
            {
                await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
                await SaveStateAsync(state, ctx, ct);
            }
            return;
        }

        if (!pending.RequestDispatched)
        {
            await StartApprovedExecutionAsync(
                state,
                coordination,
                Math.Max(1, pending.Attempt),
                ctx,
                ct);
            return;
        }

        var materialResult = await ResolveAndVerifyMaterialAsync(coordination, ctx, ct, requireUnexpired: false);
        if (pending.Attempt < pending.Attempts &&
            materialResult.Material != null &&
            ResolveEffectiveExpiry(coordination.Snapshot) > ctx.UtcNow &&
            !IsExpired(coordination.Snapshot.Plan.ExpiresAt, ctx.UtcNow))
        {
            await StartApprovedExecutionAsync(state, coordination, pending.Attempt + 1, ctx, ct);
            return;
        }

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.timeout_fired"] = "true",
        };
        await FinalizeApprovedExecutionAsync(
            pending,
            state,
            coordination,
            success: false,
            output: string.Empty,
            error: $"connector call timed out after {evt.TimeoutMs}ms",
            durationMs: evt.TimeoutMs,
            annotations,
            materialResult.Material == null ? materialResult.ReasonCode : "connector_timeout",
            ctx,
            ct);
    }

    private async Task FailApprovedBeforeDispatchAsync(
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        string reasonCode,
        ConnectorCallProtectedMaterial? material,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        coordination.Snapshot.LifecycleStatus = WorkflowExternalActionLifecycleStatus.Failed;
        coordination.Snapshot.ExecutionStatus = WorkflowExternalActionExecutionStatus.Failed;
        coordination.Snapshot.ExecutionReasonCode = reasonCode;
        coordination.Snapshot.ExecutionCompletedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);
        await SaveStateAsync(state, ctx, ct);
        await PublishApprovalStepFailureAsync(coordination, material, reasonCode, ctx, ct);
        coordination.StepCompletionPublished = true;
        await SaveStateAsync(state, ctx, ct);
        await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task FinalizeApprovedExecutionAsync(
        PendingConnectorCallState pending,
        ConnectorCallModuleState state,
        ConnectorApprovalCoordinationState coordination,
        bool success,
        string output,
        string error,
        double durationMs,
        IReadOnlyDictionary<string, string> responseAnnotations,
        string reasonCode,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            ctx,
            pending.TimeoutLease,
            "approved connector terminal timeout",
            ct);
        var snapshot = coordination.Snapshot;
        snapshot.LifecycleStatus = success
            ? WorkflowExternalActionLifecycleStatus.Succeeded
            : WorkflowExternalActionLifecycleStatus.Failed;
        snapshot.ExecutionStatus = success
            ? WorkflowExternalActionExecutionStatus.Succeeded
            : WorkflowExternalActionExecutionStatus.Failed;
        snapshot.ExecutionReasonCode = reasonCode;
        snapshot.ExecutionCompletedAt = Timestamp.FromDateTimeOffset(ctx.UtcNow);

        var completion = BuildPendingCompletion(
            pending,
            success,
            output,
            error,
            durationMs,
            responseAnnotations);
        completion.Annotations["connector.approval.action_id"] = snapshot.Plan.ActionId;
        completion.Annotations["connector.approval.status"] = snapshot.ApprovalStatus.ToString();
        completion.Annotations["connector.approval.execution_status"] = snapshot.ExecutionStatus.ToString();
        coordination.CompletionReference = await StoreApprovedCompletionAsync(
            coordination,
            completion,
            ctx,
            ct);
        await SaveStateAsync(state, ctx, ct);
        await ctx.PublishAsync(completion, TopologyAudience.Self, ct);

        coordination.StepCompletionPublished = true;
        RemovePending(state, pending);
        await SaveStateAsync(state, ctx, ct);
        await RevokeProtectedExecutionDataAsync(coordination, ctx, ct);
        await SaveStateAsync(state, ctx, ct);
    }
}
