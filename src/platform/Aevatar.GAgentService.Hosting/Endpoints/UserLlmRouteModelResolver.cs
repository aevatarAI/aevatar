using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class UserLlmRouteModelResolver
{
    public static async Task<(string? Model, string? Route)> ResolveAsync(
        HttpContext http,
        string? defaultModel,
        string? preferredRoute,
        CancellationToken ct)
    {
        var model = NormalizeOptional(defaultModel);
        var route = NormalizeOptional(preferredRoute);
        if (!string.IsNullOrWhiteSpace(route) ||
            UserConfigLlmModel.TryParseRouteModel(model) is not { } prefixed)
        {
            return (model, route);
        }

        var catalogPort = http.RequestServices.GetService<IUserLlmCatalogPort>();
        var bearerToken = ExtractBearerToken(http);
        if (catalogPort is null || string.IsNullOrWhiteSpace(bearerToken))
            return (model, route);

        try
        {
            var result = await catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
            var service = result.Services.FirstOrDefault(candidate => IsSameService(candidate, prefixed.RouteSlug));
            if (service is null || !IsSelectable(service))
                return (model, route);

            return (prefixed.Model, UserConfigLlmRoute.Normalize(service.RouteValue));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (model, route);
        }
    }

    private static bool IsSameService(NyxIdLlmService service, string requested) =>
        string.Equals(service.UserServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(service.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(service.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(service.RouteValue, UserConfigLlmRoute.Normalize(requested), StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectable(NyxIdLlmService service) =>
        service.Allowed &&
        string.Equals(service.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(service.RouteValue);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
