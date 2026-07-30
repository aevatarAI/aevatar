using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Capability layer seam between channel runtime and NyxID broker. **Write-side
/// only**: starting bindings, revoking bindings, and issuing short-lived
/// tokens. Read-side queries (resolve external subject -> binding) live on
/// <see cref="IExternalIdentityBindingQueryPort"/>; broker callers MUST go
/// through that port for reads so the read/write seams stay distinct.
/// Production implementation issues no long-lived user secret material into
/// aevatar grain state; aevatar holds only the opaque <see cref="BindingId"/>.
/// See ADR-0018 §INyxIdCapabilityBroker.
/// </summary>
public interface INyxIdCapabilityBroker
{
    /// <summary>
    /// Starts a new OAuth Authorization Code + PKCE binding flow for the given
    /// external subject and returns the authorize URL plus expiry. Caller is
    /// responsible for delivering the URL via a private channel (e.g. Lark DM)
    /// to prevent OAuth state hijack.
    /// </summary>
    Task<BindingChallenge> StartExternalBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the binding both at NyxID (source of truth) and locally.
    /// NyxID failures abort the local revoke to avoid source-of-truth divergence
    /// — see ADR-0018 §Decision (`/unbind` behaviour).
    /// </summary>
    Task RevokeBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a short-lived access token for the resolved binding via RFC 8693
    /// token-exchange. Throws <see cref="BindingNotFoundException"/> when no
    /// active binding exists for <paramref name="externalSubject"/>; throws
    /// <see cref="BindingRevokedException"/> when NyxID reports
    /// <c>invalid_grant</c> on a previously-bound subject; throws
    /// <see cref="BindingScopeMismatchException"/> when NyxID reports
    /// <c>invalid_scope</c> for an existing binding; throws
    /// <see cref="BindingServiceAccessMismatchException"/> when the binding's
    /// token does not grant every required service resource. Binding-required callers
    /// can prompt the sender to re-run <c>/init</c>; normal LLM turns can
    /// continue with bot-owner fallback credentials.
    /// </summary>
    /// <exception cref="BindingNotFoundException">
    /// No active binding exists for the subject (never bound, or readmodel
    /// has not yet observed the bind).
    /// </exception>
    /// <exception cref="BindingRevokedException">
    /// NyxID reports the binding as revoked (HTTP 400 <c>invalid_grant</c>).
    /// </exception>
    /// <exception cref="BindingScopeMismatchException">
    /// NyxID reports the binding is missing the requested scope (HTTP 400
    /// <c>invalid_scope</c>).
    /// </exception>
    /// <exception cref="BindingServiceAccessMismatchException">
    /// The binding does not grant every required NyxID service resource.
    /// </exception>
    Task<CapabilityHandle> IssueShortLivedAsync(
        ExternalSubjectRef externalSubject,
        CapabilityScope scope,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a short-lived access token directly from a known
    /// <paramref name="bindingId"/>, skipping the readmodel resolve that
    /// <see cref="IssueShortLivedAsync"/> performs. Use when the caller already
    /// holds the persisted binding id (e.g. a deferred reply run that carried it
    /// as an identity fact across the run boundary), so token re-mint does not
    /// depend on the readmodel having observed the bind. Same exception contract
    /// as <see cref="IssueShortLivedAsync"/>: throws
    /// <see cref="BindingRevokedException"/> on NyxID <c>invalid_grant</c> and
    /// <see cref="BindingScopeMismatchException"/> on <c>invalid_scope</c>, and
    /// <see cref="BindingServiceAccessMismatchException"/> when service access is absent.
    /// </summary>
    Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CapabilityScope scope,
        CancellationToken ct = default);
}
