using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceVolatileToolCredentialPort : IVoiceVolatileToolCredentialPort, ICredentialProvider
{
    private const string Prefix = "voice-tool:";
    private readonly ConcurrentDictionary<string, BoundCredential> _transportCredentials = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public VoiceVolatileToolCredentialPort(TimeProvider? timeProvider = null)
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
        return Task.FromResult<VoiceToolCredentialIssueResult?>(
            new VoiceToolCredentialIssueResult(
                credentialRef,
                expiresAtUtc,
                new VoiceToolCredentialTransportBinding(credentialRef, token, expiresAtUtc)));
    }

    public Task<bool> BindTransportLeaseAsync(
        VoiceToolCredentialTransportBinding credentialBinding,
        string transportLeaseId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credentialBinding);

        var normalizedRef = NormalizeCredentialRef(credentialBinding.CredentialRef);
        var token = Normalize(credentialBinding.NyxIdAccessToken);
        var normalizedTransportLeaseId = Normalize(transportLeaseId);
        if (normalizedRef is null || token is null || normalizedTransportLeaseId is null)
            return Task.FromResult(false);

        ReleaseExpiredLeases();

        var expiresAtUtc = credentialBinding.ExpiresAtUtc.ToUniversalTime();
        if (expiresAtUtc <= UtcNow)
            return Task.FromResult(false);

        _transportCredentials[normalizedTransportLeaseId] = new BoundCredential(
            normalizedRef,
            token,
            expiresAtUtc);
        return Task.FromResult(true);
    }

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = NormalizeCredentialRef(credentialRef);
        if (normalized is null)
            return Task.FromResult<string?>(null);

        ReleaseExpiredLeases();

        foreach (var pair in _transportCredentials)
        {
            if (!string.Equals(pair.Value.CredentialRef, normalized, StringComparison.Ordinal))
                continue;

            if (pair.Value.ExpiresAtUtc <= UtcNow)
            {
                _transportCredentials.TryRemove(pair.Key, out _);
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(pair.Value.NyxIdAccessToken);
        }

        return Task.FromResult<string?>(null);
    }

    public Task ReleaseAsync(string credentialRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = NormalizeCredentialRef(credentialRef);
        if (normalized is null)
            return Task.CompletedTask;

        foreach (var pair in _transportCredentials)
        {
            if (string.Equals(pair.Value.CredentialRef, normalized, StringComparison.Ordinal))
                _transportCredentials.TryRemove(pair.Key, out _);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseTransportLeaseAsync(string transportLeaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedTransportLeaseId = Normalize(transportLeaseId);
        if (normalizedTransportLeaseId is not null)
            _transportCredentials.TryRemove(normalizedTransportLeaseId, out _);

        return Task.CompletedTask;
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow().ToUniversalTime();

    private void ReleaseExpiredLeases()
    {
        var utcNow = UtcNow;
        foreach (var pair in _transportCredentials)
        {
            if (pair.Value.ExpiresAtUtc <= utcNow)
                _transportCredentials.TryRemove(pair.Key, out _);
        }
    }

    private static string? NormalizeCredentialRef(string? value)
    {
        var normalized = Normalize(value);
        return normalized is not null && normalized.StartsWith(Prefix, StringComparison.Ordinal)
            ? normalized
            : null;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record BoundCredential(
        string CredentialRef,
        string NyxIdAccessToken,
        DateTimeOffset ExpiresAtUtc);
}
