using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed record ResolvedLLMModelSource(
    NyxIdResolvedModelSource Source,
    ExplicitLLMModels ModelSelection);

internal sealed class LLMModelSourceResolver
{
    private readonly ILLMModelCatalogPolicyQueryPort _policyQueryPort;

    public LLMModelSourceResolver(ILLMModelCatalogPolicyQueryPort policyQueryPort)
    {
        _policyQueryPort = policyQueryPort ?? throw new ArgumentNullException(nameof(policyQueryPort));
    }

    public async Task<IReadOnlyList<ResolvedLLMModelSource>> ResolveAsync(
        string scopeId,
        CancellationToken ct)
    {
        var configuredSources = await ReadEffectiveSourcesAsync(scopeId, ct).ConfigureAwait(false);
        return ResolveTargets(configuredSources);
    }

    public async Task<IReadOnlyList<LLMModelCatalogPolicySource>> ReadEffectiveSourcesAsync(
        string scopeId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        var scopePolicy = await _policyQueryPort
            .GetAsync(LLMModelCatalogPolicyOwner.ForScope(scopeId), ct)
            .ConfigureAwait(false);
        if (scopePolicy?.Mode == LLMModelCatalogPolicyMode.Custom)
            return scopePolicy.Sources;

        var platformPolicy = await _policyQueryPort
            .GetAsync(LLMModelCatalogPolicyOwner.Platform, ct)
            .ConfigureAwait(false);
        if (platformPolicy is null)
        {
            throw new InvalidOperationException(
                "The effective platform model catalog policy projection is unavailable.");
        }

        return platformPolicy.Sources;
    }

    internal static IReadOnlyList<ResolvedLLMModelSource> ResolveTargets(
        IReadOnlyList<LLMModelCatalogPolicySource> configuredSources)
    {
        var targets = new List<ResolvedLLMModelSource>();
        foreach (var configuredSource in configuredSources)
        {
            var serviceSlug = configuredSource.ServiceSlugSnapshot;
            if (!NyxIdServiceSlugPolicy.IsCanonical(serviceSlug))
            {
                throw new InvalidDataException(
                    "A configured model source requires a canonical service slug snapshot.");
            }

            var source = configuredSource.SourceIdentity switch
            {
                NyxIDUserServiceModelSourceIdentity userService =>
                    new NyxIdResolvedUserModelSource(userService.UserServiceId, serviceSlug!)
                        as NyxIdResolvedModelSource,
                NyxIDCatalogServiceModelSourceIdentity catalogService =>
                    new NyxIdResolvedCatalogModelSource(catalogService.CatalogServiceId, serviceSlug!),
                _ => null,
            };
            if (source is null)
                continue;

            targets.Add(new ResolvedLLMModelSource(
                source,
                configuredSource.ModelSelection));
        }

        if (targets
            .GroupBy(static target => target.Source.ServiceSlug, StringComparer.Ordinal)
            .Any(static group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "A model catalog policy cannot contain duplicate service slug snapshots.");
        }

        return targets;
    }
}
