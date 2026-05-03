namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdSpecCatalogRefreshFailureKind
{
    EmptySpec,
    Unauthorized,
    Forbidden,
    HttpError,
    NetworkError,
    Unexpected,
}

public sealed record NyxIdSpecCatalogStatus(
    bool BaseUrlConfigured,
    bool SpecFetchTokenConfigured,
    bool InitialRefreshAttempted,
    bool RefreshInProgress,
    int OperationCount,
    DateTimeOffset? LastSuccessfulRefreshUtc,
    string? LastRefreshError,
    NyxIdSpecCatalogRefreshFailureKind? LastRefreshFailureKind);
