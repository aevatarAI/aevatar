using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ConnectorHttpAuthorizationExtractor
{
    private const string BearerPrefix = "Bearer ";

    public static string? Extract(HttpContext? http)
    {
        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null || !auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var bearerToken = auth[BearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(bearerToken) ? null : $"{BearerPrefix}{bearerToken}";
    }
}
