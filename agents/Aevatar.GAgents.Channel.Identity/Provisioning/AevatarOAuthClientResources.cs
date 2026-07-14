namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// RFC 8707 resource indicators required by aevatar's NyxID OAuth flows.
/// </summary>
public static class AevatarOAuthClientResources
{
    public const string RequiredServiceSlug = "aevatar";

    public static string RequiredServiceResourceUri(string nyxIdAuthority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nyxIdAuthority);
        return $"{nyxIdAuthority.TrimEnd('/')}/api/v1/proxy/s/{RequiredServiceSlug}";
    }

    public static string[] RequiredResourceUris(string nyxIdAuthority) =>
        [RequiredServiceResourceUri(nyxIdAuthority)];

    public static bool ContainsRequiredResource(
        IEnumerable<string>? resources,
        string nyxIdAuthority)
    {
        var required = RequiredServiceResourceUri(nyxIdAuthority);
        return resources?.Any(resource => string.Equals(
            resource?.Trim(),
            required,
            StringComparison.Ordinal)) == true;
    }
}
