namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMModelCatalogPolicyLimits
{
    public const int MaxSources = 32;
    public const int MaxExplicitModelsPerSource = 256;
    public const int MaxExplicitModelsPerPolicy = LLMSelectionPolicy.MaxModelsPerCatalog;
    public const int MaxServiceIdentityUtf8Bytes = 256;
    public const int MaxServiceSlugUtf8Bytes = NyxIdServiceSlugPolicy.MaxLength;
    public const int MaxMutationIdUtf8Bytes = 128;
    public const int MaxScopeIdUtf8Bytes = 256;
}
