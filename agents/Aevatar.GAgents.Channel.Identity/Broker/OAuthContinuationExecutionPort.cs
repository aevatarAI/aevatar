using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Identity adapter used by the callback operation authority. Code exchange,
/// validation and binding dispatch are separate so the single-use exchange
/// result is persisted before any retryable verification or inbox delivery.
/// </summary>
public sealed class OAuthContinuationExecutionPort(
    INyxIdBrokerCallbackClient callbackClient,
    INyxIdCapabilityBroker capabilityBroker,
    IExternalIdentityBindingQueryPort bindingQuery,
    IOwnerScopeResolver ownerScopeResolver,
    ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> commitDispatch,
    ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> replaceDispatch,
    ICommandDispatchService<ConfirmBindingGrantCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> confirmDispatch,
    ICommandDispatchService<AbandonBindingPreparationCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> abandonDispatch,
    ICommandDispatchService<ObserveBrokerCapabilityCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> capabilityDispatch,
    ILogger<OAuthContinuationExecutionPort> logger) : IOAuthContinuationExecutionPort
{
    public async Task<OAuthBindingPreparation> ExchangeAsync(
        OAuthContinuationSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var prepared = new OAuthBindingPreparation
        {
            ExternalSubject = submission.ExternalSubject.Clone(),
            ExpiresAtUnixMs = submission.ExpiresAtUnixMs,
        };

        // Query before consuming the authorization code. Transient local query
        // failures therefore cannot discard an already exchanged binding.
        var existing = await bindingQuery.ResolveAsync(submission.ExternalSubject, ct).ConfigureAwait(false);
        prepared.ExpectedPreviousBindingId = existing?.Value ?? string.Empty;
        var currentHash = existing is null ? string.Empty : NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value);
        if (!string.Equals(currentHash, submission.ExpectedBindingHash, StringComparison.Ordinal))
            return Rejected(prepared, "binding_changed_during_review");

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await callbackClient.ExchangeAuthorizationCodeAsync(
                submission.AuthorizationCode, submission.PkceVerifier, ct).ConfigureAwait(false);
        }
        catch (NyxIdRequiredServiceAccessException)
        {
            return Rejected(prepared, "required_service_access_missing");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return Rejected(prepared, "authorization_code_rejected");
        }

        prepared.BindingUpdated = exchange.BindingUpdated;
        prepared.BindingId = exchange.BindingUpdated ? existing?.Value ?? string.Empty : exchange.BindingId ?? string.Empty;
        prepared.OwnerScopeId = OAuthBindingVerification.ResolveOwnerScopeId(exchange.IdToken) ?? string.Empty;
        if (prepared.BindingId.Length == 0)
            return Rejected(prepared, exchange.BindingUpdated ? "binding_changed_during_review" : "broker_capability_disabled");
        if (!exchange.BindingUpdated && prepared.OwnerScopeId.Length == 0)
        {
            return Rejected(prepared, "owner_scope_missing");
        }
        return prepared;
    }

    public async Task<OAuthBindingPreparation> ValidateAsync(
        OAuthBindingPreparation preparation, CancellationToken ct = default)
    {
        var prepared = preparation.Clone();
        if (prepared.Rejected)
            return prepared;
        prepared.IsTransientFailure = false;
        prepared.ErrorCode = string.Empty;
        try
        {
            // Re-check the exact reference on every retry. The binding actor
            // performs the final compare-and-swap on its authoritative state.
            var current = await bindingQuery.ResolveAsync(prepared.ExternalSubject, ct).ConfigureAwait(false);
            if (!string.Equals(current?.Value ?? string.Empty, prepared.ExpectedPreviousBindingId, StringComparison.Ordinal))
                return Rejected(prepared, "binding_changed_during_review");

            if (prepared.ExpectedPreviousBindingId.Length > 0)
            {
                var owner = await ownerScopeResolver.ResolveAsync(prepared.ExternalSubject, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(owner?.Value))
                    owner = await callbackClient.ResolveBindingOwnerScopeAsync(prepared.ExpectedPreviousBindingId, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(owner?.Value))
                    return Rejected(prepared, "binding_owner_missing");
                if (prepared.BindingUpdated && prepared.OwnerScopeId.Length == 0)
                    prepared.OwnerScopeId = owner.Value;
                if (!string.Equals(owner.Value, prepared.OwnerScopeId, StringComparison.Ordinal))
                    return Rejected(prepared, "binding_owner_mismatch");
            }

            var probe = await OAuthBindingVerification.ProbeIssuedBindingAsync(
                capabilityBroker, prepared.ExternalSubject, prepared.BindingId, logger, ct).ConfigureAwait(false);
            if (probe == OAuthBindingVerification.IssuedBindingProbeResult.Unavailable)
                return Transient(prepared, "issued_binding_probe_failed");
            if (probe != OAuthBindingVerification.IssuedBindingProbeResult.Usable)
                return Rejected(prepared,
                    probe == OAuthBindingVerification.IssuedBindingProbeResult.Invalid
                        ? "issued_binding_invalid" : "required_service_access_missing");

            prepared.VerificationSucceeded = true;
            return prepared;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OAuth binding verification remains pending.");
            return Transient(prepared, "binding_verification_unavailable");
        }
    }

    public async Task DispatchBindingAsync(
        OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default)
    {
        if (!preparation.VerificationSucceeded || preparation.Rejected)
            throw new InvalidOperationException("Binding dispatch requires verified OAuth preparation.");
        var reply = new BindingCallbackReply
        {
            CallbackId = callbackId,
            OperationActorId = operationActorId,
            ExpiresAtUnixMs = preparation.ExpiresAtUnixMs,
        };
        var accepted = preparation.BindingUpdated
            ? await confirmDispatch.DispatchAsync(new ConfirmBindingGrantCommand
            {
                ExternalSubject = preparation.ExternalSubject.Clone(),
                BindingId = preparation.BindingId,
                OwnerScopeId = preparation.OwnerScopeId,
                CallbackReply = reply,
            }, ct).ConfigureAwait(false)
            : preparation.ExpectedPreviousBindingId.Length > 0
                ? await replaceDispatch.DispatchAsync(new ReplaceBindingCommand
                {
                    ExternalSubject = preparation.ExternalSubject.Clone(),
                    BindingId = preparation.BindingId,
                    OwnerScopeId = preparation.OwnerScopeId,
                    ExpectedPreviousBindingId = preparation.ExpectedPreviousBindingId,
                    Reason = "channel_service_access_review",
                    CallbackReply = reply,
                }, ct).ConfigureAwait(false)
                : await commitDispatch.DispatchAsync(new CommitBindingCommand
                {
                    ExternalSubject = preparation.ExternalSubject.Clone(),
                    BindingId = preparation.BindingId,
                    OwnerScopeId = preparation.OwnerScopeId,
                    CallbackReply = reply,
                }, ct).ConfigureAwait(false);
        if (!accepted.Succeeded || accepted.Receipt is null)
            throw new InvalidOperationException("OAuth binding command was not accepted for dispatch.");

        try { await capabilityDispatch.DispatchAsync(new ObserveBrokerCapabilityCommand(), ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Unable to record OAuth broker capability observation.");
        }
    }

    public async Task AbandonAsync(OAuthBindingPreparation preparation, string callbackId, string operationActorId, CancellationToken ct = default)
    {
        // In-place grant review owns no newly issued binding to retire.
        if (preparation.BindingUpdated || string.IsNullOrWhiteSpace(preparation.BindingId))
            return;
        var accepted = await abandonDispatch.DispatchAsync(new AbandonBindingPreparationCommand
        {
            ExternalSubject = preparation.ExternalSubject.Clone(),
            BindingId = preparation.BindingId,
            OwnerScopeId = preparation.OwnerScopeId,
            CallbackReply = new BindingCallbackReply
            {
                CallbackId = callbackId, OperationActorId = operationActorId, ExpiresAtUnixMs = preparation.ExpiresAtUnixMs,
            },
        }, ct).ConfigureAwait(false);
        if (!accepted.Succeeded || accepted.Receipt is null)
            throw new InvalidOperationException("OAuth binding retirement was not accepted for dispatch.");
    }

    private static OAuthBindingPreparation Rejected(OAuthBindingPreparation preparation, string error)
    {
        preparation.Rejected = true;
        preparation.VerificationSucceeded = false;
        preparation.ErrorCode = error;
        return preparation;
    }

    private static OAuthBindingPreparation Transient(OAuthBindingPreparation preparation, string error)
    {
        preparation.IsTransientFailure = true;
        preparation.VerificationSucceeded = false;
        preparation.ErrorCode = error;
        return preparation;
    }
}
