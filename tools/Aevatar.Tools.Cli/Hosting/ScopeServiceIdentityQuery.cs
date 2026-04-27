namespace Aevatar.Tools.Cli.Hosting;

internal static class ScopeServiceIdentityQuery
{
    private const string DefaultServiceAppId = "default";
    private const string DefaultServiceNamespace = "default";

    public static string BuildQueryString(
        string? scopeId,
        params (string Key, string Value)[] extraParameters)
    {
        var parts = new List<string>();
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedScopeId))
        {
            parts.Add(BuildPart("tenantId", normalizedScopeId));
            parts.Add(BuildPart("appId", DefaultServiceAppId));
            parts.Add(BuildPart("namespace", DefaultServiceNamespace));
        }

        foreach (var (key, value) in extraParameters)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            parts.Add(BuildPart(key, value));
        }

        return string.Join("&", parts);
    }

    private static string BuildPart(string key, string value) =>
        $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? string.Empty)}";
}
