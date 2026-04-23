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
        QuerySingleByFieldAsync(nameof(ChannelBotRegistrationDocument.NyxAgentApiKeyId), nyxAgentApiKeyId, ct);

    public Task<ChannelBotRegistrationEntry?> GetByNyxChannelBotIdAsync(
        string nyxChannelBotId,
        CancellationToken ct = default) =>
        QuerySingleByFieldAsync(nameof(ChannelBotRegistrationDocument.NyxChannelBotId), nyxChannelBotId, ct);

    private async Task<ChannelBotRegistrationEntry?> QuerySingleByFieldAsync(
        string fieldPath,
        string fieldValue,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fieldValue))
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
                        Value = ProjectionDocumentValue.FromString(fieldValue),
                    },
                ],
            },
            ct);

        var document = result.Items.FirstOrDefault();
        return document == null ? null : ToEntry(document);
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
            NyxAgentApiKeyHash = document.NyxAgentApiKeyHash ?? string.Empty,
        };
}
