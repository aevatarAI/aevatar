using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Callback-side surface used by the OAuth callback endpoint to finish a
/// binding flow. Distinct from <c>INyxIdCapabilityBroker</c> (which is the
/// per-turn write-side seam used by <c>ChannelConversationTurnRunner</c>);
/// the callback handler depends on this narrower contract so the seam is
/// obvious in the dependency graph. See ADR-0017 §Decision (`/init` flow).
/// </summary>
public interface INyxIdBrokerCallbackClient
{
    /// <summary>
    /// Validates and decodes the incoming <c>state</c> token. Returns
    /// <c>true</c> with the carried correlation/subject/verifier when the
    /// HMAC + expiry are valid; otherwise <c>false</c> with an
    /// <paramref name="errorCode"/> identifying the failure mode (see
    /// ADR-0017 §Implementation Notes #3 for the user-facing UX classes:
    /// <c>state_expired</c>, <c>state_signature_invalid</c>,
    /// <c>state_payload_invalid</c>, <c>state_malformed</c>,
    /// <c>state_kid_unknown</c>, <c>state_missing</c>).
    /// </summary>
    bool TryDecodeStateToken(
        string stateToken,
        out string correlationId,
        out ExternalSubjectRef? externalSubject,
        out string pkceVerifier,
        out string? errorCode);

    /// <summary>
    /// Exchanges the OAuth authorization code for a binding handle via
    /// <c>POST /oauth/token</c> with <c>grant_type=authorization_code</c>.
    /// Under the broker scope (<c>urn:nyxid:scope:broker_binding</c>), NyxID
    /// returns <c>binding_id</c> instead of <c>refresh_token</c>; aevatar
    /// holds the binding pointer only.
    /// </summary>
    Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct = default);
}

/// <summary>
/// Result of an authorization-code -> binding-id exchange. <see cref="BindingId"/>
/// is the opaque handle the broker hands back to aevatar; <see cref="IdToken"/>
/// is the OIDC ID token (carries the <c>sub</c>/<c>name</c> claims so the
/// callback handler can render an "已绑定 &lt;name&gt;" message without a
/// separate <c>/oauth/userinfo</c> round-trip — see ADR-0017 §Decision).
/// Both tokens are one-shot; the callback handler MUST NOT persist them.
/// </summary>
public sealed record BrokerAuthorizationCodeResult(string BindingId, string? IdToken, string? AccessToken);
