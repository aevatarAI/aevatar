using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public static class NyxIdAgentKeyInventoryFallback
{
    public static bool TryReadInventory(
        NyxIdToolOptions? options,
        string agentKey,
        ILogger? logger,
        out NyxIdServiceInventoryResult inventory)
    {
        inventory = new NyxIdServiceInventoryResult();
        if (options?.EnableLocalAgentKeyInventoryFallback != true ||
            string.IsNullOrWhiteSpace(options.LocalAgentKeyInventoryFallbackJson))
        {
            return false;
        }

        try
        {
            var bindings = ReadBindings(options.LocalAgentKeyInventoryFallbackJson, agentKey);
            inventory.Instances.Add(bindings
                .Where(static binding => binding.Instance.IsActive && binding.Instance.CredentialAllowed)
                .Select(static binding => binding.Instance.Clone()));
            inventory.RecommendedSkillCatalog.Add(inventory.Instances.SelectMany(BuildRecommendedSkillCatalogEntries));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "NyxID local Agent Key inventory fallback JSON is invalid");
            return false;
        }
    }

    internal static bool TryReadBindings(
        NyxIdToolOptions? options,
        string agentKey,
        ILogger? logger,
        out IReadOnlyList<NyxIdServiceInstanceBinding> bindings)
    {
        bindings = [];
        if (options?.EnableLocalAgentKeyInventoryFallback != true ||
            string.IsNullOrWhiteSpace(options.LocalAgentKeyInventoryFallbackJson))
        {
            return false;
        }

        try
        {
            bindings = ReadBindings(options.LocalAgentKeyInventoryFallbackJson, agentKey)
                .Where(static binding => binding.Instance.IsActive && binding.Instance.CredentialAllowed)
                .ToArray();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "NyxID local Agent Key inventory fallback JSON is invalid");
            return false;
        }
    }

    public static bool TrySupplementMissingRecommendedSkillRefs(
        NyxIdToolOptions? options,
        string agentKey,
        ILogger? logger,
        NyxIdServiceInventoryResult inventory)
    {
        if (inventory.Instances.Count == 0 ||
            options?.EnableLocalAgentKeyInventoryFallback != true ||
            string.IsNullOrWhiteSpace(options.LocalAgentKeyInventoryFallbackJson))
        {
            return false;
        }

        try
        {
            var fallbackBindings = ReadActiveBindings(options.LocalAgentKeyInventoryFallbackJson, agentKey);
            var updated = SupplementMissingRecommendedSkillRefs(
                inventory.Instances,
                fallbackBindings.Select(static binding => binding.Instance));
            if (!updated)
                return false;

            inventory.RecommendedSkillCatalog.Clear();
            inventory.RecommendedSkillCatalog.Add(inventory.Instances.SelectMany(BuildRecommendedSkillCatalogEntries));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "NyxID local Agent Key inventory fallback JSON is invalid");
            return false;
        }
    }

    internal static bool TrySupplementMissingRecommendedSkillRefs(
        NyxIdToolOptions? options,
        string agentKey,
        ILogger? logger,
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings)
    {
        if (bindings.Count == 0 ||
            options?.EnableLocalAgentKeyInventoryFallback != true ||
            string.IsNullOrWhiteSpace(options.LocalAgentKeyInventoryFallbackJson))
        {
            return false;
        }

        try
        {
            var fallbackBindings = ReadActiveBindings(options.LocalAgentKeyInventoryFallbackJson, agentKey);
            return SupplementMissingRecommendedSkillRefs(
                bindings.Select(static binding => binding.Instance),
                fallbackBindings.Select(static binding => binding.Instance));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "NyxID local Agent Key inventory fallback JSON is invalid");
            return false;
        }
    }

    private static IReadOnlyList<NyxIdServiceInstanceBinding> ReadBindings(string json, string agentKey) =>
        NyxIdServiceInstanceClient.ParseBindings(
            json,
            agentKey,
            NyxIdServiceAccessTokenSource.User);

    private static IReadOnlyList<NyxIdServiceInstanceBinding> ReadActiveBindings(string json, string agentKey) =>
        ReadBindings(json, agentKey)
            .Where(static binding => binding.Instance.IsActive && binding.Instance.CredentialAllowed)
            .ToArray();

    private static bool SupplementMissingRecommendedSkillRefs(
        IEnumerable<NyxIdServiceInstance> instances,
        IEnumerable<NyxIdServiceInstance> fallbackInstances)
    {
        var fallbackById = fallbackInstances
            .Where(static instance => instance.RecommendedSkillRefs.Count > 0)
            .GroupBy(static instance => instance.UserServiceId, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.Ordinal);
        var updated = false;
        foreach (var instance in instances)
        {
            if (instance.RecommendedSkillRefs.Count > 0 ||
                !fallbackById.TryGetValue(instance.UserServiceId, out var fallbackInstance))
            {
                continue;
            }

            instance.RecommendedSkillRefs.Add(fallbackInstance.RecommendedSkillRefs.Select(static skillRef => skillRef.Clone()));
            updated = true;
        }

        return updated;
    }

    private static IEnumerable<NyxIdRecommendedSkillCatalogEntry> BuildRecommendedSkillCatalogEntries(
        NyxIdServiceInstance instance)
    {
        foreach (var skillRef in instance.RecommendedSkillRefs)
        {
            var title = FirstNonEmpty(skillRef.DisplayName, skillRef.RecommendationName, skillRef.SkillId);
            yield return new NyxIdRecommendedSkillCatalogEntry
            {
                UserServiceId = instance.UserServiceId,
                ServiceSlug = instance.DisplaySlug,
                ServiceLabel = FirstNonEmpty(instance.Label, instance.DisplaySlug, instance.CatalogServiceSlug),
                SkillRef = skillRef.Clone(),
                Title = title,
                TaskSummary = $"Load this recommended skill for {FirstNonEmpty(instance.Label, instance.DisplaySlug, instance.CatalogServiceSlug)} connected-service tasks related to {title}.",
            };
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
