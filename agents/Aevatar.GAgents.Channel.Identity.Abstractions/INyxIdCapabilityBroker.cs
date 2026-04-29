namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Capability layer seam between channel runtime and NyxID broker.
/// Production implementation issues no long-lived user secret material into
/// aevatar grain state; aevatar holds only the opaque <see cref="BindingId"/>.
/// See ADR-0017 §INyxIdCapabilityBroker.
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
    /// Resolves the active binding pointer for the given external subject.
    /// Returns <c>null</c> when no active binding exists. Reads the local
    /// projection only — does not call NyxID.
    /// </summary>
    Task<BindingId?> ResolveBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the binding both at NyxID (source of truth) and locally.
    /// NyxID failures abort the local revoke to avoid source-of-truth divergence.
    /// </summary>
    Task RevokeBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default);

    /// <summary>
    /// Issues a short-lived access token for the resolved binding via RFC 8693
    /// token-exchange. Throws <see cref="BindingRevokedException"/> when NyxID
    /// reports <c>invalid_grant</c> (binding revoked); callers should
    /// event-source revoke the local binding actor and prompt the sender to
    /// re-run <c>/init</c>.
    /// </summary>
    /// <exception cref="BindingRevokedException">
    /// NyxID reports the binding as revoked (HTTP 400 <c>invalid_grant</c>).
    /// </exception>
    Task<CapabilityHandle> IssueShortLivedAsync(
        ExternalSubjectRef externalSubject,
        CapabilityScope scope,
        CancellationToken ct = default);
}
