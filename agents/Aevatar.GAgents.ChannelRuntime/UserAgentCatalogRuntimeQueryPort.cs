using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogRuntimeQueryPort : IUserAgentCatalogRuntimeQueryPort
{
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;
    private readonly IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> _credentialReader;

    public UserAgentCatalogRuntimeQueryPort(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> credentialReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
    }

    public async Task<UserAgentCatalogEntry?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        if (document == null || document.Tombstoned)
            return null;

        var credential = await _credentialReader.GetAsync(agentId, ct);
        return UserAgentCatalogQueryPort.ToEntry(document, credential?.NyxApiKey ?? string.Empty);
    }

    public async Task<long?> GetStateVersionAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        return document?.StateVersion;
    }

    public async Task<IReadOnlyList<UserAgentCatalogEntry>> QueryAllAsync(CancellationToken ct = default)
    {
        var documents = await _documentReader.QueryAsync(new ProjectionDocumentQuery { Take = 1000 }, ct);
        var credentials = await _credentialReader.QueryAsync(new ProjectionDocumentQuery { Take = 1000 }, ct);
        var credentialById = credentials.Items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(static item => item.Id, static item => item.NyxApiKey ?? string.Empty, StringComparer.Ordinal);

        return documents.Items
            .Where(static item => !item.Tombstoned)
            .Select(item => UserAgentCatalogQueryPort.ToEntry(
                item,
                credentialById.TryGetValue(item.Id, out var nyxApiKey) ? nyxApiKey : string.Empty))
            .ToArray();
    }
}
