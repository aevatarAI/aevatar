using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Host.Api;

internal static class ApiTracingScopeMiddlewareExtensions
{
    internal static IApplicationBuilder UseAevatarApiTracingScope(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var loggerFactory = context.RequestServices.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Aevatar.Workflow.Host.Api.Scope");

            var scopeState = new Dictionary<string, object?>
            {
                ["trace_id"] = Activity.Current?.TraceId.ToString() ?? string.Empty,
                ["correlation_id"] = ResolveCorrelationId(context),
                ["causation_id"] = string.Empty,
            };

            using var _ = logger?.BeginScope(scopeState);
            await next(context);
        });
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var headerValues))
        {
            var value = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
