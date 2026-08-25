namespace Aevatar.AIWorkspace.Application.Abstractions;

public interface IAIWorkspaceAgentsQueryService
{
    Task<AIWorkspaceQueryResult<AIWorkspaceAgentsView>> QueryAsync(
        string scopeId,
        AIWorkspaceAgentsQuery query,
        CancellationToken ct = default);
}

public interface IAIWorkspaceModelsQueryService
{
    Task<AIWorkspaceModelsView> QueryAsync(
        string scopeId,
        string? bearerToken,
        CancellationToken ct = default);
}

public interface IAIWorkspaceActivityQueryService
{
    Task<AIWorkspaceQueryResult<AIWorkspaceActivityView>> QueryAsync(
        string scopeId,
        AIWorkspaceActivityQuery query,
        CancellationToken ct = default);

    Task<AIWorkspaceQueryResult<AIWorkspaceConversationCollectionView>> QueryConversationsAsync(
        string scopeId,
        AIWorkspacePageQuery query,
        CancellationToken ct = default);

    Task<AIWorkspaceQueryResult<AIWorkspaceRunCollectionView>> QueryRunsAsync(
        string scopeId,
        AIWorkspaceRunsQuery query,
        CancellationToken ct = default);

    Task<AIWorkspaceQueryResult<AIWorkspaceRunDetailView>> GetRunAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default);
}

public interface IAIWorkspaceOverviewQueryService
{
    Task<AIWorkspaceQueryResult<AIWorkspaceOverviewView>> QueryAsync(
        string scopeId,
        int take,
        CancellationToken ct = default);
}

public sealed record AIWorkspaceAgentsQuery(
    string? OwnedCursor = null,
    string? SystemCursor = null,
    int Take = 50);

public sealed record AIWorkspacePageQuery(
    int Take = 50,
    string? Cursor = null);

public sealed record AIWorkspaceActivityQuery(
    int Take = 50,
    string? ConversationCursor = null,
    string? RunCursor = null);

public enum AIWorkspaceRunOriginFilter
{
    Interactive = 0,
    Integration = 1,
    Automation = 2,
    Development = 3,
}

public sealed record AIWorkspaceRunsQuery(
    string? Status = null,
    IReadOnlyList<AIWorkspaceRunOriginFilter>? Origins = null,
    string? WorkflowId = null,
    string? SearchText = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Take = 50,
    string? Cursor = null,
    bool IncludeTotalCount = false);

public enum AIWorkspaceQueryFailureKind
{
    InvalidInput = 0,
    InvalidCursor = 1,
    NotFound = 2,
    Unavailable = 3,
}

public sealed record AIWorkspaceQueryFailure(
    AIWorkspaceQueryFailureKind Kind,
    string Code,
    string Message);

public sealed record AIWorkspaceQueryResult<T>(
    T? Value,
    AIWorkspaceQueryFailure? Failure)
{
    public static AIWorkspaceQueryResult<T> Success(T value) => new(value, null);

    public static AIWorkspaceQueryResult<T> Fail(
        AIWorkspaceQueryFailureKind kind,
        string code,
        string message) =>
        new(default, new AIWorkspaceQueryFailure(kind, code, message));
}
