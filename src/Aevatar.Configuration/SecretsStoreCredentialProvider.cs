using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.Configuration;

/// <summary>
/// Default credential provider backed by <see cref="IAevatarSecretsStore"/>.
/// </summary>
public sealed class SecretsStoreCredentialProvider : ICredentialProvider
{
    private readonly IAevatarSecretsStore _secretsStore;

    public SecretsStoreCredentialProvider(IAevatarSecretsStore secretsStore)
    {
        _secretsStore = secretsStore ?? throw new ArgumentNullException(nameof(secretsStore));
    }

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credentialRef))
            return Task.FromResult<string?>(null);

        return Task.FromResult(_secretsStore.Get(credentialRef.Trim()));
    }
}
