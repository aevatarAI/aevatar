using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogQueryPort : IUserAgentCatalogQueryPort
{
    private readonly IProjectionDocumentReader<AgentRegistryDocument, string> _documentReader;

    public UserAgentCatalogQueryPort(IProjectionDocumentReader<AgentRegistryDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<AgentRegistryEntry?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        return document == null || document.Tombstoned ? null : ToEntry(document);
    }

    public async Task<long?> GetStateVersionAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        return document?.StateVersion;
    }

    public async Task<IReadOnlyList<AgentRegistryEntry>> QueryAllAsync(CancellationToken ct = default)
    {
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery { Take = 1000 }, ct);
        return result.Items
            .Where(static x => !x.Tombstoned)
            .Select(static x => ToEntry(x))
            .ToArray();
    }

    private static AgentRegistryEntry ToEntry(AgentRegistryDocument document) =>
        new()
        {
            AgentId = document.Id ?? string.Empty,
            Platform = document.Platform ?? string.Empty,
            ConversationId = document.ConversationId ?? string.Empty,
            NyxProviderSlug = document.NyxProviderSlug ?? string.Empty,
            NyxApiKey = document.NyxApiKey ?? string.Empty,
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
