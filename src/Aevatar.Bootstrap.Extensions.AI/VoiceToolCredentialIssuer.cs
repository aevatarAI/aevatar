using Aevatar.Configuration;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Bootstrap.Extensions.AI;

public sealed class VoiceToolCredentialIssuer : IVoiceToolCredentialIssuer
{
    private const string Prefix = "voice-tool:";
    private readonly IAevatarSecretsStore _secretsStore;

    public VoiceToolCredentialIssuer(IAevatarSecretsStore secretsStore)
    {
        _secretsStore = secretsStore ?? throw new ArgumentNullException(nameof(secretsStore));
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
        if (expiresAtUtc <= DateTimeOffset.UtcNow)
            return Task.FromResult<VoiceToolCredentialIssueResult?>(null);

        var credentialRef = Prefix + Guid.NewGuid().ToString("N");
        _secretsStore.Set(credentialRef, token);
        return Task.FromResult<VoiceToolCredentialIssueResult?>(
            new VoiceToolCredentialIssueResult(credentialRef, expiresAtUtc));
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
