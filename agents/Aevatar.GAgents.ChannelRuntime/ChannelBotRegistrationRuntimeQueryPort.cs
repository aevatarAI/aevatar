using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationRuntimeQueryPort : IChannelBotRegistrationRuntimeQueryPort
{
    private readonly IProjectionDocumentReader<ChannelBotRegistrationDocument, string> _documentReader;

    public ChannelBotRegistrationRuntimeQueryPort(
        IProjectionDocumentReader<ChannelBotRegistrationDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        if (document is null)
            return null;

        return new ChannelBotRegistrationEntry
        {
            Id = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            NyxUserToken = document.NyxUserToken ?? string.Empty,
            VerificationToken = document.VerificationToken ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            WebhookUrl = document.WebhookUrl ?? string.Empty,
            EncryptKey = document.EncryptKey ?? string.Empty,
            CredentialRef = document.CredentialRef ?? string.Empty,
        };
    }
}
