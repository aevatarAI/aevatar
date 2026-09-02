using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed class InMemoryManagedCodexCredentialMutationLease :
    IManagedCodexCredentialMutationLease
{
    private readonly ConcurrentDictionary<string, string> _owners =
        new(StringComparer.Ordinal);

    public ValueTask<IManagedCodexCredentialMutationLeaseHandle?> TryAcquireAsync(
        string ownerKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ct.ThrowIfCancellationRequested();
        var key = ownerKey.Trim();
        var ownerToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        IManagedCodexCredentialMutationLeaseHandle? handle =
            _owners.TryAdd(key, ownerToken)
                ? new Handle(_owners, key, ownerToken)
                : null;
        return ValueTask.FromResult(handle);
    }

    private sealed class Handle(
        ConcurrentDictionary<string, string> owners,
        string key,
        string ownerToken) : IManagedCodexCredentialMutationLeaseHandle
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = ((ICollection<KeyValuePair<string, string>>)owners)
                    .Remove(new KeyValuePair<string, string>(key, ownerToken));
            }
            return ValueTask.CompletedTask;
        }
    }
}
