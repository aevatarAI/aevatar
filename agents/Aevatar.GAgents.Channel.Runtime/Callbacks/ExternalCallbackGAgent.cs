using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>One external operation, one durable authority. HTTP returns never decide success.</summary>
[GAgent("channel.runtime.external-callback")]
public sealed class ExternalCallbackGAgent : GAgentBase<ExternalCallbackState>
{
    public const int MaxVerificationAttempts = 12;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DeliveryGrace = TimeSpan.FromMinutes(15);
    private long Now => (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
    private bool Terminal => State.Result != CallbackResult.Unspecified;
    private bool Consumed => State.ConsumedAtUnixMs > 0;

    protected override ExternalCallbackState TransitionState(ExternalCallbackState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ExternalCallbackStateChanged>((_, changed) => changed.State.Clone())
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        // Recover the saved lease without inventing state on a callback thread.
        if (State.Registration is not null && (!Consumed || State.AbandonPending))
            await ArmAsync(ct);
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterExternalCallback command)
    {
        var registration = command.Registration;
        if (!ValidRegistration(registration) || registration.OperationActorId != Id)
        {
            await RejectWriteAsync(command.CommandId, "invalid_registration");
            return;
        }
        var sanitized = registration.Clone();
        if (sanitized.Origin.OriginalActivity.TransportExtras is { } extras)
            extras.NyxUserAccessToken = "";
        if (State.Registration is not null)
        {
            if (!State.Registration.Equals(sanitized))
                await RejectWriteAsync(command.CommandId, "registration_conflict");
            else
                await SaveAsync(State.Clone(), command.CommandId);
            return;
        }
        var state = new ExternalCallbackState
        {
            Registration = sanitized,
            NextAttemptAtUnixMs = Now,
            TimerGeneration = 1,
            ContextRevision = 1,
            DeliveryDeadlineAtUnixMs = sanitized.ExpiresAtUnixMs + (long)DeliveryGrace.TotalMilliseconds,
            OauthPhase = ExternalCallbackAuthorizationPhase.Waiting,
        };
        await SaveAsync(state, command.CommandId);
        await ArmAsync();
        await DeliverContextAsync();
        await SendToAsync(Id, new ExternalCallbackStartRequested { CallbackId = registration.CallbackId });
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleStartAsync(ExternalCallbackStartRequested command)
    {
        if (!Matches(command.CallbackId) || Terminal || Consumed) return;
        if (Now >= State.Registration.ExpiresAtUnixMs)
        { await CompleteAsync(CallbackResult.Expired, "expired"); return; }
        if (State.LinkUrl.Length > 0) { await DeliverLinkAsync(); return; }
        if (State.Registration.Kind == ExternalCallbackKind.Oauth)
        {
            var ready = State.Clone();
            ready.LinkUrl = ready.Registration.OauthAuthorizeUrl;
            if (string.IsNullOrEmpty(ready.LinkUrl))
            { await CompleteAsync(CallbackResult.Failed, "oauth_url_missing"); return; }
            await SaveAsync(ready);
            await DeliverLinkAsync();
            return;
        }
        // The create API has no idempotency contract. Never repeat an ambiguous external create.
        if (State.LinkCreationStarted)
        { await CompleteAsync(CallbackResult.Failed, "connect_link_creation_unconfirmed"); return; }
        var state = State.Clone();
        state.LinkCreationStarted = true;
        await SaveAsync(state);
        await ArmAsync();
        ConnectLinkCreationResult created;
        try
        {
            created = await Services.GetRequiredService<IConnectLinkCreationPort>().CreateAsync(State.Registration.Clone());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Connect Link create was interrupted for {CallbackId}", command.CallbackId);
            await CompleteAsync(CallbackResult.Failed, "connect_link_creation_unconfirmed");
            return;
        }
        if (!string.IsNullOrEmpty(created.FailureCode) || string.IsNullOrWhiteSpace(created.ExternalRequestId) ||
            !Uri.TryCreate(created.ConnectUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            created.ExpiresAtUnixMs <= Now)
        { await CompleteAsync(CallbackResult.Failed, "connect_link_creation_failed"); return; }
        var linked = State.Clone();
        linked.Registration.ExternalRequestId = created.ExternalRequestId;
        linked.Registration.ExpiresAtUnixMs = Math.Min(linked.Registration.ExpiresAtUnixMs, created.ExpiresAtUnixMs);
        linked.DeliveryDeadlineAtUnixMs = linked.Registration.ExpiresAtUnixMs + (long)DeliveryGrace.TotalMilliseconds;
        linked.LinkUrl = created.ConnectUrl;
        await SaveAsync(linked);
        await DeliverLinkAsync();
    }

    [EventHandler]
    public async Task HandleLinkPresentedAsync(ExternalCallbackLinkPresented presented)
    {
        if (!Matches(presented.CallbackId) || State.Registration.LinkDeliveryMode == ExternalCallbackLinkDeliveryMode.Caller ||
            State.LinkPresented || presented.ContextRevision != 1 || State.LinkUrl.Length == 0)
            return;
        var state = State.Clone(); state.LinkPresented = true;
        await SaveAsync(state);
    }

    [EventHandler]
    public async Task HandleHintAsync(ExternalCallbackHint hint)
    {
        if (!Matches(hint.CallbackId) || State.Registration.Kind != ExternalCallbackKind.ConnectLink || Consumed ||
            string.IsNullOrWhiteSpace(State.Registration.ExternalRequestId) ||
            (!string.IsNullOrEmpty(hint.ExternalRequestId) && hint.ExternalRequestId != State.Registration.ExternalRequestId))
            return;
        if (Terminal) { await DeliverAsync(); return; }
        if (Now < State.NextAttemptAtUnixMs)
            return;
        await AdvanceAsync();
    }

    [EventHandler]
    public async Task HandleOAuthAsync(OAuthContinuationSubmission submission)
    {
        if (!Matches(submission.CallbackId) || State.Registration.Kind != ExternalCallbackKind.Oauth ||
            !Equals(submission.ExternalSubject, State.Registration.Authorization.ExternalSubject))
            return;
        if (Consumed) return;
        if (Terminal) { await DeliverAsync(); return; }
        if (Now >= State.Registration.ExpiresAtUnixMs)
        { await CompleteAsync(CallbackResult.Expired, "expired"); return; }
        // An admitted code is never exchanged a second time, even after a crash.
        if (State.OauthPhase != ExternalCallbackAuthorizationPhase.Waiting)
            return;
        if (!string.IsNullOrEmpty(submission.OauthError))
        {
            await CompleteAsync(submission.OauthError == "access_denied" ? CallbackResult.Cancelled : CallbackResult.Failed, "oauth_denied");
            return;
        }
        if (string.IsNullOrWhiteSpace(submission.AuthorizationCode) || string.IsNullOrWhiteSpace(submission.PkceVerifier))
            return;
        var started = State.Clone();
        started.OauthPhase = ExternalCallbackAuthorizationPhase.ExchangeStarted;
        await SaveAsync(started);
        await ArmAsync();
        try
        {
            var prepared = await Services.GetRequiredService<IOAuthContinuationExecutionPort>().ExchangeAsync(submission.Clone());
            var state = State.Clone();
            state.OauthPreparation = prepared.Clone();
            state.OauthPreparation.ExpiresAtUnixMs = State.Registration.ExpiresAtUnixMs;
            state.OauthPhase = ExternalCallbackAuthorizationPhase.Prepared;
            await SaveAsync(state);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "OAuth exchange was interrupted for callback {CallbackId}; no code replay", State.Registration.CallbackId);
            await CompleteAsync(CallbackResult.Failed, "oauth_exchange_unconfirmed");
            return;
        }
        await AdvanceAsync();
    }

    [EventHandler]
    public async Task HandleOAuthOutcomeAsync(OAuthBindingOutcome outcome)
    {
        if (!Matches(outcome.CallbackId) || State.Registration.Kind != ExternalCallbackKind.Oauth ||
            State.OauthPreparation is not { } preparation ||
            (State.OauthPhase != ExternalCallbackAuthorizationPhase.BindingPending && !outcome.Abandoned) ||
            !Equals(outcome.ExternalSubject, preparation.ExternalSubject) ||
            outcome.BindingId != preparation.BindingId || outcome.OwnerScopeId != preparation.OwnerScopeId)
            return;
        if (outcome.Abandoned)
        {
            if (State.AbandonPending)
            {
                var cleaned = State.Clone(); cleaned.AbandonPending = false;
                await SaveAsync(cleaned);
            }
            return;
        }
        if (Terminal)
        {
            if ((State.Result == CallbackResult.Succeeded) != outcome.Succeeded)
                Logger.LogWarning("Conflicting terminal OAuth result ignored for {CallbackId}", outcome.CallbackId);
            return;
        }
        if (Now >= State.Registration.ExpiresAtUnixMs)
        { await CompleteAsync(CallbackResult.Expired, "expired"); return; }
        await CompleteAsync(outcome.Succeeded ? CallbackResult.Succeeded : CallbackResult.Failed,
            outcome.ErrorCode, outcome.Succeeded ? new ExternalCallbackVerifiedReferences
            { BindingId = outcome.BindingId, OwnerScopeId = outcome.OwnerScopeId } : null);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleRetryAsync(ExternalCallbackRetryFired retry)
    {
        if (!Matches(retry.CallbackId) || retry.Generation != State.TimerGeneration || (Consumed && !State.AbandonPending))
            return;
        if (!Consumed && Now >= DeliveryDeadlineAtUnixMs)
        {
            await CompleteDeliveryDeadlineAsync();
            return;
        }
        if (Terminal)
        {
            var next = State.Clone();
            next.NextAttemptAtUnixMs = Now + (long)RetryInterval.TotalMilliseconds;
            next.TimerGeneration++;
            await SaveAsync(next);
            await ArmAsync();
            await AbandonAsync();
            await DeliverAsync();
            return;
        }
        if (Now < State.NextAttemptAtUnixMs) { await ArmAsync(); return; }
        // The one-shot lease has been consumed. Advance and arm its successor before
        // any context/link publication, including OAuth startup, can fail this turn.
        await AdvanceAsync();
        if (Terminal) return;
        if (State.LinkUrl.Length == 0)
        {
            await HandleStartAsync(new() { CallbackId = retry.CallbackId });
            if (Terminal) return;
        }
        await DeliverLinkAsync();
    }

    [EventHandler]
    public async Task HandleContextAcceptedAsync(ExternalCallbackActionContextAccepted accepted)
    {
        if (!Matches(accepted.CallbackId) || Consumed || accepted.ContextRevision != State.ContextRevision)
            return;
        if (!Terminal)
        {
            if (!State.LinkContextAccepted)
            {
                var initial = State.Clone(); initial.LinkContextAccepted = true;
                await SaveAsync(initial);
            }
            await DeliverLinkAsync();
            return;
        }
        if (!State.CompletionContextAccepted)
        {
            var state = State.Clone(); state.CompletionContextAccepted = true;
            await SaveAsync(state);
        }
        await DeliverAsync();
    }

    [EventHandler]
    public async Task HandleContinuationRejectedAsync(ExternalCallbackContinuationRejected rejected)
    {
        if (!Matches(rejected.CallbackId))
            return;

        var failureCode = string.IsNullOrWhiteSpace(rejected.FailureCode)
            ? "continuation_rejected" : rejected.FailureCode;
        if (Consumed)
        {
            // Resume admission may have already consumed the operation before
            // final Channel delivery fails. Preserve the authoritative callback
            // result, while recording the exact terminal delivery receipt.
            if (rejected.ContextRevision != State.ContextRevision || State.DeliveryFailureCode == failureCode)
                return;
            var consumedState = State.Clone();
            consumedState.DeliveryFailureCode = failureCode;
            consumedState.CompletionContextAccepted = true;
            consumedState.TimerGeneration++;
            await SaveAsync(consumedState);
            if (consumedState.AbandonPending)
                await ArmAsync();
            return;
        }
        if (rejected.ContextRevision != State.ContextRevision)
            return;

        // The original conversation has durably declared this route
        // unreachable. Preserve an existing authoritative result; otherwise
        // terminalize as failed without waiting for another ACK that cannot arrive.
        var state = State.Clone();
        if (!Terminal)
        {
            state.Result = CallbackResult.Failed;
            state.FailureCode = failureCode;
            state.ContextRevision++;
            state.AbandonPending = (state.OauthPhase is ExternalCallbackAuthorizationPhase.Prepared or ExternalCallbackAuthorizationPhase.BindingPending) &&
                state.OauthPreparation is { BindingUpdated: false, BindingId.Length: > 0 };
        }
        state.CompletionContextAccepted = true;
        state.DeliveryFailureCode = failureCode;
        state.ConsumedAtUnixMs = Now;
        state.TimerGeneration++;
        state.NextAttemptAtUnixMs = Now;
        await SaveAsync(state);
        await AbandonAsync();
        if (state.AbandonPending)
            await ArmAsync();
    }

    [EventHandler]
    public async Task HandleConsumedAsync(CallbackCompletionConsumed consumed)
    {
        if (!Matches(consumed.CallbackId) || !Terminal || Consumed || consumed.Result != State.Result || !State.CompletionContextAccepted)
            return;
        var state = State.Clone();
        state.ConsumedAtUnixMs = Now;
        state.TimerGeneration++;
        await SaveAsync(state);
        if (state.AbandonPending) await ArmAsync();
    }

    private async Task AdvanceAsync()
    {
        if (Now >= State.Registration.ExpiresAtUnixMs)
        { await CompleteAsync(CallbackResult.Expired, "expired"); return; }
        if (State.OauthPhase == ExternalCallbackAuthorizationPhase.ExchangeStarted)
        { await CompleteAsync(CallbackResult.Failed, "oauth_exchange_unconfirmed"); return; }
        var state = State.Clone();
        state.NextAttemptAtUnixMs = Now + (long)RetryInterval.TotalMilliseconds;
        state.TimerGeneration++;
        // Waiting for an OAuth browser return or link creation consumes time, not verification attempts.
        var canVerify = state.Registration.Kind == ExternalCallbackKind.ConnectLink
            ? state.Registration.ExternalRequestId.Length > 0
            : state.OauthPreparation is not null;
        // The persisted counter is authoritative across activation/crash
        // windows. Hints cannot bypass an exhausted budget, and the provider
        // is never called after the limit was durably reserved.
        if (canVerify && State.VerificationAttempts >= MaxVerificationAttempts)
        {
            await CompleteAsync(CallbackResult.Failed, "verification_retry_exhausted");
            return;
        }
        // Every actual provider verification consumes one persisted slot,
        // including callback hints. Healthy pending responses clear the slot.
        if (canVerify) state.VerificationAttempts++;
        await SaveAsync(state);
        // Lease first: provider failure or process interruption cannot remove the retry.
        await ArmAsync();
        if (!canVerify) return;
        try
        {
            if (State.Registration.Kind == ExternalCallbackKind.ConnectLink)
            {
                var result = await Services.GetRequiredService<IConnectLinkVerificationPort>().VerifyAsync(State.Registration.Clone());
                if (result.Disposition == CallbackVerificationDisposition.Succeeded)
                {
                    if (!ValidConnectReferences(result.References))
                        await CompleteAsync(CallbackResult.Failed, "verification_reference_mismatch");
                    else
                        await CompleteAsync(CallbackResult.Succeeded, "", result.References);
                }
                else if (result.Disposition is not (CallbackVerificationDisposition.Pending or CallbackVerificationDisposition.Unspecified or CallbackVerificationDisposition.Unavailable))
                    await CompleteAsync(result.Disposition switch
                    {
                        CallbackVerificationDisposition.Cancelled => CallbackResult.Cancelled,
                        CallbackVerificationDisposition.Expired => CallbackResult.Expired,
                        _ => CallbackResult.Failed,
                    }, result.FailureCode);
                else if (result.Disposition == CallbackVerificationDisposition.Pending)
                {
                    // A valid exact response showing that the user is still
                    // completing the link is healthy progress. It clears the
                    // consecutive unavailable-provider budget.
                    var healthy = State.Clone();
                    healthy.VerificationAttempts = 0;
                    await SaveAsync(healthy);
                    await ArmAsync();
                }
            }
            else
            {
                var driver = Services.GetRequiredService<IOAuthContinuationExecutionPort>();
                if (State.OauthPhase == ExternalCallbackAuthorizationPhase.Prepared)
                {
                    var verified = await driver.ValidateAsync(State.OauthPreparation.Clone());
                    if (verified.Rejected || (!verified.IsTransientFailure && !verified.VerificationSucceeded))
                    { await CompleteAsync(CallbackResult.Failed, verified.ErrorCode); return; }
                    if (verified.IsTransientFailure)
                    {
                        if (State.VerificationAttempts >= MaxVerificationAttempts)
                            await CompleteAsync(CallbackResult.Failed, "verification_retry_exhausted");
                        return;
                    }
                    var pending = State.Clone();
                    pending.OauthPreparation = verified.Clone();
                    pending.OauthPreparation.ExpiresAtUnixMs = State.Registration.ExpiresAtUnixMs;
                    pending.OauthPhase = ExternalCallbackAuthorizationPhase.BindingPending;
                    // A successful authoritative validation ends its failure
                    // streak. Reserve the first binding-dispatch attempt in
                    // this same durable commit before invoking that port.
                    pending.VerificationAttempts = 1;
                    await SaveAsync(pending);
                }
                await driver.DispatchBindingAsync(State.OauthPreparation.Clone(), State.Registration.CallbackId, Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "External callback verification unavailable for {CallbackId}", State.Registration.CallbackId);
        }
        if (!Terminal && State.VerificationAttempts >= MaxVerificationAttempts)
            await CompleteAsync(CallbackResult.Failed, "verification_retry_exhausted");
    }

    private bool ValidConnectReferences(ExternalCallbackVerifiedReferences? refs) => refs is not null &&
        refs.ConnectLinkId == State.Registration.ExternalRequestId &&
        refs.CatalogServiceSlug == State.Registration.RequestedCatalogServiceSlug &&
        refs.BindingId == State.Registration.Authorization.BindingId &&
        refs.OwnerScopeId == State.Registration.Authorization.OwnerScopeId &&
        !string.IsNullOrWhiteSpace(refs.ConnectedServiceId) && !string.IsNullOrWhiteSpace(refs.ConnectedServiceSlug);

    private long DeliveryDeadlineAtUnixMs => State.DeliveryDeadlineAtUnixMs > 0
        ? State.DeliveryDeadlineAtUnixMs
        : State.Registration.ExpiresAtUnixMs + (long)DeliveryGrace.TotalMilliseconds;

    private async Task CompleteDeliveryDeadlineAsync()
    {
        if (Consumed) return;
        var state = State.Clone();
        if (!Terminal)
        {
            state.Result = CallbackResult.Expired;
            state.FailureCode = "delivery_deadline_exceeded";
            state.ContextRevision++;
            state.AbandonPending = (state.OauthPhase is ExternalCallbackAuthorizationPhase.Prepared or ExternalCallbackAuthorizationPhase.BindingPending) &&
                state.OauthPreparation is { BindingUpdated: false, BindingId.Length: > 0 };
        }
        state.CompletionContextAccepted = true;
        state.DeliveryFailureCode = "delivery_deadline_exceeded";
        state.ConsumedAtUnixMs = Now;
        state.TimerGeneration++;
        state.NextAttemptAtUnixMs = Now;
        await SaveAsync(state);
        await AbandonAsync();
        if (state.AbandonPending)
            await ArmAsync();
    }

    private async Task CompleteAsync(CallbackResult result, string failureCode, ExternalCallbackVerifiedReferences? references = null)
    {
        if (Terminal) return;
        var state = State.Clone();
        state.Result = result;
        state.FailureCode = failureCode ?? "";
        state.VerifiedReferences = references?.Clone();
        state.AbandonPending = result != CallbackResult.Succeeded &&
            state.OauthPhase is ExternalCallbackAuthorizationPhase.Prepared or ExternalCallbackAuthorizationPhase.BindingPending &&
            state.OauthPreparation is { BindingUpdated: false, BindingId.Length: > 0 };
        state.ContextRevision++;
        state.CompletionContextAccepted = false;
        state.TimerGeneration++;
        state.NextAttemptAtUnixMs = Now + (long)RetryInterval.TotalMilliseconds;
        if (result == CallbackResult.Succeeded && state.Registration.Kind == ExternalCallbackKind.Oauth)
        {
            state.Registration.Authorization.BindingId = references!.BindingId;
            state.Registration.Authorization.OwnerScopeId = references.OwnerScopeId;
        }
        await SaveAsync(state);
        await ArmAsync();
        await AbandonAsync();
        await DeliverAsync();
    }

    private async Task AbandonAsync()
    {
        if (!State.AbandonPending) return;
        try
        {
            await Services.GetRequiredService<IOAuthContinuationExecutionPort>().AbandonAsync(
                State.OauthPreparation.Clone(), State.Registration.CallbackId, Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Deferred OAuth binding retirement for {CallbackId}", State.Registration.CallbackId);
        }
    }

    private Task DeliverContextAsync() => SendToAsync(State.Registration.Origin.ConversationActorId,
        new ExternalCallbackActionContextRecorded
        {
            CallbackId = State.Registration.CallbackId, OperationActorId = Id,
            Origin = State.Registration.Origin.Clone(), Authorization = State.Registration.Authorization.Clone(),
            VerifiedReferences = State.VerifiedReferences?.Clone(), ContextRevision = State.ContextRevision,
        });

    private Task DeliverLinkAsync() => Terminal || State.LinkPresented || State.LinkUrl.Length == 0
        ? Task.CompletedTask
        : !State.LinkContextAccepted ? DeliverContextAsync()
        : State.Registration.LinkDeliveryMode == ExternalCallbackLinkDeliveryMode.Caller ? Task.CompletedTask
        : SendToAsync(State.Registration.Origin.ConversationActorId, new ExternalCallbackLinkReady
        {
            CallbackId = State.Registration.CallbackId, OperationActorId = Id,
            Origin = State.Registration.Origin.Clone(), ConnectUrl = State.LinkUrl,
            Kind = State.Registration.Kind, ContextRevision = 1,
        });

    private Task DeliverAsync() => Consumed ? Task.CompletedTask : !State.CompletionContextAccepted
        ? DeliverContextAsync()
        : SendToAsync(State.Registration.Origin.ConversationActorId,
            new CallbackCompleted { CallbackId = State.Registration.CallbackId, Result = State.Result });

    private async Task ArmAsync(CancellationToken ct = default)
    {
        var registration = State.Registration;
        if (registration is null || (Consumed && !State.AbandonPending)) return;
        var due = Math.Max(1, State.NextAttemptAtUnixMs - Now);
        if (!Terminal)
            due = Math.Min(due, Math.Max(1, registration.ExpiresAtUnixMs - Now));
        await ScheduleSelfDurableTimeoutAsync("external-callback-retry", TimeSpan.FromMilliseconds(due),
            new ExternalCallbackRetryFired { CallbackId = registration.CallbackId, Generation = State.TimerGeneration }, ct: ct);
    }

    private Task SaveAsync(ExternalCallbackState state, string commandId = "")
    {
        state.Revision = State.Revision + 1;
        state.Snapshot = new ExternalCallbackSnapshot
        {
            CallbackId = state.Registration.CallbackId, OperationActorId = Id,
            ExternalRequestId = state.Registration.ExternalRequestId, Kind = state.Registration.Kind,
            Result = state.Result, ExpiresAtUnixMs = state.Registration.ExpiresAtUnixMs, ConsumedAtUnixMs = state.ConsumedAtUnixMs,
        };
        return PersistDomainEventAsync(new ExternalCallbackStateChanged { State = state, CommandId = commandId });
    }

    private Task RejectWriteAsync(string commandId, string reason) =>
        PersistDomainEventAsync(new ExternalCallbackWriteRejected { CommandId = commandId, RejectionCode = reason });
    private bool Matches(string callbackId) => State.Registration is not null && State.Registration.CallbackId == callbackId;
    private bool ValidRegistration(ExternalCallbackRegistration? value) => value is not null &&
        !string.IsNullOrWhiteSpace(value.CallbackId) && value.Kind is ExternalCallbackKind.Oauth or ExternalCallbackKind.ConnectLink &&
        (value.LinkDeliveryMode is ExternalCallbackLinkDeliveryMode.Unspecified or ExternalCallbackLinkDeliveryMode.Conversation ||
         value.LinkDeliveryMode == ExternalCallbackLinkDeliveryMode.Caller && value.Kind == ExternalCallbackKind.Oauth) &&
        value.ExpiresAtUnixMs > 0 && value.Origin?.OriginalActivity is not null &&
        !string.IsNullOrWhiteSpace(value.Origin.ConversationActorId) && !string.IsNullOrWhiteSpace(value.Origin.ChannelRegistrationId) &&
        !string.IsNullOrWhiteSpace(value.Origin.ActionId) && value.Authorization?.ExternalSubject is not null &&
        (value.Kind != ExternalCallbackKind.ConnectLink ||
         (!string.IsNullOrWhiteSpace(value.Authorization.BindingId) && !string.IsNullOrWhiteSpace(value.Authorization.OwnerScopeId) &&
          !string.IsNullOrWhiteSpace(value.RequestedCatalogServiceSlug)));
}
