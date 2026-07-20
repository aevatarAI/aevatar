using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Foundation.Abstractions.Credentials.Testing;

public interface IRuntimeSecretClock
{
    long UnixTimeMilliseconds { get; }
}

public sealed class SystemRuntimeSecretClock : IRuntimeSecretClock
{
    public long UnixTimeMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class ManualRuntimeSecretClock(long unixTimeMilliseconds) : IRuntimeSecretClock
{
    public long UnixTimeMilliseconds { get; private set; } = unixTimeMilliseconds;

    public void Advance(TimeSpan duration)
    {
        UnixTimeMilliseconds += (long)duration.TotalMilliseconds;
    }
}

/// <summary>
/// Development/test-only in-memory runtime secret store. It is not distributed and not production safe.
/// </summary>
public sealed class InMemoryRuntimeSecretStore : IRuntimeSecretStore
{
    private readonly object _gate = new();
    private readonly IRuntimeSecretClock _clock;
    private readonly Dictionary<string, StoredRuntimeSecret> _secrets = new(StringComparer.Ordinal);
    private long _nextId;

    public InMemoryRuntimeSecretStore()
        : this(new SystemRuntimeSecretClock())
    {
    }

    public InMemoryRuntimeSecretStore(IRuntimeSecretClock clock)
    {
        _clock = clock;
    }

    public Task<StoreRuntimeSecretResult> PutAsync(StoreRuntimeSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var reference = new RuntimeSecretReference
        {
            Ref = NewReference(),
            Purpose = request.Purpose,
            Fingerprint = Fingerprint(request.Secret),
            ExpiresAtUnixMs = _clock.UnixTimeMilliseconds + (long)request.TimeToLive.TotalMilliseconds,
            ConsumeOnce = request.ConsumeOnce,
            OwnerRunId = request.OwnerRunId,
            OwnerStepId = request.OwnerStepId,
        };

        lock (_gate)
        {
            _secrets[reference.Ref] = new StoredRuntimeSecret(
                reference,
                request.Secret,
                Consumed: false,
                Revoked: false);
        }

        return Task.FromResult(new StoreRuntimeSecretResult(reference.Clone()));
    }

    public Task<ResolveRuntimeSecretResult> ResolveAsync(
        ResolveRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerRunId, request.OwnerStepId, out var storedSecret) ||
                !storedSecret.IsActive(_clock.UnixTimeMilliseconds))
            {
                return Task.FromResult(new ResolveRuntimeSecretResult(null, null));
            }

            return Task.FromResult(new ResolveRuntimeSecretResult(storedSecret.Reference.Clone(), storedSecret.Secret));
        }
    }

    public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
        ConsumeRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerRunId, request.OwnerStepId, out var storedSecret) ||
                !storedSecret.IsActive(_clock.UnixTimeMilliseconds))
            {
                return Task.FromResult(new ConsumeRuntimeSecretResult(false));
            }

            _secrets[request.Ref] = storedSecret with { Consumed = storedSecret.Reference.ConsumeOnce };
            return Task.FromResult(new ConsumeRuntimeSecretResult(true));
        }
    }

    public Task<RevokeRuntimeSecretResult> RevokeAsync(
        RevokeRuntimeSecretRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerRunId, request.OwnerStepId, out var storedSecret) ||
                storedSecret.Revoked)
            {
                return Task.FromResult(new RevokeRuntimeSecretResult(false));
            }

            _secrets[request.Ref] = storedSecret with { Revoked = true };
            return Task.FromResult(new RevokeRuntimeSecretResult(true));
        }
    }

    private bool TryGetAuthorized(
        string reference,
        string purpose,
        string ownerRunId,
        string ownerStepId,
        out StoredRuntimeSecret storedSecret)
    {
        if (!_secrets.TryGetValue(reference, out storedSecret!))
        {
            return false;
        }

        return string.Equals(storedSecret.Reference.Purpose, purpose, StringComparison.Ordinal) &&
               string.Equals(storedSecret.Reference.OwnerRunId, ownerRunId, StringComparison.Ordinal) &&
               string.Equals(storedSecret.Reference.OwnerStepId, ownerStepId, StringComparison.Ordinal);
    }

    private string NewReference()
    {
        var id = Interlocked.Increment(ref _nextId);
        return $"rsec_{id:0000000000000000}";
    }

    private static string Fingerprint(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record StoredRuntimeSecret(
        RuntimeSecretReference Reference,
        string Secret,
        bool Consumed,
        bool Revoked)
    {
        public bool IsActive(long nowUnixTimeMilliseconds) =>
            !Consumed && !Revoked && Reference.ExpiresAtUnixMs > nowUnixTimeMilliseconds;
    }
}
