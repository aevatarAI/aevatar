using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Channel-facing helpers over the shared credential-resolution contract introduced in foundation abstractions.
/// </summary>
public static class ChannelCredentialProviderExtensions
{
    /// <summary>
    /// Resolves the delegated user credential referenced by one auth context.
    /// </summary>
    public static Task<string?> ResolveUserCredentialAsync(
        this ICredentialProvider credentialProvider,
        AuthContext authContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(authContext);

        if (authContext.Kind != PrincipalKind.OnBehalfOfUser)
            throw new ArgumentException("Only delegated user auth contexts carry a user credential reference.", nameof(authContext));

        if (string.IsNullOrWhiteSpace(authContext.UserCredentialRef))
            throw new ArgumentException("Auth context must carry a user credential reference.", nameof(authContext));

        return credentialProvider.ResolveAsync(authContext.UserCredentialRef, ct);
    }
}
