namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentToolSourceReadableNyxIdCredential
{
    public static string? ResolveBearerToken(AgentToolCredentials? credentials)
    {
        if (credentials?.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.SourceReadableUserBearer)
            return Normalize(credentials.NyxIdAccessToken);
        if (credentials?.NyxIdCredentialKind != AgentToolNyxIdCredentialKind.ProxyDelegation)
            return null;

        return Normalize(credentials.SourceReadableNyxIdAccessToken);
    }

    private static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
                return null;
        }

        return normalized;
    }
}
