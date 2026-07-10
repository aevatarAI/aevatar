using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Foundation.Abstractions.Credentials.Testing;

/// <summary>
/// Development/test-only in-memory vault. It is not a production secret authority.
/// </summary>
public sealed class InMemorySecretVault : ISecretVault
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredSecret> _secrets = new(StringComparer.Ordinal);
    private long _nextId;

    public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var reference = new SecretReference
        {
            Ref = NewReference(),
            Purpose = request.Purpose,
            Fingerprint = Fingerprint(request.Secret),
            Version = 1,
            OwnerScopeKey = request.OwnerScopeKey,
            CreatedAtUnixMs = now,
            ExpiresAtUnixMs = request.ExpiresAt?.ToUniversalTime().ToUnixTimeMilliseconds() ?? 0,
        };

        lock (_gate)
        {
            _secrets[reference.Ref] = new StoredSecret(reference, request.SubjectId, request.Secret, Revoked: false);
        }

        return Task.FromResult(new StoreSecretResult(reference.Clone()));
    }

    public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerScopeKey, request.SubjectId, out var storedSecret) ||
                storedSecret.Revoked ||
                IsExpired(storedSecret.Reference))
            {
                return Task.FromResult(new ResolveSecretResult(null, null));
            }

            return Task.FromResult(new ResolveSecretResult(storedSecret.Reference.Clone(), storedSecret.Secret));
        }
    }

    public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerScopeKey, request.SubjectId, out var storedSecret) ||
                storedSecret.Revoked)
            {
                throw new InvalidOperationException("Secret reference is not active for the requested owner and purpose.");
            }

            var reference = storedSecret.Reference.Clone();
            reference.Version++;
            reference.Fingerprint = Fingerprint(request.Secret);
            _secrets[request.Ref] = storedSecret with
            {
                Reference = reference,
                Secret = request.Secret,
            };

            return Task.FromResult(new RotateSecretResult(reference.Clone()));
        }
    }

    public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!TryGetAuthorized(request.Ref, request.Purpose, request.OwnerScopeKey, request.SubjectId, out var storedSecret) ||
                storedSecret.Revoked)
            {
                return Task.FromResult(new RevokeSecretResult(false));
            }

            _secrets[request.Ref] = storedSecret with { Revoked = true };
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }

    private bool TryGetAuthorized(
        string reference,
        string purpose,
        string ownerScopeKey,
        string subjectId,
        out StoredSecret storedSecret)
    {
        if (!_secrets.TryGetValue(reference, out storedSecret!))
        {
            return false;
        }

        return string.Equals(storedSecret.Reference.Purpose, purpose, StringComparison.Ordinal) &&
               string.Equals(storedSecret.Reference.OwnerScopeKey, ownerScopeKey, StringComparison.Ordinal) &&
               string.Equals(storedSecret.SubjectId, subjectId, StringComparison.Ordinal);
    }

    private string NewReference()
    {
        var id = Interlocked.Increment(ref _nextId);
        return $"sec_{id:0000000000000000}";
    }

    private static string Fingerprint(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsExpired(SecretReference reference) =>
        reference.ExpiresAtUnixMs > 0 &&
        reference.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed record StoredSecret(
        SecretReference Reference,
        string SubjectId,
        string Secret,
        bool Revoked);
}
