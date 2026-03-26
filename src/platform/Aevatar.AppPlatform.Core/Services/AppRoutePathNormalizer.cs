namespace Aevatar.AppPlatform.Core.Services;

public static class AppRoutePathNormalizer
{
    public static string NormalizeRequired(string routePath, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePath, paramName);
        return Normalize(routePath);
    }

    public static string Normalize(string routePath)
    {
        var trimmed = routePath.Trim();
        if (trimmed.Length == 0)
            return "/";

        if (!trimmed.StartsWith('/'))
            trimmed = "/" + trimmed;

        if (trimmed.Length > 1)
            trimmed = trimmed.TrimEnd('/');

        return trimmed.ToLowerInvariant();
    }
}
