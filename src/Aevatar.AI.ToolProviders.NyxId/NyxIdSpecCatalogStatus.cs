namespace Aevatar.AI.ToolProviders.NyxId;

public sealed record NyxIdSpecCatalogStatus(
    bool BaseUrlConfigured,
    bool SpecFetchTokenConfigured,
    int OperationCount,
    DateTimeOffset? LastSuccessfulRefreshUtc,
    string? LastRefreshError);
