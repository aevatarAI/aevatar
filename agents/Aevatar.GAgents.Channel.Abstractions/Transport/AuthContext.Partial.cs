namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for the transport-facing auth context contract.
/// </summary>
public sealed partial class AuthContext
{
    /// <summary>
    /// Creates one bot-scoped auth context.
    /// </summary>
    public static AuthContext Bot() => new()
    {
        Kind = PrincipalKind.Bot,
    };

    /// <summary>
    /// Creates one delegated user auth context (legacy per-bot credential
    /// reference). Broker-mode callers should use
    /// <see cref="OnBehalfOfExternalSubject"/> instead — see ADR-0018
    /// §Outbound Send.
    /// </summary>
    public static AuthContext OnBehalfOfUser(string userCredentialRef, string onBehalfOfUserId) => new()
    {
        Kind = PrincipalKind.OnBehalfOfUser,
        UserCredentialRef = NormalizeRequired(userCredentialRef, nameof(userCredentialRef)),
        OnBehalfOfUserId = NormalizeRequired(onBehalfOfUserId, nameof(onBehalfOfUserId)),
    };

    /// <summary>
    /// Creates one delegated user auth context for broker-mode outbound. The
    /// outbound adapter resolves <paramref name="externalSubject"/> via
    /// <c>IExternalIdentityBindingQueryPort</c> and mints a short-lived access
    /// token via <c>INyxIdCapabilityBroker.IssueShortLivedAsync</c> per send.
    /// <paramref name="onBehalfOfUserId"/> is the channel-native identifier
    /// the platform expects for delegation (e.g. Lark open_user_id).
    /// </summary>
    public static AuthContext OnBehalfOfExternalSubject(
        ExternalSubjectRef externalSubject,
        string onBehalfOfUserId) => new()
    {
        Kind = PrincipalKind.OnBehalfOfUser,
        ExternalSubject = externalSubject?.Clone()
            ?? throw new ArgumentNullException(nameof(externalSubject)),
        OnBehalfOfUserId = NormalizeRequired(onBehalfOfUserId, nameof(onBehalfOfUserId)),
    };

    /// <summary>
    /// Returns <see langword="true"/> when this context uses the adapter's bot identity.
    /// </summary>
    public bool UsesBotIdentity => Kind == PrincipalKind.Bot;

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", paramName);

        return value.Trim();
    }
}
