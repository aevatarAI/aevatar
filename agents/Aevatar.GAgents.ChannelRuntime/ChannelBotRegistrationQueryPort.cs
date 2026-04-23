using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationQueryPort : IChannelBotRegistrationQueryPort
    , IChannelBotRegistrationQueryByNyxIdentityPort
{
    private readonly IProjectionDocumentReader<ChannelBotRegistrationDocument, string> _documentReader;

    public ChannelBotRegistrationQueryPort(
        IProjectionDocumentReader<ChannelBotRegistrationDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        return document == null ? null : ToEntry(document);
    }

    public async Task<long?> GetStateVersionAsync(string registrationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        var document = await _documentReader.GetAsync(registrationId, ct);
        return document?.StateVersion;
    }

    public async Task<IReadOnlyList<ChannelBotRegistrationEntry>> QueryAllAsync(CancellationToken ct = default)
    {
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery { Take = 1000 },
            ct);

        return result.Items
            .Select(static doc => ToEntry(doc))
            .ToArray();
    }

    public Task<ChannelBotRegistrationEntry?> GetByNyxAgentApiKeyIdAsync(
        string nyxAgentApiKeyId,
        CancellationToken ct = default) =>
        QuerySingleByFieldAsync(
            nameof(ChannelBotRegistrationDocument.NyxAgentApiKeyId),
            nyxAgentApiKeyId,
            static entry => entry.NyxAgentApiKeyId,
            ct);

    public Task<ChannelBotRegistrationEntry?> GetByNyxChannelBotIdAsync(
        string nyxChannelBotId,
        CancellationToken ct = default) =>
        QuerySingleByFieldAsync(
            nameof(ChannelBotRegistrationDocument.NyxChannelBotId),
            nyxChannelBotId,
            static entry => entry.NyxChannelBotId,
            ct);

    private async Task<ChannelBotRegistrationEntry?> QuerySingleByFieldAsync(
        string fieldPath,
        string fieldValue,
        Func<ChannelBotRegistrationEntry, string?> entrySelector,
        CancellationToken ct)
    {
        var normalizedFieldValue = NormalizeOptional(fieldValue);
        if (normalizedFieldValue is null)
            return null;

        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = fieldPath,
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(normalizedFieldValue),
                    },
                ],
            },
            ct);

        var document = result.Items.FirstOrDefault();
        if (document is not null)
            return ToEntry(document);

        // Some projection providers have returned empty exact-match results even while the
        // document is visible via QueryAll(). Fall back to a bounded scan so relay traffic
        // still resolves registrations by Nyx identity instead of silently failing.
        var entries = await QueryAllAsync(ct);
        return entries.FirstOrDefault(entry =>
            string.Equals(NormalizeOptional(entrySelector(entry)), normalizedFieldValue, StringComparison.Ordinal));
    }

    private static ChannelBotRegistrationEntry ToEntry(ChannelBotRegistrationDocument document) =>
        new()
        {
            Id = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            WebhookUrl = document.WebhookUrl ?? string.Empty,
            NyxChannelBotId = document.NyxChannelBotId ?? string.Empty,
            NyxAgentApiKeyId = document.NyxAgentApiKeyId ?? string.Empty,
            NyxConversationRouteId = document.NyxConversationRouteId ?? string.Empty,
            CredentialRef = document.CredentialRef ?? string.Empty,
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
