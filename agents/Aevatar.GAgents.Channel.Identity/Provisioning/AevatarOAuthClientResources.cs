namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// RFC 8707 resource indicators required by aevatar's NyxID OAuth flows.
/// </summary>
public static class AevatarOAuthClientResources
{
    public const string RequiredServiceSlug = "aevatar";

    public static string RequiredServiceResourceUri(string nyxIdApiBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nyxIdApiBaseUrl);
        return $"{nyxIdApiBaseUrl.Trim().TrimEnd('/')}/api/v1/proxy/s/{RequiredServiceSlug}";
    }

    public static string[] RequiredResourceUris(string nyxIdApiBaseUrl) =>
        [RequiredServiceResourceUri(nyxIdApiBaseUrl)];

    public static bool ContainsRequiredResource(
        IEnumerable<string>? resources,
        string nyxIdApiBaseUrl)
    {
        var required = RequiredServiceResourceUri(nyxIdApiBaseUrl);
        return resources?.Any(resource => string.Equals(
            resource?.Trim(),
            required,
            StringComparison.Ordinal)) == true;
    }
}
