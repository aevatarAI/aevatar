using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationRuntimeQueryPort : IChannelBotRegistrationRuntimeQueryPort
{
    private readonly IProjectionDocumentReader<ChannelBotRegistrationDocument, string> _documentReader;
    private readonly IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string> _legacyDirectBindingReader;

    public ChannelBotRegistrationRuntimeQueryPort(
        IProjectionDocumentReader<ChannelBotRegistrationDocument, string> documentReader,
        IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string> legacyDirectBindingReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _legacyDirectBindingReader = legacyDirectBindingReader ?? throw new ArgumentNullException(nameof(legacyDirectBindingReader));
    }

    public async Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        if (document is null)
            return null;

        var legacyDirectBinding = await _legacyDirectBindingReader.GetAsync(registrationId, ct);
        return new ChannelBotRegistrationEntry
        {
            Id = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            WebhookUrl = document.WebhookUrl ?? string.Empty,
            NyxChannelBotId = document.NyxChannelBotId ?? string.Empty,
            NyxAgentApiKeyId = document.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationRouteId = document.NyxConversationRouteId ?? string.Empty,
            LegacyDirectBinding = legacyDirectBinding is null
                ? null
                : new ChannelBotLegacyDirectBinding
                {
                    NyxUserToken = legacyDirectBinding.NyxUserToken ?? string.Empty,
                    NyxRefreshToken = legacyDirectBinding.NyxRefreshToken ?? string.Empty,
                    VerificationToken = legacyDirectBinding.VerificationToken ?? string.Empty,
                    CredentialRef = legacyDirectBinding.CredentialRef ?? string.Empty,
                    EncryptKey = legacyDirectBinding.EncryptKey ?? string.Empty,
                },
        };
    }
}
