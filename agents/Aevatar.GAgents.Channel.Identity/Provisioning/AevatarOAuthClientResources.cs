namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// RFC 8707 resource indicators required by aevatar's NyxID OAuth flows.
/// </summary>
public static class AevatarOAuthClientResources
{
    public const string RequiredServiceSlug = "aevatar";

    public static string RequiredServiceResourceUri(string nyxIdAuthority)
        => ServiceResourceUri(nyxIdAuthority, RequiredServiceSlug);

    public static string ServiceResourceUri(string nyxIdAuthority, string serviceSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nyxIdAuthority);
        var normalizedServiceSlug = NormalizeServiceSlug(serviceSlug);
        return $"{nyxIdAuthority.Trim().TrimEnd('/')}/api/v1/proxy/s/{normalizedServiceSlug}";
    }

    public static string[] RequiredResourceUris(
        string nyxIdAuthority,
        string? requiredLlmServiceSlug,
        IEnumerable<string>? additionalRequiredServiceSlugs = null)
    {
        var serviceSlugs = new List<string> { RequiredServiceSlug };
        if (!string.IsNullOrWhiteSpace(requiredLlmServiceSlug))
            serviceSlugs.Add(requiredLlmServiceSlug);
        if (additionalRequiredServiceSlugs is not null)
        {
            serviceSlugs.AddRange(additionalRequiredServiceSlugs.Where(
                static serviceSlug => !string.IsNullOrWhiteSpace(serviceSlug)));
        }

        return serviceSlugs
            .Select(serviceSlug => ServiceResourceUri(nyxIdAuthority, serviceSlug))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string[] MissingRequiredResources(
        IEnumerable<string>? resources,
        IEnumerable<string> requiredResources)
    {
        ArgumentNullException.ThrowIfNull(requiredResources);

        var granted = resources?
            .Where(static resource => !string.IsNullOrWhiteSpace(resource))
            .Select(static resource => resource.Trim())
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        return requiredResources
            .Where(static resource => !string.IsNullOrWhiteSpace(resource))
            .Select(static resource => resource.Trim())
            .Distinct(StringComparer.Ordinal)
            .Where(resource => !granted.Contains(resource))
            .ToArray();
    }

    private static string NormalizeServiceSlug(string serviceSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceSlug);
        var normalized = serviceSlug.Trim();
        if (!IsValidServiceSlug(normalized))
        {
            throw new ArgumentException(
                "NyxID service slug must be 1-80 lowercase ASCII letters or digits separated by single hyphens.",
                nameof(serviceSlug));
        }

        return normalized;
    }

    internal static bool IsValidServiceSlug(string? serviceSlug)
    {
        if (string.IsNullOrWhiteSpace(serviceSlug))
            return false;

        var normalized = serviceSlug.Trim();
        return normalized.Length <= 80
               && normalized[0] != '-'
               && normalized[^1] != '-'
               && !normalized.Contains("--", StringComparison.Ordinal)
               && normalized.All(static character =>
                   character is >= 'a' and <= 'z'
                   || character is >= '0' and <= '9'
                   || character == '-');
    }
}
