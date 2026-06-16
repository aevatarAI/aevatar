using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

[GAgent("workflow.lease")]
public sealed class WorkflowLeaseGAgent : GAgentBase<WorkflowLeaseState>
{
    public const int DefaultLeaseTtlMs = 300_000;
    public const int MinLeaseTtlMs = 1_000;
    public const int MaxLeaseTtlMs = 3_600_000;
    public const int DefaultWaitTimeoutMs = 300_000;
    public const int MinWaitTimeoutMs = 1_000;
    public const int MaxWaitTimeoutMs = 3_600_000;
    public const int MaxWaiters = 32;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await RecoverCallbackIntentsAsync(ct);
    }

    public override Task<string> GetDescriptionAsync()
    {
        var key = string.IsNullOrWhiteSpace(State.LeaseKey) ? Id : State.LeaseKey;
        var status = HasActiveHolder(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? "held" : "available";
        return Task.FromResult($"WorkflowLeaseGAgent[{key}] {status} generation={State.Generation}");
    }

    [EventHandler]
    public Task HandleAcquireAsync(WorkflowLeaseAcquireRequestedEvent request) =>
        HandleAcquireAsync(request, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None);

    [EventHandler]
    public Task HandleRenewAsync(WorkflowLeaseRenewRequestedEvent request) =>
        HandleRenewAsync(request, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None);

    [EventHandler]
    public Task HandleReleaseAsync(WorkflowLeaseReleaseRequestedEvent request) =>
        HandleReleaseAsync(request, CancellationToken.None);

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleExpirationFiredAsync(WorkflowLeaseExpirationFiredEvent fired) =>
        HandleExpirationFiredAsync(fired, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None);

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleWaitTimeoutFiredAsync(WorkflowLeaseWaitTimeoutFiredEvent fired) =>
        HandleWaitTimeoutFiredAsync(fired, CancellationToken.None);

    internal async Task HandleAcquireAsync(
        WorkflowLeaseAcquireRequestedEvent request,
        long nowUnixMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeAcquire(request, out var normalized, out var error))
        {
            await ReplyRejectedAsync(
                request,
                WorkflowLeaseOperation.Acquire,
                WorkflowLeaseRejectionReason.InvalidRequest,
                error,
                ct);
            return;
        }

        await ExpireHolderIfNeededAsync(nowUnixMs, ct);
        if (!HasActiveHolder(nowUnixMs))
        {
            await GrantAsync(normalized, nowUnixMs, ct);
            return;
        }

        if (normalized.OnConflict != WorkflowLeaseConflictPolicy.Wait)
        {
            await ReplyRejectedAsync(
                normalized,
                WorkflowLeaseOperation.Acquire,
                WorkflowLeaseRejectionReason.LeaseBusy,
                $"workflow lease '{normalized.LeaseKey}' is held by run '{State.HolderRunId}'.",
                ct);
            return;
        }

        if (HasWaiter(normalized.RequestId))
            return;

        if (State.Waiters.Count >= MaxWaiters)
        {
            await ReplyRejectedAsync(
                normalized,
                WorkflowLeaseOperation.Acquire,
                WorkflowLeaseRejectionReason.WaitQueueFull,
                $"workflow lease '{normalized.LeaseKey}' wait queue is full.",
                ct);
            return;
        }

        var timeoutCallbackId = BuildWaitTimeoutCallbackId(normalized.LeaseKey, normalized.RequestId);
        var waiter = new WorkflowLeaseWaiterState
        {
            RequestId = normalized.RequestId,
            RequesterRunId = normalized.RequesterRunId,
            RequesterActorId = normalized.RequesterActorId,
            RequesterStepId = normalized.RequesterStepId,
            TtlMs = normalized.TtlMs,
            WaitTimeoutMs = normalized.WaitTimeoutMs,
            EnqueuedAtUnixMs = nowUnixMs,
            TimeoutCallbackId = timeoutCallbackId,
        };

        var waitTimeoutLease = await ScheduleSelfDurableTimeoutAsync(
            timeoutCallbackId,
            TimeSpan.FromMilliseconds(normalized.WaitTimeoutMs),
            new WorkflowLeaseWaitTimeoutFiredEvent
            {
                LeaseKey = normalized.LeaseKey,
                RequestId = normalized.RequestId,
                RequesterRunId = normalized.RequesterRunId,
                RequesterActorId = normalized.RequesterActorId,
                RequesterStepId = normalized.RequesterStepId,
            },
            ct: ct);
        waiter.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(waitTimeoutLease);

        await PersistDomainEventAsync(BuildStateUpsertedEvent(State, addWaiter: waiter), ct);
    }

    internal async Task HandleRenewAsync(
        WorkflowLeaseRenewRequestedEvent request,
        long nowUnixMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeRenew(request, out var normalized, out var error))
        {
            await ReplyRejectedAsync(
                request,
                WorkflowLeaseOperation.Renew,
                WorkflowLeaseRejectionReason.InvalidRequest,
                error,
                ct);
            return;
        }

        await ExpireHolderIfNeededAsync(nowUnixMs, ct);
        if (!MatchesHolder(normalized.HolderToken, normalized.Generation, normalized.RequesterRunId))
        {
            await ReplyRejectedAsync(
                normalized,
                WorkflowLeaseOperation.Renew,
                WorkflowLeaseRejectionReason.StaleHolder,
                $"workflow lease '{normalized.LeaseKey}' renew token or generation is stale.",
                ct);
            return;
        }

        var expiresAt = nowUnixMs + normalized.TtlMs;
        var previousLease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(State.ExpirationLease);
        await PersistDomainEventAsync(
            BuildStateUpsertedEvent(
                State,
                holderRunId: normalized.RequesterRunId,
                holderActorId: normalized.RequesterActorId,
                holderStepId: normalized.RequesterStepId,
                holderToken: normalized.HolderToken,
                generation: normalized.Generation,
                expiresAtUnixMs: expiresAt,
                expirationCallbackId: BuildExpirationCallbackId(normalized.LeaseKey, normalized.Generation)),
            ct);
        await CancelLeaseBestEffortAsync(previousLease, CancellationToken.None);
        await ActivateExpirationIntentAsync(ct);

        await SendToAsync(
            normalized.RequesterActorId,
            new WorkflowLeaseRenewedEvent
            {
                LeaseKey = normalized.LeaseKey,
                RequestId = normalized.RequestId,
                RequesterRunId = normalized.RequesterRunId,
                RequesterActorId = normalized.RequesterActorId,
                RequesterStepId = normalized.RequesterStepId,
                HolderToken = normalized.HolderToken,
                Generation = normalized.Generation,
                ExpiresAtUnixMs = expiresAt,
                TtlMs = normalized.TtlMs,
            },
            ct);
    }

    internal async Task HandleReleaseAsync(WorkflowLeaseReleaseRequestedEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeRelease(request, out var normalized, out var error))
        {
            await ReplyRejectedAsync(
                request,
                WorkflowLeaseOperation.Release,
                WorkflowLeaseRejectionReason.InvalidRequest,
                error,
                ct);
            return;
        }

        if (!MatchesHolder(normalized.HolderToken, normalized.Generation, normalized.RequesterRunId))
        {
            await ReplyRejectedAsync(
                normalized,
                WorkflowLeaseOperation.Release,
                WorkflowLeaseRejectionReason.StaleHolder,
                $"workflow lease '{normalized.LeaseKey}' release token or generation is stale.",
                ct);
            return;
        }

        var previousLease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(State.ExpirationLease);
        await ClearHolderAsync(ct);
        await CancelLeaseBestEffortAsync(previousLease, CancellationToken.None);

        await SendToAsync(
            normalized.RequesterActorId,
            new WorkflowLeaseReleasedEvent
            {
                LeaseKey = normalized.LeaseKey,
                RequestId = normalized.RequestId,
                RequesterRunId = normalized.RequesterRunId,
                RequesterActorId = normalized.RequesterActorId,
                RequesterStepId = normalized.RequesterStepId,
                HolderToken = normalized.HolderToken,
                Generation = normalized.Generation,
            },
            ct);

        await GrantNextWaiterAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
    }

    internal async Task HandleExpirationFiredAsync(
        WorkflowLeaseExpirationFiredEvent fired,
        long nowUnixMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fired);
        var leaseKey = TryNormalizeKey(fired.LeaseKey, out var normalizedKey)
            ? normalizedKey
            : State.LeaseKey;

        if (!string.Equals(State.LeaseKey, leaseKey, StringComparison.Ordinal) ||
            !string.Equals(State.HolderToken, fired.HolderToken, StringComparison.Ordinal) ||
            State.Generation != fired.Generation ||
            State.ExpiresAtUnixMs != fired.ExpiresAtUnixMs ||
            State.ExpiresAtUnixMs > nowUnixMs)
        {
            return;
        }

        await ClearHolderAsync(ct);
        await GrantNextWaiterAsync(nowUnixMs, ct);
    }

    internal async Task HandleWaitTimeoutFiredAsync(
        WorkflowLeaseWaitTimeoutFiredEvent fired,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fired);
        if (string.IsNullOrWhiteSpace(fired.RequestId))
            return;

        var state = State.Clone();
        var index = FindWaiterIndex(state, fired.RequestId);
        if (index < 0)
            return;

        var waiter = state.Waiters[index];
        state.Waiters.RemoveAt(index);
        await PersistDomainEventAsync(BuildStateUpsertedEvent(state), ct);

        await SendToAsync(
            waiter.RequesterActorId,
            BuildRejected(
                waiter,
                WorkflowLeaseOperation.Acquire,
                WorkflowLeaseRejectionReason.WaitTimeout,
                $"workflow lease '{State.LeaseKey}' wait timed out."),
            ct);
    }

    protected override WorkflowLeaseState TransitionState(WorkflowLeaseState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowLeaseStateUpsertedEvent>(ApplyStateUpserted)
            .OrCurrent();

    private async Task RecoverCallbackIntentsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.LeaseKey))
            return;

        if (!string.IsNullOrWhiteSpace(State.HolderToken) && State.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            await ActivateExpirationIntentAsync(ct);

        foreach (var waiter in State.Waiters.ToList())
            await ActivateWaitTimeoutIntentAsync(waiter, ct);
    }

    private async Task ExpireHolderIfNeededAsync(long nowUnixMs, CancellationToken ct)
    {
        if (!HasActiveHolder(nowUnixMs) &&
            !string.IsNullOrWhiteSpace(State.HolderToken))
        {
            var previousLease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(State.ExpirationLease);
            await ClearHolderAsync(ct);
            await CancelLeaseBestEffortAsync(previousLease, CancellationToken.None);
        }
    }

    private async Task GrantNextWaiterAsync(long nowUnixMs, CancellationToken ct)
    {
        while (State.Waiters.Count > 0 && !HasActiveHolder(nowUnixMs))
        {
            var waiter = State.Waiters[0];
            var state = State.Clone();
            state.Waiters.RemoveAt(0);
            await PersistDomainEventAsync(BuildStateUpsertedEvent(state), ct);

            await CancelWaiterTimeoutAsync(waiter, CancellationToken.None);
            await GrantAsync(
                new WorkflowLeaseAcquireRequestedEvent
                {
                    LeaseKey = State.LeaseKey,
                    RequestId = waiter.RequestId,
                    RequesterRunId = waiter.RequesterRunId,
                    RequesterActorId = waiter.RequesterActorId,
                    RequesterStepId = waiter.RequesterStepId,
                    TtlMs = waiter.TtlMs,
                    WaitTimeoutMs = waiter.WaitTimeoutMs,
                    OnConflict = WorkflowLeaseConflictPolicy.Wait,
                },
                nowUnixMs,
                ct);
        }
    }

    private async Task GrantAsync(
        WorkflowLeaseAcquireRequestedEvent request,
        long nowUnixMs,
        CancellationToken ct)
    {
        var generation = State.Generation + 1;
        var holderToken = Guid.NewGuid().ToString("N");
        var expiresAt = nowUnixMs + request.TtlMs;
        var previousLease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(State.ExpirationLease);
        await PersistDomainEventAsync(
            BuildStateUpsertedEvent(
                State,
                leaseKey: request.LeaseKey,
                holderRunId: request.RequesterRunId,
                holderActorId: request.RequesterActorId,
                holderStepId: request.RequesterStepId,
                holderToken: holderToken,
                generation: generation,
                expiresAtUnixMs: expiresAt,
                expirationCallbackId: BuildExpirationCallbackId(request.LeaseKey, generation)),
            ct);
        await CancelLeaseBestEffortAsync(previousLease, CancellationToken.None);
        await ActivateExpirationIntentAsync(ct);

        await SendToAsync(
            request.RequesterActorId,
            new WorkflowLeaseAcquiredEvent
            {
                LeaseKey = request.LeaseKey,
                RequestId = request.RequestId,
                RequesterRunId = request.RequesterRunId,
                RequesterActorId = request.RequesterActorId,
                RequesterStepId = request.RequesterStepId,
                HolderToken = holderToken,
                Generation = generation,
                ExpiresAtUnixMs = expiresAt,
                TtlMs = request.TtlMs,
            },
            ct);
    }

    private async Task ActivateExpirationIntentAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.HolderToken) ||
            string.IsNullOrWhiteSpace(State.ExpirationCallbackId) ||
            State.ExpiresAtUnixMs <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dueMs = Math.Max(1, State.ExpiresAtUnixMs - now);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            State.ExpirationCallbackId,
            TimeSpan.FromMilliseconds(dueMs),
            new WorkflowLeaseExpirationFiredEvent
            {
                LeaseKey = State.LeaseKey,
                HolderToken = State.HolderToken,
                Generation = State.Generation,
                ExpiresAtUnixMs = State.ExpiresAtUnixMs,
            },
            ct: ct);

        await PersistDomainEventAsync(
            BuildStateUpsertedEvent(
                State,
                expirationLease: WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease)),
            ct);
    }

    private async Task ActivateWaitTimeoutIntentAsync(WorkflowLeaseWaiterState waiter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(waiter.TimeoutCallbackId))
            return;

        var elapsedMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - waiter.EnqueuedAtUnixMs);
        var dueMs = Math.Max(1, waiter.WaitTimeoutMs - elapsedMs);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            waiter.TimeoutCallbackId,
            TimeSpan.FromMilliseconds(dueMs),
            new WorkflowLeaseWaitTimeoutFiredEvent
            {
                LeaseKey = State.LeaseKey,
                RequestId = waiter.RequestId,
                RequesterRunId = waiter.RequesterRunId,
                RequesterActorId = waiter.RequesterActorId,
                RequesterStepId = waiter.RequesterStepId,
            },
            ct: ct);

        var state = State.Clone();
        var index = FindWaiterIndex(state, waiter.RequestId);
        if (index < 0)
            return;

        state.Waiters[index].TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        await PersistDomainEventAsync(BuildStateUpsertedEvent(state), ct);
    }

    private async Task ClearHolderAsync(CancellationToken ct)
    {
        await PersistDomainEventAsync(
            BuildStateUpsertedEvent(
                State,
                holderRunId: string.Empty,
                holderActorId: string.Empty,
                holderStepId: string.Empty,
                holderToken: string.Empty,
                expiresAtUnixMs: 0,
                expirationCallbackId: string.Empty,
                expirationLease: null,
                replaceExpirationLease: true),
            ct);
    }

    private Task ReplyRejectedAsync(
        WorkflowLeaseAcquireRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error,
        CancellationToken ct) =>
        SendToAsync(request.RequesterActorId, BuildRejected(request, operation, reason, error), ct);

    private Task ReplyRejectedAsync(
        WorkflowLeaseRenewRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error,
        CancellationToken ct) =>
        SendToAsync(request.RequesterActorId, BuildRejected(request, operation, reason, error), ct);

    private Task ReplyRejectedAsync(
        WorkflowLeaseReleaseRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error,
        CancellationToken ct) =>
        SendToAsync(request.RequesterActorId, BuildRejected(request, operation, reason, error), ct);

    private bool HasActiveHolder(long nowUnixMs) =>
        !string.IsNullOrWhiteSpace(State.HolderToken) && State.ExpiresAtUnixMs > nowUnixMs;

    private bool MatchesHolder(string holderToken, long generation, string requesterRunId) =>
        !string.IsNullOrWhiteSpace(holderToken) &&
        string.Equals(State.HolderToken, holderToken, StringComparison.Ordinal) &&
        State.Generation == generation &&
        string.Equals(State.HolderRunId, requesterRunId, StringComparison.Ordinal);

    private bool HasWaiter(string requestId) =>
        State.Waiters.Any(x => string.Equals(x.RequestId, requestId, StringComparison.Ordinal));

    private static WorkflowLeaseState ApplyStateUpserted(
        WorkflowLeaseState current,
        WorkflowLeaseStateUpsertedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static int FindWaiterIndex(WorkflowLeaseState state, string requestId)
    {
        for (var i = 0; i < state.Waiters.Count; i++)
        {
            if (string.Equals(state.Waiters[i].RequestId, requestId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static WorkflowLeaseStateUpsertedEvent BuildStateUpsertedEvent(
        WorkflowLeaseState state,
        string? leaseKey = null,
        string? holderRunId = null,
        string? holderActorId = null,
        string? holderStepId = null,
        string? holderToken = null,
        long? generation = null,
        long? expiresAtUnixMs = null,
        string? expirationCallbackId = null,
        WorkflowRuntimeCallbackLeaseState? expirationLease = null,
        bool replaceExpirationLease = false,
        WorkflowLeaseWaiterState? addWaiter = null)
    {
        var next = state.Clone();
        if (leaseKey != null)
            next.LeaseKey = leaseKey;
        if (holderRunId != null)
            next.HolderRunId = holderRunId;
        if (holderActorId != null)
            next.HolderActorId = holderActorId;
        if (holderStepId != null)
            next.HolderStepId = holderStepId;
        if (holderToken != null)
            next.HolderToken = holderToken;
        if (generation.HasValue)
            next.Generation = generation.Value;
        if (expiresAtUnixMs.HasValue)
            next.ExpiresAtUnixMs = expiresAtUnixMs.Value;
        if (expirationCallbackId != null)
            next.ExpirationCallbackId = expirationCallbackId;
        if (replaceExpirationLease || expirationLease != null)
            next.ExpirationLease = expirationLease?.Clone();
        if (addWaiter != null)
            next.Waiters.Add(addWaiter.Clone());

        return new WorkflowLeaseStateUpsertedEvent
        {
            State = next,
        };
    }

    private static WorkflowLeaseStateUpsertedEvent BuildStateUpsertedEvent(WorkflowLeaseState state) =>
        new()
        {
            State = state.Clone(),
        };

    private static bool TryNormalizeAcquire(
        WorkflowLeaseAcquireRequestedEvent request,
        out WorkflowLeaseAcquireRequestedEvent normalized,
        out string error)
    {
        normalized = request.Clone();
        error = string.Empty;
        if (!TryNormalizeRequestBasics(
                request.LeaseKey,
                request.RequestId,
                request.RequesterRunId,
                request.RequesterActorId,
                request.RequesterStepId,
                out var leaseKey,
                out error))
        {
            return false;
        }

        normalized.LeaseKey = leaseKey;
        normalized.RequestId = request.RequestId.Trim();
        normalized.RequesterRunId = WorkflowRunIdNormalizer.Normalize(request.RequesterRunId);
        normalized.RequesterActorId = request.RequesterActorId.Trim();
        normalized.RequesterStepId = request.RequesterStepId.Trim();
        normalized.TtlMs = NormalizeDuration(request.TtlMs, DefaultLeaseTtlMs, MinLeaseTtlMs, MaxLeaseTtlMs);
        normalized.WaitTimeoutMs = NormalizeDuration(request.WaitTimeoutMs, DefaultWaitTimeoutMs, MinWaitTimeoutMs, MaxWaitTimeoutMs);
        normalized.OnConflict = request.OnConflict == WorkflowLeaseConflictPolicy.Wait
            ? WorkflowLeaseConflictPolicy.Wait
            : WorkflowLeaseConflictPolicy.Fail;
        return true;
    }

    private static bool TryNormalizeRenew(
        WorkflowLeaseRenewRequestedEvent request,
        out WorkflowLeaseRenewRequestedEvent normalized,
        out string error)
    {
        normalized = request.Clone();
        if (!TryNormalizeRequestBasics(
                request.LeaseKey,
                request.RequestId,
                request.RequesterRunId,
                request.RequesterActorId,
                request.RequesterStepId,
                out var leaseKey,
                out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.HolderToken) || request.Generation <= 0)
        {
            error = "workflow lease renew requires holder_token and positive generation.";
            return false;
        }

        normalized.LeaseKey = leaseKey;
        normalized.RequestId = request.RequestId.Trim();
        normalized.RequesterRunId = WorkflowRunIdNormalizer.Normalize(request.RequesterRunId);
        normalized.RequesterActorId = request.RequesterActorId.Trim();
        normalized.RequesterStepId = request.RequesterStepId.Trim();
        normalized.HolderToken = request.HolderToken.Trim();
        normalized.TtlMs = NormalizeDuration(request.TtlMs, DefaultLeaseTtlMs, MinLeaseTtlMs, MaxLeaseTtlMs);
        return true;
    }

    private static bool TryNormalizeRelease(
        WorkflowLeaseReleaseRequestedEvent request,
        out WorkflowLeaseReleaseRequestedEvent normalized,
        out string error)
    {
        normalized = request.Clone();
        if (!TryNormalizeRequestBasics(
                request.LeaseKey,
                request.RequestId,
                request.RequesterRunId,
                request.RequesterActorId,
                request.RequesterStepId,
                out var leaseKey,
                out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.HolderToken) || request.Generation <= 0)
        {
            error = "workflow lease release requires holder_token and positive generation.";
            return false;
        }

        normalized.LeaseKey = leaseKey;
        normalized.RequestId = request.RequestId.Trim();
        normalized.RequesterRunId = WorkflowRunIdNormalizer.Normalize(request.RequesterRunId);
        normalized.RequesterActorId = request.RequesterActorId.Trim();
        normalized.RequesterStepId = request.RequesterStepId.Trim();
        normalized.HolderToken = request.HolderToken.Trim();
        return true;
    }

    private static bool TryNormalizeRequestBasics(
        string leaseKey,
        string requestId,
        string requesterRunId,
        string requesterActorId,
        string requesterStepId,
        out string normalizedLeaseKey,
        out string error)
    {
        normalizedLeaseKey = string.Empty;
        error = string.Empty;
        if (!TryNormalizeKey(leaseKey, out normalizedLeaseKey))
        {
            error = "workflow lease key is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(requesterRunId) ||
            string.IsNullOrWhiteSpace(requesterActorId) ||
            string.IsNullOrWhiteSpace(requesterStepId))
        {
            error = "workflow lease request requires request_id, requester_run_id, requester_actor_id, and requester_step_id.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeKey(string leaseKey, out string normalizedLeaseKey)
    {
        try
        {
            normalizedLeaseKey = WorkflowLeaseActorId.NormalizeKey(leaseKey);
            return true;
        }
        catch (ArgumentException)
        {
            normalizedLeaseKey = string.Empty;
            return false;
        }
    }

    private static int NormalizeDuration(int raw, int fallback, int min, int max)
    {
        var value = raw <= 0 ? fallback : raw;
        return Math.Clamp(value, min, max);
    }

    private WorkflowLeaseRejectedEvent BuildRejected(
        WorkflowLeaseAcquireRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error) =>
        new()
        {
            LeaseKey = request.LeaseKey ?? string.Empty,
            RequestId = request.RequestId ?? string.Empty,
            RequesterRunId = request.RequesterRunId ?? string.Empty,
            RequesterActorId = request.RequesterActorId ?? string.Empty,
            RequesterStepId = request.RequesterStepId ?? string.Empty,
            Operation = operation,
            Reason = reason,
            CurrentHolderRunId = State.HolderRunId ?? string.Empty,
            Error = error ?? string.Empty,
        };

    private WorkflowLeaseRejectedEvent BuildRejected(
        WorkflowLeaseRenewRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error) =>
        new()
        {
            LeaseKey = request.LeaseKey ?? string.Empty,
            RequestId = request.RequestId ?? string.Empty,
            RequesterRunId = request.RequesterRunId ?? string.Empty,
            RequesterActorId = request.RequesterActorId ?? string.Empty,
            RequesterStepId = request.RequesterStepId ?? string.Empty,
            Operation = operation,
            Reason = reason,
            CurrentHolderRunId = State.HolderRunId ?? string.Empty,
            Error = error ?? string.Empty,
        };

    private WorkflowLeaseRejectedEvent BuildRejected(
        WorkflowLeaseReleaseRequestedEvent request,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error) =>
        new()
        {
            LeaseKey = request.LeaseKey ?? string.Empty,
            RequestId = request.RequestId ?? string.Empty,
            RequesterRunId = request.RequesterRunId ?? string.Empty,
            RequesterActorId = request.RequesterActorId ?? string.Empty,
            RequesterStepId = request.RequesterStepId ?? string.Empty,
            Operation = operation,
            Reason = reason,
            CurrentHolderRunId = State.HolderRunId ?? string.Empty,
            Error = error ?? string.Empty,
        };

    private WorkflowLeaseRejectedEvent BuildRejected(
        WorkflowLeaseWaiterState waiter,
        WorkflowLeaseOperation operation,
        WorkflowLeaseRejectionReason reason,
        string error) =>
        new()
        {
            LeaseKey = State.LeaseKey ?? string.Empty,
            RequestId = waiter.RequestId ?? string.Empty,
            RequesterRunId = waiter.RequesterRunId ?? string.Empty,
            RequesterActorId = waiter.RequesterActorId ?? string.Empty,
            RequesterStepId = waiter.RequesterStepId ?? string.Empty,
            Operation = operation,
            Reason = reason,
            CurrentHolderRunId = State.HolderRunId ?? string.Empty,
            Error = error ?? string.Empty,
        };

    private static string BuildExpirationCallbackId(string leaseKey, long generation) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-lease-expiration", leaseKey, generation.ToString("D"));

    private static string BuildWaitTimeoutCallbackId(string leaseKey, string requestId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-lease-wait-timeout", leaseKey, requestId);

    private async Task CancelWaiterTimeoutAsync(WorkflowLeaseWaiterState waiter, CancellationToken ct)
    {
        var lease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(waiter.TimeoutLease);
        await CancelLeaseBestEffortAsync(lease, ct);
    }

    private async Task CancelLeaseBestEffortAsync(RuntimeCallbackLease? lease, CancellationToken ct)
    {
        if (lease == null)
            return;

        await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
            CancelDurableCallbackAsync,
            Logger,
            lease,
            $"Workflow lease {Id} callback cleanup",
            ct);
    }
}
