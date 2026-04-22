using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationRuntimeQueryPort : IChannelBotRegistrationRuntimeQueryPort
{
    private readonly IProjectionDocumentReader<ChannelBotRegistrationDocument, string> _documentReader;
    private readonly IProjectionDocumentReader<ChannelBotDirectCallbackBindingDocument, string> _directCallbackBindingReader;

    public ChannelBotRegistrationRuntimeQueryPort(
        IProjectionDocumentReader<ChannelBotRegistrationDocument, string> documentReader,
        IProjectionDocumentReader<ChannelBotDirectCallbackBindingDocument, string> directCallbackBindingReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _directCallbackBindingReader = directCallbackBindingReader ?? throw new ArgumentNullException(nameof(directCallbackBindingReader));
    }

    public async Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        if (document is null)
            return null;

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

        if (string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            return entry;

        var directCallbackBindingDocument = await _directCallbackBindingReader.GetAsync(registrationId, ct);
        if (directCallbackBindingDocument is null)
            return entry;

        entry.ApplyDirectCallbackBinding(new ChannelBotDirectCallbackBinding
        {
            NyxUserToken = directCallbackBindingDocument.NyxUserToken ?? string.Empty,
            NyxRefreshToken = directCallbackBindingDocument.NyxRefreshToken ?? string.Empty,
            VerificationToken = directCallbackBindingDocument.VerificationToken ?? string.Empty,
            CredentialRef = directCallbackBindingDocument.CredentialRef ?? string.Empty,
            EncryptKey = directCallbackBindingDocument.EncryptKey ?? string.Empty,
        });

        return entry;
    }
}
