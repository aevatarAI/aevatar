using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Configuration for the NyxID Developer App service access defaults attached
/// to the aevatar OAuth client during dynamic client registration.
/// </summary>
public sealed class AevatarOAuthClientDefaultServiceOptions
{
    public const string SectionName = "Aevatar:OAuthClient";
    public const string DefaultServiceSlugsEnvVar = "AEVATAR_OAUTH_DEFAULT_SERVICE_SLUGS";

    public static readonly string[] BuiltInDefaultServiceSlugs = ["aevatar"];

    public string[] DefaultServiceSlugs { get; set; } = BuiltInDefaultServiceSlugs.ToArray();
}

public static class AevatarOAuthClientDefaultServices
{
    public static IReadOnlyList<string> Resolve(
        IOptions<AevatarOAuthClientDefaultServiceOptions>? options = null,
        ILogger? logger = null)
    {
        var configured = Environment.GetEnvironmentVariable(
            AevatarOAuthClientDefaultServiceOptions.DefaultServiceSlugsEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return ResolveConfigured(configured.Split(',', StringSplitOptions.RemoveEmptyEntries), logger);

        return ResolveConfigured(
            options?.Value.DefaultServiceSlugs ??
            AevatarOAuthClientDefaultServiceOptions.BuiltInDefaultServiceSlugs,
            logger);
    }

    public static string[] ResolveConfigured(IEnumerable<string>? slugs, ILogger? logger = null)
    {
        var values = Normalize(slugs, logger);
        return values.Length == 0
            ? AevatarOAuthClientDefaultServiceOptions.BuiltInDefaultServiceSlugs.ToArray()
            : values;
    }

    public static string[] Normalize(IEnumerable<string>? slugs, ILogger? logger = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (var raw in slugs ?? [])
        {
            var value = raw.Trim();
            if (value.Length == 0)
                continue;
            if (!IsValidSlug(value))
            {
                logger?.LogWarning(
                    "Ignoring invalid NyxID default service slug '{Slug}' from OAuth client configuration.",
                    value);
                continue;
            }
            if (seen.Add(value))
                values.Add(value);
        }

        return values.ToArray();
    }

    public static bool ListsEqual(IEnumerable<string> stored, IReadOnlyCollection<string> expected)
    {
        var storedValues = Normalize(stored);
        return storedValues.Length == expected.Count
            && storedValues.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool IsValidSlug(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
                continue;
            return false;
        }

        return true;
    }
}
