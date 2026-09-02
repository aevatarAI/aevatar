using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Foundation.Abstractions.Credentials.Testing;

/// <summary>
/// Development/test-only in-memory vault. It is not a production secret authority.
/// </summary>
public sealed class InMemorySecretVault : ISecretVault
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, StoredSecret> _secrets = new(StringComparer.Ordinal);
    private long _nextId;

    public InMemorySecretVault()
        : this(TimeProvider.System)
    {
    }

    public InMemorySecretVault(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestedRef is not null && string.IsNullOrWhiteSpace(request.RequestedRef))
            throw new ArgumentException("RequestedRef must be null or non-empty.", nameof(request));
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var requestedRef = request.RequestedRef?.Trim();
        var reference = new SecretReference
        {
            Ref = string.IsNullOrEmpty(requestedRef) ? NewReference() : requestedRef,
            Purpose = request.Purpose,
            Fingerprint = Fingerprint(request.Secret),
            Version = 1,
            OwnerScopeKey = request.OwnerScopeKey,
            CreatedAtUnixMs = now,
            ExpiresAtUnixMs = request.ExpiresAt?.ToUniversalTime().ToUnixTimeMilliseconds() ?? 0,
        };

        lock (_gate)
        {
            if (_secrets.TryGetValue(reference.Ref, out var existing))
            {
                if (SameCreateRequest(existing, reference, request.SubjectId, request.Secret))
                    return Task.FromResult(new StoreSecretResult(existing.Reference.Clone()));

                throw new InvalidOperationException("Secret reference already exists with a different descriptor or secret.");
            }

            _secrets[reference.Ref] = new StoredSecret(reference, request.SubjectId, request.Secret, Revoked: false);
        }

        return Task.FromResult(new StoreSecretResult(reference.Clone()));
    }

    public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_secrets.TryGetValue(request.Ref, out var storedSecret))
            {
                return Task.FromResult(new ResolveSecretResult(null, null, SecretResolutionFailureReason.NotFound));
            }

            if (storedSecret.Revoked)
            {
                return Task.FromResult(new ResolveSecretResult(null, null, SecretResolutionFailureReason.Revoked));
            }

            if (!IsAuthorized(storedSecret, request.Purpose, request.OwnerScopeKey, request.SubjectId))
            {
                return Task.FromResult(new ResolveSecretResult(null, null, SecretResolutionFailureReason.Unauthorized));
            }

            if (IsExpired(storedSecret.Reference))
            {
                return Task.FromResult(new ResolveSecretResult(null, null, SecretResolutionFailureReason.NotFound));
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
            if (!_secrets.TryGetValue(request.Ref, out var storedSecret))
                return Task.FromResult(new RevokeSecretResult(true));

            if (!IsAuthorized(storedSecret, request.Purpose, request.OwnerScopeKey, request.SubjectId))
            {
                return Task.FromResult(new RevokeSecretResult(false));
            }

            if (storedSecret.Revoked)
                return Task.FromResult(new RevokeSecretResult(true));

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

    private static bool IsAuthorized(
        StoredSecret storedSecret,
        string purpose,
        string ownerScopeKey,
        string subjectId) =>
        string.Equals(storedSecret.Reference.Purpose, purpose, StringComparison.Ordinal) &&
        string.Equals(storedSecret.Reference.OwnerScopeKey, ownerScopeKey, StringComparison.Ordinal) &&
               string.Equals(storedSecret.SubjectId, subjectId, StringComparison.Ordinal);

    private static bool SameCreateRequest(
        StoredSecret storedSecret,
        SecretReference reference,
        string subjectId,
        string secret) =>
        !storedSecret.Revoked &&
        string.Equals(storedSecret.Reference.Purpose, reference.Purpose, StringComparison.Ordinal) &&
        string.Equals(storedSecret.Reference.OwnerScopeKey, reference.OwnerScopeKey, StringComparison.Ordinal) &&
        string.Equals(storedSecret.SubjectId, subjectId, StringComparison.Ordinal) &&
        string.Equals(storedSecret.Secret, secret, StringComparison.Ordinal) &&
        storedSecret.Reference.ExpiresAtUnixMs == reference.ExpiresAtUnixMs;

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

    private bool IsExpired(SecretReference reference) =>
        reference.ExpiresAtUnixMs > 0 &&
        reference.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private sealed record StoredSecret(
        SecretReference Reference,
        string SubjectId,
        string Secret,
        bool Revoked);
}
