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
    /// <c>invalid_scope</c> for an existing binding. Callers MUST event-source
    /// revoke the local binding actor on invalid_grant and prompt the sender
    /// to re-run <c>/init</c> for both user-remediable cases.
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
    Task<CapabilityHandle> IssueShortLivedAsync(
        ExternalSubjectRef externalSubject,
        CapabilityScope scope,
        CancellationToken ct = default);
}
