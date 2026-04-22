namespace Aevatar.Foundation.Abstractions.Credentials;

/// <summary>
/// Stable auth intent that can cross module boundaries without carrying a raw secret.
/// The credential reference is late-bound and resolved only at the provider edge.
/// </summary>
public sealed record AuthContext
{
    public AuthContext(
        AuthPrincipal principal,
        string? principalId = null,
        string? credentialRef = null,
        string? onBehalfOfUserId = null)
    {
        Principal = principal;
        PrincipalId = Normalize(principalId);
        CredentialRef = Normalize(credentialRef);
        OnBehalfOfUserId = Normalize(onBehalfOfUserId);

        Validate(Principal, PrincipalId, OnBehalfOfUserId);
    }

    public AuthPrincipal Principal { get; }

    /// <summary>
    /// Stable user identity when the principal is user-scoped.
    /// </summary>
    public string? PrincipalId { get; }

    /// <summary>
    /// Opaque late-bound reference. This is never the raw secret itself.
    /// </summary>
    public string? CredentialRef { get; }

    /// <summary>
    /// Audit target for delegated sends.
    /// </summary>
    public string? OnBehalfOfUserId { get; }

    public bool UsesBotIdentity => Principal == AuthPrincipal.Bot;

    public static AuthContext Bot(string? credentialRef = null) =>
        new(AuthPrincipal.Bot, credentialRef: credentialRef);

    public static AuthContext User(string principalId, string? credentialRef = null) =>
        new(AuthPrincipal.User, principalId: principalId, credentialRef: credentialRef);

    public static AuthContext OnBehalfOfUser(
        string principalId,
        string onBehalfOfUserId,
        string? credentialRef = null) =>
        new(
            AuthPrincipal.OnBehalfOfUser,
            principalId: principalId,
            credentialRef: credentialRef,
            onBehalfOfUserId: onBehalfOfUserId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void Validate(
        AuthPrincipal principal,
        string? principalId,
        string? onBehalfOfUserId)
    {
        switch (principal)
        {
            case AuthPrincipal.Bot:
                if (principalId is not null)
                    throw new ArgumentException("Bot auth context cannot carry a principal id.", nameof(principalId));

                if (onBehalfOfUserId is not null)
                    throw new ArgumentException("Bot auth context cannot carry an on-behalf-of user id.", nameof(onBehalfOfUserId));

                break;
            case AuthPrincipal.User:
                if (principalId is null)
                    throw new ArgumentException("User auth context requires a principal id.", nameof(principalId));

                if (onBehalfOfUserId is not null)
                    throw new ArgumentException("User auth context cannot carry an on-behalf-of user id.", nameof(onBehalfOfUserId));

                break;
            case AuthPrincipal.OnBehalfOfUser:
                if (principalId is null)
                    throw new ArgumentException("Delegated auth context requires a principal id.", nameof(principalId));

                if (onBehalfOfUserId is null)
                    throw new ArgumentException("Delegated auth context requires an on-behalf-of user id.", nameof(onBehalfOfUserId));

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(principal), principal, "Unsupported auth principal.");
        }
    }
}
