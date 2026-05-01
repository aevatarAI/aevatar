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
            .On<ExternalIdentityBindingProjectionRebuildRequestedEvent>(static (state, _) => state)
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
            // Steady-state branch: persist a no-op rebuild request so the
            // projector materializes the existing binding into the readmodel.
            // Without this, a legacy binding actor whose projection scope
            // was never activated (issue #549 follow-up: the binding scope
            // missed an EnsureProjectionForActorAsync wiring while every
            // other GAgent had one) leaves the readmodel empty, the OAuth
            // callback's readiness wait times out, and the next inbound
            // message's binding gate keeps re-sending the user back to /init.
            // Apply is identity, so the binding facts are not mutated by
            // this event.
            await PersistDomainEventAsync(new ExternalIdentityBindingProjectionRebuildRequestedEvent
            {
                Reason = "commit_already_bound",
                RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
            Logger.LogInformation(
                "CommitBinding discarded: already bound for {Platform}:{Tenant}:{User} (existing={ExistingBindingId}, incoming={IncomingBindingId}); rebuild requested so the projector materializes the existing binding",
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
    /// Revokes the active binding. NO-OP when state has no active binding
    /// (e.g. concurrent /unbind, or revoke-after-revoke from <c>invalid_grant</c>
    /// retry). Caller must have already invoked the NyxID-side revoke
    /// (or observed <c>invalid_grant</c>) — this command only transitions
    /// local state.
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

        if (string.IsNullOrEmpty(State.BindingId))
        {
            Logger.LogInformation(
                "RevokeBinding skipped: no active binding for {Platform}:{Tenant}:{User}",
                cmd.ExternalSubject.Platform,
                cmd.ExternalSubject.Tenant,
                cmd.ExternalSubject.ExternalUserId);
            return;
        }

        var revokedBindingId = State.BindingId;

        // Use the explicit "unspecified" sentinel so the persisted audit
        // trail distinguishes "caller did not supply a reason" from a
        // missing/empty value. The event Reason field is non-nullable in
        // proto3 (defaults to ""), so the sentinel substitution lives at
        // the boundary here rather than relying on per-call interpretation
        // (kimi-k2p6 L109 / L124 5/5 consensus).
        var reason = string.IsNullOrWhiteSpace(cmd.Reason) ? "unspecified" : cmd.Reason;

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
