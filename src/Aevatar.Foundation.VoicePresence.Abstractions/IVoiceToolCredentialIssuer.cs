namespace Aevatar.Foundation.VoicePresence.Abstractions;

public interface IVoiceToolCredentialIssuer
{
    Task<VoiceToolCredentialIssueResult?> IssueAsync(
        VoiceToolCredentialIssueRequest request,
        CancellationToken ct = default);
}

public sealed record VoiceToolCredentialIssueRequest(
    string NyxIdAccessToken,
    DateTimeOffset ExpiresAtUtc,
    string? CallerScopeId = null,
    string? CallerSubject = null,
    string? OwnerSubject = null,
    string? ChannelPlatform = null,
    string? ChannelSenderId = null,
    string? ChannelRegistrationScopeId = null,
    string? ChannelMessageId = null,
    string? ChannelPlatformMessageId = null,
    string? ChannelDeliveryTargetId = null,
    string? ConnectedServicesContextJson = null,
    IReadOnlyCollection<string>? AllowedToolNames = null,
    string? NyxIdRoutePreference = null,
    string? SenderBindingId = null);

public sealed record VoiceToolCredentialIssueResult(
    string CredentialRef,
    DateTimeOffset ExpiresAtUtc);
