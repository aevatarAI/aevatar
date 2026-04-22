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

        var legacyDirectBindingDocument = await _legacyDirectBindingReader.GetAsync(registrationId, ct);
        var entry = new ChannelBotRegistrationEntry
        {
            Id = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            WebhookUrl = document.WebhookUrl ?? string.Empty,
            NyxChannelBotId = document.NyxChannelBotId ?? string.Empty,
            NyxAgentApiKeyId = document.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationRouteId = document.NyxConversationRouteId ?? string.Empty,
        };

        entry.ApplyLegacyDirectBinding(legacyDirectBindingDocument is null
            ? document.ResolveLegacyDirectBinding()
            : new ChannelBotLegacyDirectBinding
            {
                NyxUserToken = legacyDirectBindingDocument.NyxUserToken ?? string.Empty,
                NyxRefreshToken = legacyDirectBindingDocument.NyxRefreshToken ?? string.Empty,
                VerificationToken = legacyDirectBindingDocument.VerificationToken ?? string.Empty,
                CredentialRef = legacyDirectBindingDocument.CredentialRef ?? string.Empty,
                EncryptKey = legacyDirectBindingDocument.EncryptKey ?? string.Empty,
            });

        return entry;
    }
}
