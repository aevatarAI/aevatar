using Microsoft.AspNetCore.Routing;

namespace Aevatar.Hosting;

public sealed class AevatarHostHealthService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<AevatarHealthContributorRegistration> _contributors;
    private readonly IReadOnlyList<EndpointDataSource> _endpointDataSources;
    private readonly AevatarHostMetadata _hostMetadata;

    public AevatarHostHealthService(
        IServiceProvider serviceProvider,
        IEnumerable<AevatarHealthContributorRegistration> contributors,
        IEnumerable<EndpointDataSource> endpointDataSources,
        AevatarHostMetadata hostMetadata)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(endpointDataSources);
        ArgumentNullException.ThrowIfNull(hostMetadata);

        _serviceProvider = serviceProvider;
        _contributors = contributors
            .OrderBy(static contributor => contributor.Category, StringComparer.Ordinal)
            .ThenBy(static contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _endpointDataSources = endpointDataSources.ToArray();
        _hostMetadata = hostMetadata;
    }

    public Task<AevatarHealthResponse> GetLivenessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new AevatarHealthResponse(
            Ok: true,
            Service: _hostMetadata.ServiceName,
            Status: "alive",
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Components:
            [
                BuildHostComponent(
                    AevatarHealthStatuses.Healthy,
                    "Host process is running.")
            ]));
    }

    public async Task<AevatarHealthResponse> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var components = new List<AevatarHealthComponentResponse>
        {
            BuildHostComponent(
                AevatarHealthStatuses.Healthy,
                "Host process is running."),
        };

        var routePatterns = _endpointDataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => NormalizeRoute(endpoint.RoutePattern.RawText))
            .Where(static rawText => !string.IsNullOrWhiteSpace(rawText))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var contributor in _contributors)
            components.Add(await EvaluateContributorAsync(contributor, routePatterns, cancellationToken));

        var anyCriticalNotHealthy = components
            .Where(static component => component.Critical)
            .Any(component => !AevatarHealthStatuses.IsHealthy(component.Status));
        var anyNonHealthy = components.Any(component => !AevatarHealthStatuses.IsHealthy(component.Status));
        var status = anyCriticalNotHealthy
            ? "not-ready"
            : anyNonHealthy
                ? "degraded"
                : "ready";

        return new AevatarHealthResponse(
            Ok: !anyCriticalNotHealthy,
            Service: _hostMetadata.ServiceName,
            Status: status,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Components: components);
    }

    private async Task<AevatarHealthComponentResponse> EvaluateContributorAsync(
        AevatarHealthContributorRegistration contributor,
        IReadOnlyList<string> routePatterns,
        CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (contributor.RequiredRoutes.Count > 0)
            details["requiredRoutes"] = string.Join(", ", contributor.RequiredRoutes);

        var missingRoutes = contributor.RequiredRoutes
            .Where(route => !IsRouteRequirementSatisfied(routePatterns, route))
            .ToArray();
        if (missingRoutes.Length > 0)
        {
            details["missingRoutes"] = string.Join(", ", missingRoutes);
            return BuildComponent(
                contributor,
                AevatarHealthStatuses.Unhealthy,
                "Required routes are not mapped.",
                details);
        }

        if (contributor.ProbeAsync == null)
        {
            return BuildComponent(
                contributor,
                AevatarHealthStatuses.Healthy,
                "Required routes are mapped.",
                details);
        }

        try
        {
            var result = await contributor.ProbeAsync(_serviceProvider, cancellationToken);
            foreach (var entry in result.Details)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                    details[entry.Key] = entry.Value;
            }

            return BuildComponent(
                contributor,
                NormalizeStatus(result.Status),
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Capability probe completed."
                    : result.Message,
                details);
        }
        catch (Exception exception)
        {
            details["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name;
            return BuildComponent(
                contributor,
                AevatarHealthStatuses.Unhealthy,
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "Capability probe failed."
                    : exception.Message,
                details);
        }
    }

    private AevatarHealthComponentResponse BuildHostComponent(
        string status,
        string message) =>
        new(
            Name: "host",
            Category: "runtime",
            Critical: true,
            Status: status,
            Message: message,
            Details: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service"] = _hostMetadata.ServiceName,
            });

    private static AevatarHealthComponentResponse BuildComponent(
        AevatarHealthContributorRegistration contributor,
        string status,
        string message,
        IReadOnlyDictionary<string, string> details) =>
        new(
            Name: contributor.Name,
            Category: contributor.Category,
            Critical: contributor.Critical,
            Status: status,
            Message: message,
            Details: details);

    private static string NormalizeStatus(string status)
    {
        if (string.Equals(status, AevatarHealthStatuses.Healthy, StringComparison.OrdinalIgnoreCase))
            return AevatarHealthStatuses.Healthy;

        if (string.Equals(status, AevatarHealthStatuses.Degraded, StringComparison.OrdinalIgnoreCase))
            return AevatarHealthStatuses.Degraded;

        return AevatarHealthStatuses.Unhealthy;
    }

    private static bool IsRouteRequirementSatisfied(
        IReadOnlyList<string> routePatterns,
        string requiredRoute)
    {
        var normalizedRequiredRoute = NormalizeRoute(requiredRoute);
        if (string.IsNullOrWhiteSpace(normalizedRequiredRoute))
            return true;

        return routePatterns.Any(route => RouteMatchesRequirement(route, normalizedRequiredRoute));
    }

    private static bool RouteMatchesRequirement(
        string actualRoute,
        string requiredRoute)
    {
        if (string.Equals(actualRoute, requiredRoute, StringComparison.Ordinal))
            return true;

        if (!actualRoute.StartsWith(requiredRoute, StringComparison.Ordinal))
            return false;

        if (actualRoute.Length == requiredRoute.Length)
            return true;

        return actualRoute[requiredRoute.Length] is '/' or '{' or ':';
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return string.Empty;

        var normalizedRoute = route.Trim().TrimEnd('/');
        if (!normalizedRoute.StartsWith("/", StringComparison.Ordinal))
            normalizedRoute = "/" + normalizedRoute;

        return normalizedRoute;
    }
}
