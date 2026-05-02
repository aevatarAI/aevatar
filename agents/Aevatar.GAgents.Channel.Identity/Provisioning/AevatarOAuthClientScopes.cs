namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Scopes required by the cluster-owned NyxID OAuth client.
/// </summary>
public static class AevatarOAuthClientScopes
{
    public const string OpenId = "openid";
    public const string BrokerBinding = "urn:nyxid:scope:broker_binding";
    public const string Proxy = "proxy";
    public const string AuthorizationScope = $"{OpenId} {BrokerBinding} {Proxy}";

    public static bool ContainsRequiredScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var scopes = scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return scopes.Contains(OpenId) && scopes.Contains(BrokerBinding) && scopes.Contains(Proxy);
    }

    public static string EnsureRequiredScopes(string? scope)
    {
        var scopes = string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        AppendMissing(scopes, OpenId);
        AppendMissing(scopes, BrokerBinding);
        AppendMissing(scopes, Proxy);
        return string.Join(' ', scopes);
    }

    private static void AppendMissing(List<string> scopes, string required)
    {
        if (!scopes.Contains(required, StringComparer.Ordinal))
            scopes.Add(required);
    }
}
