using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public static class EndpointAuditTargetResolvers
{
    public static EndpointAuditTargetResolver Static(string kind, string id = "", string displayName = "")
    {
        return _ => ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget(
            Normalize(kind),
            EndpointAuditSanitizers.SanitizeValue(id),
            EndpointAuditSanitizers.SanitizeValue(displayName)));
    }

    public static EndpointAuditTargetResolver FromRouteValue(
        string kind,
        string routeValueName,
        string? displayNameRouteValueName = null)
    {
        if (string.IsNullOrWhiteSpace(routeValueName))
        {
            throw new ArgumentException("Route value name is required.", nameof(routeValueName));
        }

        var normalizedKind = Normalize(kind);
        var normalizedRouteValueName = routeValueName.Trim();
        var normalizedDisplayRouteValueName = displayNameRouteValueName?.Trim();

        return httpContext =>
        {
            var id = EndpointAuditSanitizers.SanitizeValue(
                httpContext.Request.RouteValues[normalizedRouteValueName]?.ToString());
            var displayName = string.IsNullOrWhiteSpace(normalizedDisplayRouteValueName)
                ? string.Empty
                : EndpointAuditSanitizers.SanitizeValue(
                    httpContext.Request.RouteValues[normalizedDisplayRouteValueName]?.ToString());
            return ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget(
                normalizedKind,
                id,
                displayName));
        };
    }

    public static EndpointAuditTargetResolver FromRouteValues(
        string kind,
        params string[] routeValueNames)
    {
        var normalizedKind = Normalize(kind);
        var normalizedRouteValueNames = routeValueNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return httpContext =>
        {
            var id = string.Join(
                '/',
                normalizedRouteValueNames
                    .Select(name => EndpointAuditSanitizers.SanitizeValue(
                        httpContext.Request.RouteValues[name]?.ToString()))
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
            return ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget(normalizedKind, id));
        };
    }

    public static EndpointAuditTargetResolver FromQuery(
        string kind,
        string queryName)
    {
        if (string.IsNullOrWhiteSpace(queryName))
        {
            throw new ArgumentException("Query name is required.", nameof(queryName));
        }

        var normalizedKind = Normalize(kind);
        var normalizedQueryName = queryName.Trim();

        return httpContext =>
        {
            var id = EndpointAuditSanitizers.SanitizeValue(
                httpContext.Request.Query[normalizedQueryName].ToString());
            return ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget(normalizedKind, id));
        };
    }

    private static string Normalize(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Target kind is required.", nameof(kind));
        }

        return kind.Trim();
    }
}
