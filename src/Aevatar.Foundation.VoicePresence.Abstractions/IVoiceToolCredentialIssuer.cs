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

public interface IVoiceVolatileToolCredentialPort : IVoiceToolCredentialIssuer
{
    Task<bool> BindTransportLeaseAsync(
        VoiceToolCredentialTransportBinding credentialBinding,
        string transportLeaseId,
        CancellationToken ct = default);

    Task ReleaseTransportLeaseAsync(
        string transportLeaseId,
        CancellationToken ct = default);
}

public sealed record VoiceToolCredentialIssueRequest(
    string NyxIdAccessToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record VoiceToolCredentialIssueResult(
    string CredentialRef,
    DateTimeOffset ExpiresAtUtc,
    VoiceToolCredentialTransportBinding? TransportBinding = null);

public sealed record VoiceToolCredentialTransportBinding(
    string CredentialRef,
    string NyxIdAccessToken,
    DateTimeOffset ExpiresAtUtc);

public sealed class VoiceVolatileToolCredentialUnavailableException()
    : Exception(Reason)
{
    public const string Reason = "voice_credential_unavailable";
}
