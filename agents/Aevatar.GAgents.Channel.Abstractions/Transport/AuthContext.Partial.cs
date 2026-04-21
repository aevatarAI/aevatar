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
    /// Creates one delegated user auth context.
    /// </summary>
    public static AuthContext OnBehalfOfUser(string userCredentialRef, string onBehalfOfUserId) => new()
    {
        Kind = PrincipalKind.OnBehalfOfUser,
        UserCredentialRef = NormalizeRequired(userCredentialRef, nameof(userCredentialRef)),
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
