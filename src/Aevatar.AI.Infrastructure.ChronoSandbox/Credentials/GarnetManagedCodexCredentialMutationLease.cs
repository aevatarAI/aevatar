using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed class GarnetManagedCodexCredentialMutationLease(
    IGarnetSecretKeyValueStore store,
    IOptions<ManagedCodexOptions> options,
    ILogger<GarnetManagedCodexCredentialMutationLease> logger) : IManagedCodexCredentialMutationLease
{
    private const string KeyPrefix = "aevatar:managed-codex:credential-mutation:";
    private readonly IGarnetSecretKeyValueStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(
        (options ?? throw new ArgumentNullException(nameof(options))).Value.MutationLeaseSeconds);
    private readonly ILogger<GarnetManagedCodexCredentialMutationLease> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<IManagedCodexCredentialMutationLeaseHandle?> TryAcquireAsync(
        string ownerKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        var key = BuildKey(ownerKey.Trim());
        var ownerToken = RandomNumberGenerator.GetBytes(32);
        var acquired = await _store.SetIfAbsentAsync(key, ownerToken, _ttl, ct)
            .ConfigureAwait(false);
        return acquired ? new Handle(_store, key, ownerToken, _logger) : null;
    }

    private static string BuildKey(string ownerKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey));
        return KeyPrefix + Convert.ToHexStringLower(digest);
    }

    private sealed class Handle(
        IGarnetSecretKeyValueStore store,
        string key,
        byte[] ownerToken,
        ILogger logger) : IManagedCodexCredentialMutationLeaseHandle
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _ = await store.CompareDeleteAsync(key, ownerToken, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Managed Codex mutation lease release failed for {LeaseKey}; its TTL remains the recovery boundary.",
                    key);
            }
        }
    }
}
