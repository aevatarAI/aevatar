namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Interactive authorization scopes required by the cluster-owned NyxID OAuth client.
/// Capability scopes such as <c>llm:proxy</c> belong to token exchange and must not
/// be sent to <c>/oauth/authorize</c>.
/// </summary>
public static class AevatarOAuthClientScopes
{
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string Email = "email";
    public const string OfflineAccess = "offline_access";
    public const string BrokerBinding = "urn:nyxid:scope:broker_binding";
    public const string Proxy = "proxy";
    public const string AuthorizationScope =
        $"{OpenId} {Profile} {Email} {OfflineAccess} {BrokerBinding} {Proxy}";

    public static bool ContainsRequiredAuthorizationScopes(string? scope)
    {
        return ContainsScope(scope, OpenId)
               && ContainsScope(scope, Profile)
               && ContainsScope(scope, Email)
               && ContainsScope(scope, OfflineAccess)
               && ContainsScope(scope, BrokerBinding)
               && ContainsScope(scope, Proxy);
    }

    private static bool ContainsScope(string? scope, string expected)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var remaining = scope.AsSpan();
        while (!remaining.IsEmpty)
        {
            remaining = remaining.TrimStart();
            var separator = IndexOfWhitespace(remaining);
            var token = separator < 0 ? remaining : remaining[..separator];

            if (token.Equals(expected.AsSpan(), StringComparison.Ordinal))
                return true;

            if (separator < 0)
                return false;
            remaining = remaining[(separator + 1)..];
        }

        return false;
    }

    private static int IndexOfWhitespace(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }
}
