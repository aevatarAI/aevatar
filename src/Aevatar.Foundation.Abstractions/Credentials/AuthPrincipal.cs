namespace Aevatar.Foundation.Abstractions.Credentials;

/// <summary>
/// Identifies which principal should be used when resolving an auth context.
/// The raw secret is still obtained later through <see cref="ICredentialProvider"/>.
/// </summary>
public enum AuthPrincipal
{
    Bot = 0,
    User = 1,
    OnBehalfOfUser = 2,
}
