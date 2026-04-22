namespace Aevatar.Foundation.Abstractions.Credentials;

/// <summary>
/// Resolves a late-bound credential reference to a raw secret string at the
/// provider boundary. Contract per Channel RFC §17.3: persisted state and
/// command envelopes carry a <c>credential_ref</c> opaque to upstream layers;
/// only the provider edge (e.g. <c>IAevatarSecretsStore</c>-backed
/// implementation) materializes the raw secret when it is about to be used.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>
    /// Resolves <paramref name="credentialRef"/> to the raw secret. Returns
    /// <c>null</c> when the reference is not known to this provider; throws
    /// when lookup itself fails.
    /// </summary>
    Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default);
}
