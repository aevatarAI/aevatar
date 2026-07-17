using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Per-(platform, tenant, external_user_id) actor that holds the opaque NyxID
/// binding pointer for one external chat-platform user. Single-threaded
/// commit-time idempotency rejects concurrent /init callbacks for the same
/// external subject (ADR-0018 §Implementation Notes #2). State holds no
/// refresh_token or any user secret material (ADR-0018 §Storage Boundary).
/// </summary>
[GAgent("channel.identity.external-identity-binding")]
public sealed partial class ExternalIdentityBindingGAgent : GAgentBase<ExternalIdentityBindingState>
{
    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    /// <inheritdoc />
    /// <remarks>
    /// <see cref="StateTransitionMatcher"/> handles <c>Any</c>-wrapped payloads
    /// transparently via <c>ProtobufContractCompatibility.TryUnpack</c>, so the
    /// event-store's wrapped form ("type.googleapis.com/...") is matched the
    /// same as a directly-typed instance. No "unrecognised event type"
    /// pre-check fires here — the earlier guard incorrectly classified every
    /// Any-wrapped event as unknown and produced noisy warnings on every
    /// activation replay.
    /// </remarks>
    protected override ExternalIdentityBindingState TransitionState(ExternalIdentityBindingState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ExternalIdentityBoundEvent>(ApplyBound)
            .On<ExternalIdentityBindingReplacedEvent>(ApplyReplaced)
            .On<ExternalIdentityBindingRetirementQueuedEvent>(ApplyRetirementQueued)
            .On<ExternalIdentityBindingRetiredEvent>(ApplyRetired)
            .On<ExternalIdentityBindingRevokedEvent>(ApplyRevoked)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await RetirePendingBindingsAsync(ct);
    }

    // ─── Commands ───

    /// <summary>
    /// Commits a binding from NyxID's authorization-code exchange. Idempotent:
    /// when state already holds an active binding_id, the command is discarded
    /// (concurrent /init protection — see ADR-0018 §Implementation Notes #2).
    /// The orphan binding on the NyxID side is left for NyxID's own reaper.
    /// </summary>
    /// <remarks>
    /// Single-actor turn ordering plus the event store's optimistic concurrency
    /// (it is the cluster event store, not raw memory) give the
    /// "discard duplicate commit" guarantee end-to-end. The in-handler check
    /// here is the per-turn fast path; OCC at append-time covers the
    /// pathological case of two turns racing past the State load.
    /// </remarks>
    [EventHandler]
    public async Task HandleCommitBinding(CommitBindingCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.ExternalSubject is null)
        {
            Logger.LogWarning("CommitBinding rejected: external_subject is required.");
            return;
        }

        if (!IsCommandSubjectMatchingActor(cmd.ExternalSubject))
            return;

        if (string.IsNullOrEmpty(cmd.BindingId))
        {
            Logger.LogWarning(
                "CommitBinding rejected: binding_id is required for {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        var ownerScopeId = ResolveOwnerScopeId(cmd.OwnerScopeId);
        if (ownerScopeId is null)
        {
            Logger.LogWarning(
                "CommitBinding rejected: owner_scope_id is required for subject {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        if (!string.IsNullOrEmpty(State.BindingId))
        {
            Logger.LogInformation(
                "CommitBinding discarded: already bound for {Platform}:{Tenant}:{User} (existing={ExistingBindingId}, incoming={IncomingBindingId}); no identity fact changed",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId,
                State.BindingId,
                cmd.BindingId);
            // Self-heal: the identity fact is unchanged (no event appended), but a
            // projection-store reset can leave the current-state readmodel wiped while
            // this actor still holds the binding. Re-emitting the current committed
            // state rebuilds that row, so a re-auth (/init callback or Studio re-login)
            // recovers a wiped readmodel instead of dead-locking on the idempotent
            // discard. See GAgentBase.RepublishCommittedStateAsync.
            await RepublishCurrentBindingStateAsync();
            return;
        }

        await PersistDomainEventAsync(new ExternalIdentityBoundEvent
        {
            ExternalSubject = cmd.ExternalSubject.Clone(),
            BindingId = cmd.BindingId,
            BoundAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            OwnerScopeId = ownerScopeId,
        });

        Logger.LogInformation(
            "Bound external identity: {Platform}:{Tenant}:{User} -> binding_id={BindingId}",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            cmd.BindingId);
    }

    /// <summary>
    /// Atomically replaces the active binding after authorization-code exchange
    /// has returned and validated a new binding id. The expected previous id is
    /// checked inside the actor turn so a stale callback cannot overwrite a
    /// newer authorization result.
    /// </summary>
    [EventHandler]
    public async Task HandleReplaceBinding(ReplaceBindingCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.ExternalSubject is null)
        {
            Logger.LogWarning("ReplaceBinding rejected: external_subject is required.");
            return;
        }

        if (!IsCommandSubjectMatchingActor(cmd.ExternalSubject))
            return;

        if (string.IsNullOrEmpty(cmd.BindingId))
        {
            Logger.LogWarning(
                "ReplaceBinding rejected: binding_id is required for {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        if (string.IsNullOrEmpty(cmd.ExpectedPreviousBindingId))
        {
            Logger.LogWarning(
                "ReplaceBinding rejected: expected_previous_binding_id is required for {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        var ownerScopeId = ResolveOwnerScopeId(cmd.OwnerScopeId);
        if (ownerScopeId is null)
        {
            Logger.LogWarning(
                "ReplaceBinding rejected: owner_scope_id is required for subject {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        var previousBindingId = State.BindingId;
        if (string.Equals(previousBindingId, cmd.BindingId, StringComparison.Ordinal))
        {
            Logger.LogInformation(
                "ReplaceBinding skipped: binding already current for {Platform}:{Tenant}:{User} (binding_id={BindingId})",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId,
                cmd.BindingId);
            // Self-heal a wiped readmodel without appending an event (same rationale as
            // the CommitBinding discard branch).
            await RepublishCurrentBindingStateAsync();
            await RetirePendingBindingsAsync();
            return;
        }

        if (!string.Equals(previousBindingId, cmd.ExpectedPreviousBindingId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "ReplaceBinding compare-and-swap rejected for {Platform}:{Tenant}:{User} (expected={ExpectedBindingId}, current={CurrentBindingId}, incoming={IncomingBindingId})",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId,
                cmd.ExpectedPreviousBindingId,
                previousBindingId,
                cmd.BindingId);

            await QueueBindingRetirementAsync(
                cmd.ExternalSubject,
                cmd.BindingId,
                "replacement_compare_and_swap_rejected");
            await RetirePendingBindingsAsync();
            return;
        }

        var reason = string.IsNullOrWhiteSpace(cmd.Reason) ? "unspecified" : cmd.Reason;
        await PersistDomainEventAsync(new ExternalIdentityBindingReplacedEvent
        {
            ExternalSubject = cmd.ExternalSubject.Clone(),
            PreviousBindingId = previousBindingId,
            BindingId = cmd.BindingId,
            ReplacedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Reason = reason,
            OwnerScopeId = ownerScopeId,
        });

        Logger.LogInformation(
            "Replaced external identity binding: {Platform}:{Tenant}:{User} (previous={PreviousBindingId}, current={BindingId}, reason={Reason})",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            previousBindingId,
            cmd.BindingId,
            reason);

        // The replacement fact is committed before any external revocation.
        // A failed NyxID call leaves the old id in actor state for activation-
        // time reconciliation and never puts the newly adopted binding at risk.
        await RetirePendingBindingsAsync();
    }

    /// <summary>
    /// Revokes the active binding. When state has no active binding (for
    /// example concurrent /unbind, revoke-after-revoke from
    /// <c>invalid_grant</c>, or remote-side self-heal after projection drift),
    /// leaves actor facts unchanged. Stale readmodel repair belongs to the
    /// projection lifecycle or maintenance path. Caller must have already
    /// invoked the NyxID-side revoke (or observed <c>invalid_grant</c>) —
    /// this command only transitions local state when an active binding exists.
    /// </summary>
    [EventHandler]
    public async Task HandleRevokeBinding(RevokeBindingCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.ExternalSubject is null)
        {
            Logger.LogWarning("RevokeBinding rejected: external_subject is required.");
            return;
        }

        if (!IsCommandSubjectMatchingActor(cmd.ExternalSubject))
            return;

        // Use the explicit "unspecified" sentinel so the persisted audit
        // trail distinguishes "caller did not supply a reason" from a
        // missing/empty value. The event Reason field is non-nullable in
        // proto3 (defaults to ""), so the sentinel substitution lives at
        // the boundary here rather than relying on per-call interpretation
        // (kimi-k2p6 L109 / L124 5/5 consensus).
        var reason = string.IsNullOrWhiteSpace(cmd.Reason) ? "unspecified" : cmd.Reason;

        if (string.IsNullOrEmpty(State.BindingId))
        {
            Logger.LogInformation(
                "RevokeBinding found no active binding for {Platform}:{Tenant}:{User}; no identity fact changed (reason={Reason})",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId,
                reason);
            return;
        }

        var revokedBindingId = State.BindingId;

        await PersistDomainEventAsync(new ExternalIdentityBindingRevokedEvent
        {
            ExternalSubject = cmd.ExternalSubject.Clone(),
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Reason = reason,
        });

        Logger.LogInformation(
            "Revoked external identity binding: {Platform}:{Tenant}:{User} (binding_id={BindingId}, reason={Reason})",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            revokedBindingId,
            reason);
    }

    /// <summary>
    /// Maintenance / disaster-recovery: re-materialize this binding's current-state
    /// readmodel from the surviving authoritative actor state. Appends no event and
    /// changes no binding_id — it re-emits the current committed state so a projection
    /// store that was wiped/reset (while the actor state in the event store survived)
    /// rebuilds the row. No-op when the actor holds no active binding.
    /// </summary>
    [EventHandler]
    public async Task HandleRebuildBindingProjection(RebuildBindingProjectionCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.ExternalSubject is null)
        {
            Logger.LogWarning("RebuildBindingProjection rejected: external_subject is required.");
            return;
        }

        if (!IsCommandSubjectMatchingActor(cmd.ExternalSubject))
            return;

        if (string.IsNullOrEmpty(State.BindingId))
        {
            Logger.LogInformation(
                "RebuildBindingProjection found no active binding for {Platform}:{Tenant}:{User}; nothing to rebuild",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        await RepublishCurrentBindingStateAsync();

        Logger.LogInformation(
            "Rebuilt external identity binding readmodel from surviving actor state: {Platform}:{Tenant}:{User} (binding_id={BindingId})",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            State.BindingId);
    }

    // Re-emits the actor's current committed binding state to the projection pipeline
    // without appending a new event. The reconstructed ExternalIdentityBoundEvent is
    // used only for projection routing/activation; the materialized row comes from the
    // current state snapshot. Precondition: State.BindingId is non-empty.
    private Task RepublishCurrentBindingStateAsync() =>
        RepublishCommittedStateAsync(new ExternalIdentityBoundEvent
        {
            ExternalSubject = State.ExternalSubject?.Clone(),
            BindingId = State.BindingId,
            BoundAt = State.BoundAt,
            OwnerScopeId = State.OwnerScopeId,
        });

    private static string? ResolveOwnerScopeId(string? ownerScopeId) =>
        NormalizeOptional(ownerScopeId);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task QueueBindingRetirementAsync(
        ExternalSubjectRef subject,
        string bindingId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bindingId)
            || string.Equals(State.BindingId, bindingId, StringComparison.Ordinal)
            || State.PendingRetirementBindingIds.Contains(bindingId))
        {
            return;
        }

        await PersistDomainEventAsync(new ExternalIdentityBindingRetirementQueuedEvent
        {
            ExternalSubject = subject.Clone(),
            BindingId = bindingId,
            QueuedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Reason = reason,
        }, ct);
    }

    private async Task RetirePendingBindingsAsync(CancellationToken ct = default)
    {
        if (State.PendingRetirementBindingIds.Count == 0)
            return;

        var retirementPort = Services.GetService<INyxIdBindingRetirementPort>();
        if (retirementPort is null)
        {
            Logger.LogWarning(
                "Pending NyxID binding retirement cannot run because {Port} is unavailable for actor={ActorId}.",
                nameof(INyxIdBindingRetirementPort),
                Id);
            return;
        }

        foreach (var bindingId in State.PendingRetirementBindingIds.ToArray())
        {
            if (string.Equals(State.BindingId, bindingId, StringComparison.Ordinal))
            {
                Logger.LogError(
                    "Refusing to retire the current NyxID binding for actor={ActorId}, binding_id={BindingId}.",
                    Id,
                    bindingId);
                continue;
            }

            try
            {
                await retirementPort.RetireAsync(bindingId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Logger.LogWarning(
                    ex,
                    "NyxID binding retirement remains pending for actor={ActorId}, binding_id={BindingId}.",
                    Id,
                    bindingId);
                continue;
            }

            await PersistDomainEventAsync(new ExternalIdentityBindingRetiredEvent
            {
                ExternalSubject = State.ExternalSubject?.Clone(),
                BindingId = bindingId,
                RetiredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct);
        }
    }

    // ─── Identity guard ───

    // Defensive routing check: when the runtime has set this actor's Id (always
    // true in production), reject commands carrying a different external
    // subject. Empty Id (test scenarios where the actor is instantiated
    // directly without runtime activation) skips the guard so unit tests can
    // exercise the handlers without pre-wiring the actor key.
    private bool IsCommandSubjectMatchingActor(ExternalSubjectRef commandSubject)
    {
        if (string.IsNullOrEmpty(Id))
            return true;

        var expected = commandSubject.ToActorId();
        if (string.Equals(expected, Id, StringComparison.Ordinal))
            return true;

        Logger.LogWarning(
            "Command rejected: external_subject mismatch (cmd={CommandActorId}, actor={ActorId})",
            expected,
            Id);
        return false;
    }

    // Refactor (iter97/cluster-097): Old pattern: no-op identity commands
    // activated committed-state projection by side-reading the latest event
    // and manually dispatching a projection envelope. New principle: identity
    // commands only commit real facts; committed-state hook + activation plan
    // provider own projection materialization, and drift repair must be an
    // explicit maintenance/admin path.

    // ─── State transitions ───

    private static ExternalIdentityBindingState ApplyBound(
        ExternalIdentityBindingState current,
        ExternalIdentityBoundEvent evt)
    {
        var next = current.Clone();
        // ExternalSubject is an actor-identity invariant — set once on the
        // first bind and never overwritten by subsequent events. ADR-0018 L58
        // review: an event with a mismatched subject should not silently
        // rewrite the actor's identity field.
        next.ExternalSubject ??= evt.ExternalSubject?.Clone();
        next.BindingId = evt.BindingId ?? string.Empty;
        next.BoundAt = evt.BoundAt;
        next.RevokedAt = null;
        next.OwnerScopeId = evt.OwnerScopeId ?? string.Empty;
        return next;
    }

    private static ExternalIdentityBindingState ApplyReplaced(
        ExternalIdentityBindingState current,
        ExternalIdentityBindingReplacedEvent evt)
    {
        var next = current.Clone();
        next.ExternalSubject ??= evt.ExternalSubject?.Clone();
        next.BindingId = evt.BindingId ?? string.Empty;
        next.BoundAt = evt.ReplacedAt;
        next.RevokedAt = null;
        next.OwnerScopeId = evt.OwnerScopeId ?? string.Empty;
        AddPendingRetirement(next, evt.PreviousBindingId);
        return next;
    }

    private static ExternalIdentityBindingState ApplyRetirementQueued(
        ExternalIdentityBindingState current,
        ExternalIdentityBindingRetirementQueuedEvent evt)
    {
        var next = current.Clone();
        AddPendingRetirement(next, evt.BindingId);
        return next;
    }

    private static ExternalIdentityBindingState ApplyRetired(
        ExternalIdentityBindingState current,
        ExternalIdentityBindingRetiredEvent evt)
    {
        var next = current.Clone();
        for (var index = next.PendingRetirementBindingIds.Count - 1; index >= 0; index--)
        {
            if (string.Equals(next.PendingRetirementBindingIds[index], evt.BindingId, StringComparison.Ordinal))
                next.PendingRetirementBindingIds.RemoveAt(index);
        }

        return next;
    }

    private static void AddPendingRetirement(ExternalIdentityBindingState state, string? bindingId)
    {
        if (!string.IsNullOrWhiteSpace(bindingId)
            && !string.Equals(state.BindingId, bindingId, StringComparison.Ordinal)
            && !state.PendingRetirementBindingIds.Contains(bindingId))
        {
            state.PendingRetirementBindingIds.Add(bindingId);
        }
    }

    private static ExternalIdentityBindingState ApplyRevoked(
        ExternalIdentityBindingState current,
        ExternalIdentityBindingRevokedEvent evt)
    {
        var next = current.Clone();
        next.BindingId = string.Empty;
        next.RevokedAt = evt.RevokedAt;
        return next;
    }
}
