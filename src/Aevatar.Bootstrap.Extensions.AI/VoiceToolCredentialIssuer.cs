using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Bootstrap.Extensions.AI;

public sealed class VoiceToolCredentialIssuer : IVoiceToolCredentialIssuer, ICredentialProvider
{
    private const string Prefix = "voice-tool:";
    private readonly ConcurrentDictionary<string, CredentialLease> _credentials = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public VoiceToolCredentialIssuer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<VoiceToolCredentialIssueResult?> IssueAsync(
        VoiceToolCredentialIssueRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var token = Normalize(request.NyxIdAccessToken);
        if (token is null)
            return Task.FromResult<VoiceToolCredentialIssueResult?>(null);

        var expiresAtUtc = request.ExpiresAtUtc.ToUniversalTime();
        ReleaseExpiredLeases();

        if (expiresAtUtc <= UtcNow)
            return Task.FromResult<VoiceToolCredentialIssueResult?>(null);

        var credentialRef = Prefix + Guid.NewGuid().ToString("N");
        _credentials[credentialRef] = new CredentialLease(token, expiresAtUtc);
        return Task.FromResult<VoiceToolCredentialIssueResult?>(
            new VoiceToolCredentialIssueResult(credentialRef, expiresAtUtc));
    }

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = Normalize(credentialRef);
        if (normalized is null || !normalized.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult<string?>(null);

        ReleaseExpiredLeases();

        if (!_credentials.TryGetValue(normalized, out var lease))
            return Task.FromResult<string?>(null);

        if (lease.ExpiresAtUtc <= UtcNow)
        {
            _credentials.TryRemove(normalized, out _);
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(lease.NyxIdAccessToken);
    }

    public Task ReleaseAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = Normalize(credentialRef);
        if (normalized is not null)
            _credentials.TryRemove(normalized, out _);

        return Task.CompletedTask;
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow().ToUniversalTime();

    private void ReleaseExpiredLeases()
    {
        var utcNow = UtcNow;
        foreach (var pair in _credentials)
        {
            if (pair.Value.ExpiresAtUtc <= utcNow)
                _credentials.TryRemove(pair.Key, out _);
        }
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record CredentialLease(string NyxIdAccessToken, DateTimeOffset ExpiresAtUtc);
}
