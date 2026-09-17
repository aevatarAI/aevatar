using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

public sealed class LeaseModule : IEventModule<IWorkflowExecutionContext>
{
    private const string ModuleStateKey = "lease";
    private const string ActionAcquire = "acquire";
    private const string ActionRenew = "renew";
    private const string ActionRelease = "release";
    private readonly IActorRuntime _actorRuntime;

    public LeaseModule(IActorRuntime actorRuntime)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    }

    public string Name => "lease";

    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowLeaseAcquiredEvent.Descriptor) ||
                payload.Is(WorkflowLeaseRenewedEvent.Descriptor) ||
                payload.Is(WorkflowLeaseReleasedEvent.Descriptor) ||
                payload.Is(WorkflowLeaseRejectedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            await HandleStepRequestAsync(payload.Unpack<StepRequestEvent>(), envelope, ctx, ct);
            return;
        }

        if (payload.Is(WorkflowLeaseAcquiredEvent.Descriptor))
        {
            await HandleAcquiredAsync(payload.Unpack<WorkflowLeaseAcquiredEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(WorkflowLeaseRenewedEvent.Descriptor))
        {
            await HandleRenewedAsync(payload.Unpack<WorkflowLeaseRenewedEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(WorkflowLeaseReleasedEvent.Descriptor))
        {
            await HandleReleasedAsync(payload.Unpack<WorkflowLeaseReleasedEvent>(), ctx, ct);
            return;
        }

        await HandleRejectedAsync(payload.Unpack<WorkflowLeaseRejectedEvent>(), ctx, ct);
    }

    private async Task HandleStepRequestAsync(
        StepRequestEvent request,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!string.Equals(request.StepType, Name, StringComparison.Ordinal))
            return;

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await PublishFailureAsync(request, ctx, "lease step requires non-empty run_id and step_id", ct);
            return;
        }

        var action = NormalizeAction(WorkflowParameterValueParser.GetString(request.Parameters, ActionAcquire, "action"));
        if (action is not ActionAcquire and not ActionRenew and not ActionRelease)
        {
            await PublishFailureAsync(request, ctx, $"lease action '{action}' is not supported", ct);
            return;
        }

        var leaseKey = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "key", "lease_key");
        if (!TryResolveLeaseIdentity(leaseKey, out var canonicalKey, out var leaseActorId, out var keyError))
        {
            await PublishFailureAsync(request, ctx, keyError, ct);
            return;
        }

        var pending = new PendingWorkflowLeaseOperationState
        {
            RequestId = BuildRequestId(runId, stepId, ResolveOriginEnvelopeId(envelope)),
            RunId = runId,
            StepId = stepId,
            LeaseKey = canonicalKey,
            LeaseActorId = leaseActorId,
            Action = action,
            Input = string.IsNullOrWhiteSpace(request.InputValueId)
                ? request.Input ?? string.Empty
                : string.Empty,
            ExecutionId = request.ExecutionId,
            InputValueId = request.InputValueId,
            HolderTokenVariable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "holder_token_variable"),
        };

        if (action == ActionAcquire)
        {
            await StartAcquireAsync(request, pending, ctx, ct);
            return;
        }

        if (!TryParseCredential(request, out var holderToken, out var generation, out var credentialError))
        {
            await PublishFailureAsync(request, ctx, credentialError, ct);
            return;
        }

        if (action == ActionRenew)
        {
            await StartRenewAsync(request, pending, holderToken, generation, ctx, ct);
            return;
        }

        await StartReleaseAsync(request, pending, holderToken, generation, ctx, ct);
    }

    private async Task StartAcquireAsync(
        StepRequestEvent request,
        PendingWorkflowLeaseOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var onConflict = NormalizeOnConflict(WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_conflict"));
        var ttlMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            WorkflowLeaseGAgent.DefaultLeaseTtlMs,
            WorkflowLeaseGAgent.MinLeaseTtlMs,
            WorkflowLeaseGAgent.MaxLeaseTtlMs,
            "ttl_ms");
        var waitTimeoutMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            WorkflowLeaseGAgent.DefaultWaitTimeoutMs,
            WorkflowLeaseGAgent.MinWaitTimeoutMs,
            WorkflowLeaseGAgent.MaxWaitTimeoutMs,
            "wait_timeout_ms");

        if (!await TryEnsureLeaseActorAsync(request, pending, ctx, ct))
            return;

        await UpsertPendingAsync(pending, ctx, ct);
        await ctx.SendToAsync(
            pending.LeaseActorId,
            new WorkflowLeaseAcquireRequestedEvent
            {
                LeaseKey = pending.LeaseKey,
                RequestId = pending.RequestId,
                RequesterRunId = pending.RunId,
                RequesterActorId = ctx.AgentId,
                RequesterStepId = pending.StepId,
                TtlMs = ttlMs,
                WaitTimeoutMs = waitTimeoutMs,
                OnConflict = onConflict == "wait"
                    ? WorkflowLeaseConflictPolicy.Wait
                    : WorkflowLeaseConflictPolicy.Fail,
            },
            ct);
    }

    private async Task StartRenewAsync(
        StepRequestEvent request,
        PendingWorkflowLeaseOperationState pending,
        string holderToken,
        long generation,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var ttlMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            WorkflowLeaseGAgent.DefaultLeaseTtlMs,
            WorkflowLeaseGAgent.MinLeaseTtlMs,
            WorkflowLeaseGAgent.MaxLeaseTtlMs,
            "ttl_ms");

        if (!await TryEnsureLeaseActorAsync(request, pending, ctx, ct))
            return;

        await UpsertPendingAsync(pending, ctx, ct);
        await ctx.SendToAsync(
            pending.LeaseActorId,
            new WorkflowLeaseRenewRequestedEvent
            {
                LeaseKey = pending.LeaseKey,
                RequestId = pending.RequestId,
                RequesterRunId = pending.RunId,
                RequesterActorId = ctx.AgentId,
                RequesterStepId = pending.StepId,
                HolderToken = holderToken,
                Generation = generation,
                TtlMs = ttlMs,
            },
            ct);
    }

    private async Task StartReleaseAsync(
        StepRequestEvent request,
        PendingWorkflowLeaseOperationState pending,
        string holderToken,
        long generation,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!await TryEnsureLeaseActorAsync(request, pending, ctx, ct))
            return;

        await UpsertPendingAsync(pending, ctx, ct);
        await ctx.SendToAsync(
            pending.LeaseActorId,
            new WorkflowLeaseReleaseRequestedEvent
            {
                LeaseKey = pending.LeaseKey,
                RequestId = pending.RequestId,
                RequesterRunId = pending.RunId,
                RequesterActorId = ctx.AgentId,
                RequesterStepId = pending.StepId,
                HolderToken = holderToken,
                Generation = generation,
            },
            ct);
    }

    private async Task HandleAcquiredAsync(
        WorkflowLeaseAcquiredEvent acquired,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!TryTakePending(acquired.RequestId, acquired.RequesterRunId, acquired.RequesterStepId, ctx, out var state, out var pending))
            return;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = true,
            Output = acquired.HolderToken ?? string.Empty,
            AssignedVariable = pending.HolderTokenVariable ?? string.Empty,
            AssignedValue = acquired.HolderToken ?? string.Empty,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
            AssignedValueProvenance = WorkflowStepAssignedValueProvenance.ReferencesOutput,
        };
        FillSuccessAnnotations(completed, pending, acquired.HolderToken ?? string.Empty, acquired.Generation, acquired.ExpiresAtUnixMs);
        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleRenewedAsync(
        WorkflowLeaseRenewedEvent renewed,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!TryTakePending(renewed.RequestId, renewed.RequesterRunId, renewed.RequesterStepId, ctx, out var state, out var pending))
            return;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = true,
            Output = renewed.HolderToken ?? string.Empty,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        FillSuccessAnnotations(completed, pending, renewed.HolderToken ?? string.Empty, renewed.Generation, renewed.ExpiresAtUnixMs);
        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleReleasedAsync(
        WorkflowLeaseReleasedEvent released,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!TryTakePending(released.RequestId, released.RequesterRunId, released.RequesterStepId, ctx, out var state, out var pending))
            return;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = true,
            Output = released.HolderToken ?? string.Empty,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        completed.Annotations["lease.key"] = pending.LeaseKey;
        completed.Annotations["lease.actor_id"] = pending.LeaseActorId;
        completed.Annotations["lease.holder_token"] = released.HolderToken ?? string.Empty;
        completed.Annotations["lease.generation"] = released.Generation.ToString("D");
        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleRejectedAsync(
        WorkflowLeaseRejectedEvent rejected,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (!TryTakePending(rejected.RequestId, rejected.RequesterRunId, rejected.RequesterStepId, ctx, out var state, out var pending))
            return;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            ExecutionId = pending.ExecutionId,
            Success = false,
            Error = string.IsNullOrWhiteSpace(rejected.Error)
                ? $"lease {pending.Action} rejected: {rejected.Reason}"
                : rejected.Error,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };
        completed.Annotations["lease.key"] = pending.LeaseKey;
        completed.Annotations["lease.actor_id"] = pending.LeaseActorId;
        completed.Annotations["lease.rejection_reason"] = rejected.Reason.ToString();
        completed.Annotations["lease.operation"] = rejected.Operation.ToString();
        if (!string.IsNullOrWhiteSpace(rejected.CurrentHolderRunId))
            completed.Annotations["lease.current_holder_run_id"] = rejected.CurrentHolderRunId;

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        await SaveStateAsync(state, ctx, ct);
    }

    private static bool TryTakePending(
        string requestId,
        string runId,
        string stepId,
        IWorkflowExecutionContext ctx,
        out WorkflowLeaseModuleState state,
        out PendingWorkflowLeaseOperationState pending)
    {
        state = WorkflowExecutionStateAccess.Load<WorkflowLeaseModuleState>(ctx, ModuleStateKey);
        pending = new PendingWorkflowLeaseOperationState();
        var key = BuildPendingKey(runId, stepId, requestId);
        if (!state.Pending.Remove(key, out var candidate))
            return false;

        if (!string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(candidate.RunId, WorkflowRunIdNormalizer.Normalize(runId), StringComparison.Ordinal) ||
            !string.Equals(candidate.StepId, stepId?.Trim() ?? string.Empty, StringComparison.Ordinal))
        {
            return false;
        }

        pending = candidate;
        return true;
    }

    private async Task<bool> TryEnsureLeaseActorAsync(
        StepRequestEvent request,
        PendingWorkflowLeaseOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (await _actorRuntime.GetAsync(pending.LeaseActorId) != null)
            return true;

        try
        {
            await _actorRuntime.CreateAsync(typeof(WorkflowLeaseGAgent), pending.LeaseActorId, ct);
            return true;
        }
        catch (Exception ex)
        {
            if (await _actorRuntime.GetAsync(pending.LeaseActorId) != null)
                return true;

            await PublishFailureAsync(
                request,
                ctx,
                $"lease actor '{pending.LeaseActorId}' could not be created: {ex.Message}",
                ct);
            return false;
        }
    }

    private static async Task UpsertPendingAsync(
        PendingWorkflowLeaseOperationState pending,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var state = WorkflowExecutionStateAccess.Load<WorkflowLeaseModuleState>(ctx, ModuleStateKey);
        state.Pending[BuildPendingKey(pending.RunId, pending.StepId, pending.RequestId)] = pending;
        await SaveStateAsync(state, ctx, ct);
    }

    private static Task SaveStateAsync(
        WorkflowLeaseModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    private static void FillSuccessAnnotations(
        StepCompletedEvent completed,
        PendingWorkflowLeaseOperationState pending,
        string holderToken,
        long generation,
        long expiresAtUnixMs)
    {
        completed.Annotations["lease.key"] = pending.LeaseKey;
        completed.Annotations["lease.actor_id"] = pending.LeaseActorId;
        completed.Annotations["lease.holder_token"] = holderToken ?? string.Empty;
        completed.Annotations["lease.generation"] = generation.ToString("D");
        completed.Annotations["lease.expires_at_unix_ms"] = expiresAtUnixMs.ToString("D");
    }

    private static bool TryResolveLeaseIdentity(
        string rawKey,
        out string canonicalKey,
        out string actorId,
        out string error)
    {
        canonicalKey = string.Empty;
        actorId = string.Empty;
        error = string.Empty;
        try
        {
            canonicalKey = WorkflowLeaseActorId.NormalizeKey(rawKey);
            actorId = WorkflowLeaseActorId.FromKey(canonicalKey);
            return true;
        }
        catch (ArgumentException)
        {
            error = "lease step requires non-empty key";
            return false;
        }
    }

    private static bool TryParseCredential(
        StepRequestEvent request,
        out string holderToken,
        out long generation,
        out string error)
    {
        var action = NormalizeAction(request.Parameters.GetValueOrDefault("action", string.Empty));
        holderToken = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "holder_token");
        generation = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(holderToken))
        {
            error = $"lease {action} requires holder_token";
            return false;
        }

        var generationRaw = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "generation");
        if (!long.TryParse(generationRaw.Trim(), out generation) || generation <= 0)
        {
            error = $"lease {action} requires positive integer generation";
            return false;
        }

        holderToken = holderToken.Trim();
        return true;
    }

    private static string NormalizeAction(string? raw)
    {
        var normalized = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? ActionAcquire : normalized;
    }

    private static string NormalizeOnConflict(string? raw)
    {
        var normalized = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized == "wait" ? "wait" : "fail";
    }

    private static string BuildRequestId(string runId, string stepId, string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-lease-request", runId, stepId, originEnvelopeId);

    private static string BuildPendingKey(string runId, string stepId, string requestId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}:{stepId?.Trim() ?? string.Empty}:{requestId?.Trim() ?? string.Empty}";

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id.Trim();

    private static Task PublishFailureAsync(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        string error,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = request.StepId ?? string.Empty,
                RunId = WorkflowRunIdNormalizer.Normalize(request.RunId),
                ExecutionId = request.ExecutionId,
                Success = false,
                Error = error,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            },
            TopologyAudience.Self,
            ct);
}
