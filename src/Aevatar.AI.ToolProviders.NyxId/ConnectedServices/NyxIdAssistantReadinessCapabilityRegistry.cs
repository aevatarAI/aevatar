namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdAssistantReadinessCapabilityRegistry
{
    public static string? Resolve(
        NyxIdToolOptions options,
        string? catalogServiceSlug)
    {
        if (string.IsNullOrWhiteSpace(catalogServiceSlug))
            return null;

        var matches = options.AssistantReadinessCapabilityBindings
            .Where(static binding =>
                !string.IsNullOrWhiteSpace(binding.CatalogServiceSlug) &&
                string.Equals(
                    binding.CatalogServiceSlug,
                    binding.CatalogServiceSlug.Trim(),
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(binding.ReadinessCapabilityId) &&
                string.Equals(
                    binding.ReadinessCapabilityId,
                    binding.ReadinessCapabilityId.Trim(),
                    StringComparison.Ordinal))
            .Where(binding => string.Equals(
                binding.CatalogServiceSlug,
                catalogServiceSlug,
                StringComparison.Ordinal))
            .Select(static binding => binding.ReadinessCapabilityId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
