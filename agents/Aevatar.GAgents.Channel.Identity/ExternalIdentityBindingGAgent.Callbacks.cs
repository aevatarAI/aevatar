using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Channel.Identity;

public sealed partial class ExternalIdentityBindingGAgent
{
    private DateTimeOffset CallbackNow => (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    [EventHandler]
    public Task HandleConfirmBindingGrant(ConfirmBindingGrantCommand command) =>
        HandleCallbackBindingAsync(command.CallbackReply, command.ExternalSubject, command.BindingId,
            command.OwnerScopeId, () => ConfirmLegacyBindingOwnerAsync(command));

    private Task ConfirmLegacyBindingOwnerAsync(ConfirmBindingGrantCommand command) =>
        !string.IsNullOrWhiteSpace(command.BindingId)
        && State.BindingId == command.BindingId && string.IsNullOrWhiteSpace(State.OwnerScopeId)
        && !string.IsNullOrWhiteSpace(command.OwnerScopeId)
            ? PersistDomainEventAsync(new ExternalIdentityBindingOwnerConfirmedEvent
            {
                ExternalSubject = command.ExternalSubject.Clone(), BindingId = command.BindingId,
                OwnerScopeId = command.OwnerScopeId, CallbackReply = command.CallbackReply?.Clone(),
            })
            : Task.CompletedTask;

    [EventHandler]
    public async Task HandleAbandonBindingPreparation(AbandonBindingPreparationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reply = command.CallbackReply;
        if (command.ExternalSubject is null || !IsCommandSubjectMatchingActor(command.ExternalSubject)
            || string.IsNullOrWhiteSpace(command.BindingId) || reply is null
            || string.IsNullOrWhiteSpace(reply.CallbackId) || string.IsNullOrWhiteSpace(reply.OperationActorId))
            return;

        var prior = State.CallbackDecisions.FirstOrDefault(x =>
            x.Reply.CallbackId == reply.CallbackId && x.Outcome.Abandoned);
        if (prior is not null)
        {
            if (prior.Reply.OperationActorId == reply.OperationActorId
                && prior.Outcome.BindingId == command.BindingId
                && prior.Outcome.OwnerScopeId == command.OwnerScopeId
                && Equals(prior.Outcome.ExternalSubject, command.ExternalSubject))
                await SendToAsync(reply.OperationActorId, prior.Outcome.Clone());
            await RetirePendingBindingsAsync();
            return;
        }

        // This guard is authoritative. Neither stale projections nor a later
        // operation can cause cleanup to revoke an adopted current binding.
        if (State.BindingId != command.BindingId)
            await QueueBindingRetirementAsync(command.ExternalSubject, command.BindingId, "callback_preparation_abandoned");

        var decision = new BindingCallbackDecision
        {
            Reply = reply.Clone(),
            Outcome = new OAuthBindingOutcome
            {
                CallbackId = reply.CallbackId,
                ExternalSubject = command.ExternalSubject.Clone(),
                BindingId = command.BindingId,
                OwnerScopeId = command.OwnerScopeId,
                Abandoned = true,
            },
        };
        await PersistDomainEventAsync(new BindingCallbackDecisionRecorded
        {
            Decision = decision,
            PruneBeforeUnixMs = CallbackNow.AddDays(-1).ToUnixTimeMilliseconds(),
        });
        // The receipt confirms durable cleanup ownership, not a synchronous
        // remote deletion. Failed retirement remains in binding actor state.
        await SendToAsync(reply.OperationActorId, decision.Outcome.Clone());
        await RetirePendingBindingsAsync();
    }

    private async Task HandleCallbackBindingAsync(
        BindingCallbackReply? reply,
        ExternalSubjectRef? subject,
        string bindingId,
        string ownerScopeId,
        Func<Task> execute)
    {
        if (reply is null)
        {
            await execute();
            return;
        }

        if (string.IsNullOrWhiteSpace(reply.CallbackId) || string.IsNullOrWhiteSpace(reply.OperationActorId))
            return;

        var prior = State.CallbackDecisions.FirstOrDefault(x => x.Reply.CallbackId == reply.CallbackId && !x.Outcome.Abandoned);
        if (prior is not null)
        {
            // A callback ID cannot be retargeted by a conflicting delivery.
            if (prior.Reply.OperationActorId == reply.OperationActorId
                && prior.Outcome.BindingId == bindingId
                && prior.Outcome.OwnerScopeId == ownerScopeId
                && Equals(prior.Outcome.ExternalSubject, subject))
                await SendToAsync(prior.Reply.OperationActorId, prior.Outcome.Clone());
            return;
        }

        var validSubject = subject is not null && IsCommandSubjectMatchingActor(subject);
        var expired = reply.ExpiresAtUnixMs <= CallbackNow.ToUnixTimeMilliseconds();
        var abandoned = State.CallbackDecisions.Any(x => x.Reply.CallbackId == reply.CallbackId && x.Outcome.Abandoned);
        var ownerMatches = string.IsNullOrEmpty(State.BindingId) ||
            string.IsNullOrEmpty(State.OwnerScopeId) || State.OwnerScopeId == ownerScopeId;
        if (validSubject && !expired && ownerMatches && !abandoned)
            await execute();
        else if (validSubject && bindingId.Length > 0 && bindingId != State.BindingId)
        {
            // Preserve the actor-owned orphan-retirement path when admission
            // loses a race or its operation has expired before this turn.
            await QueueBindingRetirementAsync(subject!, bindingId,
                expired ? "callback_expired" : "callback_owner_scope_mismatch");
            await RetirePendingBindingsAsync();
        }

        // A commit/replacement stores this success receipt atomically with its
        // binding event; a crash between commit and send therefore redelivers
        // the original decision even if another flow has since replaced it.
        var decision = State.CallbackDecisions.FirstOrDefault(x => x.Reply.CallbackId == reply.CallbackId && !x.Outcome.Abandoned);
        if (decision is null)
        {
            var succeeded = validSubject && !expired && !abandoned
                && bindingId.Length > 0 && ownerScopeId.Length > 0
                && State.BindingId == bindingId && State.OwnerScopeId == ownerScopeId;
            decision = new BindingCallbackDecision
            {
                Reply = reply.Clone(),
                Outcome = new OAuthBindingOutcome
                {
                    CallbackId = reply.CallbackId,
                    ExternalSubject = subject?.Clone(),
                    BindingId = bindingId,
                    OwnerScopeId = ownerScopeId,
                    Succeeded = succeeded,
                    ErrorCode = succeeded ? string.Empty : expired ? "callback_expired" : "binding_commit_rejected",
                },
            };
            await PersistDomainEventAsync(new BindingCallbackDecisionRecorded
            {
                Decision = decision,
                PruneBeforeUnixMs = CallbackNow.AddDays(-1).ToUnixTimeMilliseconds(),
            });
        }
        await SendToAsync(decision.Reply.OperationActorId, decision.Outcome.Clone());
    }

    private static void ApplyCommittedCallbackDecision(
        ExternalIdentityBindingState state,
        BindingCallbackReply? reply,
        ExternalSubjectRef? subject,
        string? bindingId,
        string? ownerScopeId)
    {
        if (reply is null || string.IsNullOrWhiteSpace(reply.CallbackId))
            return;
        // Retention is bounded relative to the current operation's expiry.
        // Expired operations cannot issue new binding commands.
        PruneCallbackDecisions(state, reply.ExpiresAtUnixMs - (long)TimeSpan.FromDays(1).TotalMilliseconds);
        state.CallbackDecisions.Add(new BindingCallbackDecision
        {
            Reply = reply.Clone(),
            Outcome = new OAuthBindingOutcome
            {
                CallbackId = reply.CallbackId,
                ExternalSubject = subject?.Clone(),
                BindingId = bindingId ?? string.Empty,
                OwnerScopeId = ownerScopeId ?? string.Empty,
                Succeeded = true,
            },
        });
    }

    private static ExternalIdentityBindingState ApplyCallbackDecision(
        ExternalIdentityBindingState current, BindingCallbackDecisionRecorded recorded)
    {
        var next = current.Clone();
        PruneCallbackDecisions(next, recorded.PruneBeforeUnixMs);
        next.CallbackDecisions.Add(recorded.Decision.Clone());
        return next;
    }

    private static ExternalIdentityBindingState ApplyOwnerConfirmed(
        ExternalIdentityBindingState current, ExternalIdentityBindingOwnerConfirmedEvent confirmed)
    {
        var next = current.Clone();
        next.OwnerScopeId = confirmed.OwnerScopeId;
        ApplyCommittedCallbackDecision(next, confirmed.CallbackReply, confirmed.ExternalSubject,
            confirmed.BindingId, confirmed.OwnerScopeId);
        return next;
    }

    private static void PruneCallbackDecisions(ExternalIdentityBindingState state, long cutoff)
    {
        for (var index = state.CallbackDecisions.Count - 1; index >= 0; index--)
            if (state.CallbackDecisions[index].Reply.ExpiresAtUnixMs < cutoff)
                state.CallbackDecisions.RemoveAt(index);
    }
}
