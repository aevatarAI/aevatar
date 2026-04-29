using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Per-(platform, tenant, external_user_id) actor that holds the opaque NyxID
/// binding pointer for one external chat-platform user. Single-threaded
/// commit-time idempotency rejects concurrent /init callbacks for the same
/// external subject (ADR-0017 §Implementation Notes #2). State holds no
/// refresh_token or any user secret material (ADR-0017 §Storage Boundary).
/// </summary>
public sealed partial class ExternalIdentityBindingGAgent : GAgentBase<ExternalIdentityBindingState>
{
    /// <inheritdoc />
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
    /// (concurrent /init protection — see ADR-0017 §Implementation Notes #2).
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
                "CommitBinding discarded: already bound for {Platform}:{Tenant}:{User} (existing={ExistingBindingId}, incoming={IncomingBindingId})",
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

        await PersistDomainEventAsync(new ExternalIdentityBindingRevokedEvent
        {
            ExternalSubject = cmd.ExternalSubject.Clone(),
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Reason = cmd.Reason ?? string.Empty,
        });

        Logger.LogInformation(
            "Revoked external identity binding: {Platform}:{Tenant}:{User} (binding_id={BindingId}, reason={Reason})",
            cmd.ExternalSubject.Platform,
            cmd.ExternalSubject.Tenant,
            cmd.ExternalSubject.ExternalUserId,
            revokedBindingId,
            string.IsNullOrEmpty(cmd.Reason) ? "unspecified" : cmd.Reason);
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
        // first bind and never overwritten by subsequent events. ADR-0017 L58
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
