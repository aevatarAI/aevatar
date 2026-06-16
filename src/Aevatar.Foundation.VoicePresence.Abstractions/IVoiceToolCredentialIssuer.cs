namespace Aevatar.Foundation.VoicePresence.Abstractions;

public interface IVoiceToolCredentialIssuer
{
    Task<VoiceToolCredentialIssueResult?> IssueAsync(
        VoiceToolCredentialIssueRequest request,
        CancellationToken ct = default);

    Task ReleaseAsync(
        string credentialRef,
        CancellationToken ct = default);
}

public sealed record VoiceToolCredentialIssueRequest(
    string NyxIdAccessToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record VoiceToolCredentialIssueResult(
    string CredentialRef,
    DateTimeOffset ExpiresAtUtc);
