using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogQueryPort : IUserAgentCatalogQueryPort
{
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;

    public UserAgentCatalogQueryPort(IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<UserAgentCatalogEntry?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        return document == null || document.Tombstoned ? null : ToEntry(document, string.Empty);
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
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery { Take = 1000 }, ct);
        return result.Items
            .Where(static x => !x.Tombstoned)
            .Select(static x => ToEntry(x, string.Empty))
            .ToArray();
    }

    internal static UserAgentCatalogEntry ToEntry(UserAgentCatalogDocument document, string nyxApiKey) =>
        new()
        {
            AgentId = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            ConversationId = document.ConversationId ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            NyxApiKey = nyxApiKey ?? string.Empty,
            OwnerNyxUserId = document.OwnerNyxUserId ?? string.Empty,
            AgentType = document.AgentType ?? string.Empty,
            TemplateName = document.TemplateName ?? string.Empty,
            ScopeId = document.ScopeId ?? string.Empty,
            ApiKeyId = document.ApiKeyId ?? string.Empty,
            ScheduleCron = document.ScheduleCron ?? string.Empty,
            ScheduleTimezone = document.ScheduleTimezone ?? string.Empty,
            Status = document.Status ?? string.Empty,
            LastRunAt = document.LastRunAtUtc,
            NextRunAt = document.NextRunAtUtc,
            ErrorCount = document.ErrorCount,
            LastError = document.LastError ?? string.Empty,
            CreatedAt = document.CreatedAtUtc,
            UpdatedAt = document.UpdatedAtUtc,
            Tombstoned = document.Tombstoned,
        };
}
