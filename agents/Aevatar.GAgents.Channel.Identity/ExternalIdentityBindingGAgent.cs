using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Per-(platform, tenant, external_user_id) actor that holds the opaque NyxID
/// binding pointer for one external chat-platform user. Single-threaded
/// commit-time idempotency rejects concurrent /init callbacks for the same
/// external subject (ADR-0018 §Implementation Notes #2). State holds no
/// refresh_token or any user secret material (ADR-0018 §Storage Boundary).
/// </summary>
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
            .On<ExternalIdentityBindingRevokedEvent>(ApplyRevoked)
            .OrCurrent();

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

        if (!string.IsNullOrEmpty(State.BindingId))
        {
            Logger.LogInformation(
                "CommitBinding discarded: already bound for {Platform}:{Tenant}:{User} (existing={ExistingBindingId}, incoming={IncomingBindingId}); no identity fact changed",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId,
                State.BindingId,
                cmd.BindingId);
            return;
        }

        await PersistDomainEventAsync(new ExternalIdentityBoundEvent
        {
            ExternalSubject = cmd.ExternalSubject.Clone(),
            BindingId = cmd.BindingId,
            BoundAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        Logger.LogInformation(
            "Bound external identity: {Platform}:{Tenant}:{User} -> binding_id={BindingId}",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            cmd.BindingId);
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
        return next;
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
