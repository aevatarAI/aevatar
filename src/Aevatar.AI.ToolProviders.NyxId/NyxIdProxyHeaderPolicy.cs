namespace Aevatar.AI.ToolProviders.NyxId;

internal static class NyxIdProxyHeaderPolicy
{
    public static bool IsSensitive(string headerName)
    {
        var normalized = new string((headerName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "authorization" or "proxyauthorization" or "cookie" or "setcookie" or
               "apikey" or "xapikey" or "token" or "apitoken" or "xauthtoken" or
               "accesstoken" or "xaccesstoken" or "bearertoken" ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("accesstoken", StringComparison.Ordinal) ||
               normalized.EndsWith("authtoken", StringComparison.Ordinal);
    }
}
