using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection;

internal static class LLMModelCatalogPolicyActorIdMapper
{
    public static string Build(LLMModelCatalogPolicyOwner owner) => owner.Kind switch
    {
        LLMModelCatalogPolicyOwnerKind.Platform => LLMModelCatalogPolicyConventions.PlatformActorId,
        LLMModelCatalogPolicyOwnerKind.Scope when !string.IsNullOrWhiteSpace(owner.ScopeId) =>
            LLMModelCatalogPolicyConventions.BuildScopeActorId(owner.ScopeId),
        _ => throw new ArgumentOutOfRangeException(nameof(owner)),
    };
}
