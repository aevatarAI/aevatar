using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Callback-side surface used by the OAuth callback endpoint to finish a
/// binding flow. Distinct from <c>INyxIdCapabilityBroker</c> (which is the
/// per-turn write-side seam used by <c>ChannelConversationTurnRunner</c>);
/// the callback handler depends on this narrower contract so the seam is
/// obvious in the dependency graph. See ADR-0018 §Decision (`/init` flow).
/// </summary>
public interface INyxIdBrokerCallbackClient
{
    /// <summary>
    /// Validates and decodes the incoming <c>state</c> token. Async because
    /// the HMAC key lives behind <c>IAevatarOAuthClientProvider</c>
    /// (cluster-singleton actor).
    /// </summary>
    Task<CallbackStateDecode> TryDecodeStateTokenAsync(string stateToken, CancellationToken ct = default);

    /// <summary>
    /// Exchanges the OAuth authorization code for a binding handle via
    /// <c>POST /oauth/token</c> with <c>grant_type=authorization_code</c>.
    /// Under the broker scope (<c>urn:nyxid:scope:broker_binding</c>), NyxID
    /// returns <c>binding_id</c> instead of <c>refresh_token</c> when
    /// <c>broker_capability_enabled=true</c>; if the flag is off,
    /// <c>BindingId</c> is null and the callback handler surfaces the gap to
    /// ops (one-time NyxID-admin step).
    /// </summary>
    Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke a specific NyxID-issued <paramref name="bindingId"/> by id.
    /// Used by the callback handler to clean up an orphan binding when the
    /// sender is already bound (race / replay) — without the subject lookup
    /// the standard <c>RevokeBindingAsync</c> does. Best-effort; throws on
    /// hard NyxID failures (5xx) but treats 404 / 410 as already-cleaned.
    /// </summary>
    Task RevokeBindingByIdAsync(string bindingId, CancellationToken ct = default);
}

/// <summary>
/// Result of an authorization-code -> binding-id exchange. <see cref="BindingId"/>
/// may be null when NyxID has not yet enabled <c>broker_capability_enabled</c>
/// on this client (see ADR-0018 §Decision).
/// </summary>
public sealed record BrokerAuthorizationCodeResult(string? BindingId, string? IdToken, string? AccessToken);
