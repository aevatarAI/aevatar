using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgents.UserConfig;

public static class LLMModelCatalogPolicyConventions
{
    public const string PlatformActorId = "llm-model-catalog-policy-platform";

    public static string BuildScopeActorId(string scopeId)
    {
        var normalized = scopeId?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(normalized) > LLMModelCatalogPolicyLimits.MaxScopeIdUtf8Bytes)
        {
            throw new ArgumentException("scopeId is invalid.", nameof(scopeId));
        }

        return $"llm-model-catalog-policy-scope-{normalized}";
    }

    public static string BuildActorId(
        LLMModelCatalogPolicyOwnerType ownerType,
        string scopeId) => ownerType switch
        {
            LLMModelCatalogPolicyOwnerType.Platform when string.IsNullOrEmpty(scopeId) =>
                PlatformActorId,
            LLMModelCatalogPolicyOwnerType.Scope => BuildScopeActorId(scopeId),
            _ => throw new ArgumentException("Model catalog policy owner identity is invalid."),
        };
}
