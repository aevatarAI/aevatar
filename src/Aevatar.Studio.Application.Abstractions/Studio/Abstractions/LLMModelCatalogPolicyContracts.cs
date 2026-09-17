namespace Aevatar.Studio.Application.Studio.Abstractions;

public enum LLMModelCatalogPolicyOwnerKind
{
    Platform = 1,
    Scope = 2,
}

public readonly record struct LLMModelCatalogPolicyOwner
{
    private LLMModelCatalogPolicyOwner(LLMModelCatalogPolicyOwnerKind kind, string? scopeId)
    {
        Kind = kind;
        ScopeId = scopeId;
    }

    public LLMModelCatalogPolicyOwnerKind Kind { get; }

    public string? ScopeId { get; }

    public static LLMModelCatalogPolicyOwner Platform { get; } =
        new(LLMModelCatalogPolicyOwnerKind.Platform, null);

    public static LLMModelCatalogPolicyOwner ForScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return new LLMModelCatalogPolicyOwner(
            LLMModelCatalogPolicyOwnerKind.Scope,
            scopeId.Trim());
    }
}

public enum LLMModelCatalogPolicyMode
{
    Unspecified = 0,
    InheritPlatform = 1,
    Custom = 2,
}

public abstract record LLMModelSourceIdentity(string ServiceId);

public sealed record NyxIDCatalogServiceModelSourceIdentity(string CatalogServiceId)
    : LLMModelSourceIdentity(CatalogServiceId);

public sealed record NyxIDUserServiceModelSourceIdentity(string UserServiceId)
    : LLMModelSourceIdentity(UserServiceId);

public sealed record ExplicitLLMModels(IReadOnlyList<string> UpstreamModelIds);

public sealed record LLMModelCatalogPolicySource(
    LLMModelSourceIdentity SourceIdentity,
    string? ServiceSlugSnapshot,
    ExplicitLLMModels ModelSelection);

public sealed record LLMModelCatalogPolicySnapshot(
    LLMModelCatalogPolicyOwner Owner,
    LLMModelCatalogPolicyMode Mode,
    IReadOnlyList<LLMModelCatalogPolicySource> Sources,
    long StateVersion,
    DateTimeOffset UpdatedAtUtc,
    string? LastMutationId = null);

public sealed record ReplaceLLMModelCatalogPolicy(
    LLMModelCatalogPolicyOwner Owner,
    LLMModelCatalogPolicyMode Mode,
    IReadOnlyList<LLMModelCatalogPolicySource> Sources,
    long ExpectedStateVersion,
    string MutationId);

public interface ILLMModelCatalogPolicyQueryPort
{
    Task<LLMModelCatalogPolicySnapshot?> GetAsync(
        LLMModelCatalogPolicyOwner owner,
        CancellationToken ct = default);
}

public interface ILLMModelCatalogPolicyCommandPort
{
    Task<UserConfigSaveReceipt> ReplaceAsync(
        ReplaceLLMModelCatalogPolicy command,
        CancellationToken ct = default);
}
