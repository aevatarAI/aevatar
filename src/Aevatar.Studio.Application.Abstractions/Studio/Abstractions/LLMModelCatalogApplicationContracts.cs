namespace Aevatar.Studio.Application.Studio.Abstractions;

public enum LLMModelCatalogApplicationErrorKind
{
    InvalidRequest = 1,
    Conflict = 2,
    Unavailable = 3,
    AuthenticationRejected = 4,
    Forbidden = 5,
}

public sealed class LLMModelCatalogApplicationException : Exception
{
    public LLMModelCatalogApplicationException(
        LLMModelCatalogApplicationErrorKind kind,
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    public LLMModelCatalogApplicationErrorKind Kind { get; }

    public string Code { get; }
}

public sealed record ReplaceScopeLLMModelCatalogIntent(
    LLMModelCatalogPolicyMode Mode,
    long ExpectedStateVersion,
    string? MutationId,
    IReadOnlyList<ScopeLLMModelCatalogSourceIntent?>? Sources);

public sealed record ReplacePlatformLLMModelCatalogIntent(
    long ExpectedStateVersion,
    string? MutationId,
    IReadOnlyList<PlatformLLMModelCatalogSourceIntent?>? Sources);

public sealed record LLMModelCatalogResetIntent(
    long ExpectedStateVersion,
    string? MutationId);

public sealed record ScopeLLMModelCatalogSourceIntent(
    string? ServiceSlugSnapshot,
    string? UserServiceId,
    ExplicitLLMModelsIntent? ModelSelection);

public sealed record PlatformLLMModelCatalogSourceIntent(
    string? ServiceSlugSnapshot,
    string? CatalogServiceId,
    ExplicitLLMModelsIntent? ModelSelection);

public sealed record ExplicitLLMModelsIntent(IReadOnlyList<string?>? ModelIds);

public enum LLMModelCatalogEffectiveSourceKind
{
    Platform = 1,
    Scope = 2,
}

public sealed record LLMModelCatalogView(
    LLMModelCatalogPolicyOwner Owner,
    LLMModelCatalogPolicyMode Mode,
    bool Configured,
    long StateVersion,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<LLMModelCatalogPolicySource> Sources,
    LLMModelCatalogEffectiveSourceKind EffectiveSource,
    IReadOnlyList<LLMModelCatalogPolicySource> EffectiveSources,
    string? LastMutationId);

public sealed record LLMModelSourceDiscoveryView(
    string SourceIdentity,
    string ServiceSlug,
    IReadOnlyList<string> ModelIds,
    string? DefaultModelId);

public sealed record LLMModelDescriptor(
    string Id,
    long Created,
    string OwnedBy,
    string Group,
    int? ContextLength,
    int? MaxOutputTokens,
    string? DisplayName,
    string? Description);

public abstract record NyxIdResolvedModelSource(string ServiceSlug);

public sealed record NyxIdResolvedCatalogModelSource(
    string CatalogServiceId,
    string ServiceSlug)
    : NyxIdResolvedModelSource(ServiceSlug);

public sealed record NyxIdResolvedUserModelSource(
    string UserServiceId,
    string ServiceSlug)
    : NyxIdResolvedModelSource(ServiceSlug);

public interface ILLMModelCatalogPolicyApplicationService
{
    Task<LLMModelCatalogView> GetScopeAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<LLMModelCatalogView> GetPlatformAsync(CancellationToken ct = default);

    Task<UserConfigSaveReceipt> ReplaceScopeAsync(
        string scopeId,
        ReplaceScopeLLMModelCatalogIntent intent,
        CancellationToken ct = default);

    Task<UserConfigSaveReceipt> ResetScopeAsync(
        string scopeId,
        LLMModelCatalogResetIntent intent,
        CancellationToken ct = default);

    Task<UserConfigSaveReceipt> ReplacePlatformAsync(
        ReplacePlatformLLMModelCatalogIntent intent,
        CancellationToken ct = default);

    Task<IReadOnlyList<NyxIdScopeModelSourceService>> GetScopeCandidatesAsync(
        string bearerToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<NyxIdPlatformModelSourceService>> GetPlatformCandidatesAsync(
        string bearerToken,
        CancellationToken ct = default);

    Task<LLMModelSourceDiscoveryView> DiscoverScopeModelsAsync(
        string bearerToken,
        string userServiceId,
        CancellationToken ct = default);

    Task<LLMModelSourceDiscoveryView> DiscoverPlatformModelsAsync(
        string bearerToken,
        string catalogServiceId,
        CancellationToken ct = default);
}

public interface ILLMModelDiscoveryApplicationService
{
    Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(
        string scopeId,
        CancellationToken ct = default);
}

public interface ILLMModelRouteApplicationService
{
    Task<NyxIdResolvedModelSource?> ResolveAsync(
        string scopeId,
        string serviceSlug,
        string upstreamModelId,
        CancellationToken ct = default);
}
