namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public interface IAgentProfileCatalogApplicationService
{
    Task<AgentProfileListPage> ListAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    Task<AgentProfileListPage> ListPublishedAsync(
        AgentProfileOwner owner,
        string? cursor,
        int pageSize,
        CancellationToken ct = default);
}

public sealed record AgentProfileListPage(
    IReadOnlyList<AgentProfileCatalogEntry> Items,
    string? NextCursor,
    long AuthorityStateVersion,
    DateTimeOffset UpdatedAt,
    AgentProfileMutationOutcome? LastMutation = null,
    int TotalCount = 0,
    bool IsMaterialized = true);

public sealed class AgentProfileInvalidCursorException(string message) : ArgumentException(message);
