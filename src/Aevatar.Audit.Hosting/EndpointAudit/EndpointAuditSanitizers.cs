using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public static class EndpointAuditSanitizers
{
    private const string Redacted = "redacted";
    private const string JwtSegmentPattern = "[A-Za-z0-9_-]{8,}";

    private static readonly string[] SensitiveValueFragments =
    [
        "authorization",
        "bearer",
        "token",
        "secret",
        "password",
        "cookie",
        "api_key",
        "apikey",
        "oauth",
        "credential",
        "private_key",
    ];

    private static readonly Regex JwtPattern = new(
        $@"\b{JwtSegmentPattern}\.{JwtSegmentPattern}\.{JwtSegmentPattern}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiKeyPattern = new(
        @"\b(?:sk|pk|ak|key)-[A-Za-z0-9_-]{16,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PhonePattern = new(
        @"(?<!\w)\+[0-9][0-9 .()-]{7,}[0-9](?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ValueTask<string> RouteOnlyRequest(EndpointAuditSanitizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult(
            $"{context.HttpContext.Request.Method} {ResolveRoutePattern(context.HttpContext)}");
    }

    public static ValueTask<string> StatusOnlyResult(EndpointAuditSanitizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult($"status={ResolveStatusCode(context)}");
    }

    public static EndpointAuditSummarySanitizer WithRouteValues(params string[] routeValueNames)
    {
        var names = routeValueNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return context =>
        {
            var parts = new List<string>
            {
                $"{context.HttpContext.Request.Method} {ResolveRoutePattern(context.HttpContext)}",
            };

            foreach (var name in names)
            {
                var value = context.HttpContext.Request.RouteValues[name]?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add($"{name}={SanitizeValue(value)}");
                }
            }

            return ValueTask.FromResult(string.Join(' ', parts));
        };
    }

    public static string ResolveRoutePattern(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? httpContext.Request.Path.Value ?? string.Empty
            : httpContext.Request.Path.Value ?? string.Empty;
    }

    public static string SanitizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return LooksSensitive(normalized) ? Redacted : normalized;
    }

    private static int ResolveStatusCode(EndpointAuditSanitizationContext context)
    {
        return context.Result is IStatusCodeHttpResult statusCodeHttpResult
            ? statusCodeHttpResult.StatusCode ?? context.HttpContext.Response.StatusCode
            : context.HttpContext.Response.StatusCode;
    }

    private static bool LooksSensitive(string value)
    {
        return SensitiveValueFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
               JwtPattern.IsMatch(value) ||
               ApiKeyPattern.IsMatch(value) ||
               EmailPattern.IsMatch(value) ||
               PhonePattern.IsMatch(value);
    }
}
