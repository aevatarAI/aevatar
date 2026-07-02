using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

/// <summary>
/// Emits request start/finish log lines with the query string redacted, as a
/// replacement for ASP.NET Core's built-in <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>
/// request logging.
/// <para>
/// The framework logs the raw request URL (query string included) on both
/// "Request starting" and "Request finished". The "Request starting" line is
/// emitted by the host <em>before</em> the middleware pipeline runs, so no
/// middleware can rewrite the query string in time to redact it. The only robust
/// fix is to suppress the framework's request logging (done via a log filter in
/// <c>AddAevatarDefaultHost</c>) and emit this redacted equivalent instead, so a
/// secret carried in the query — e.g. <c>/ws/voice?access_token=&lt;JWT&gt;</c> —
/// never reaches stdout/Elasticsearch.
/// </para>
/// </summary>
public sealed class RedactingRequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RedactingRequestLoggingMiddleware> _logger;

    public RedactingRequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RedactingRequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var redactedQuery = SensitiveQueryRedactor.Redact(request.QueryString.Value);

        _logger.LogInformation(
            "Request starting {Protocol} {Method} {Scheme}://{Host}{Path}{Query}",
            request.Protocol,
            request.Method,
            request.Scheme,
            request.Host.Value,
            request.Path.Value,
            redactedQuery);

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _logger.LogInformation(
                "Request finished {Protocol} {Method} {Scheme}://{Host}{Path}{Query} - {StatusCode} in {ElapsedMs}ms",
                request.Protocol,
                request.Method,
                request.Scheme,
                request.Host.Value,
                request.Path.Value,
                redactedQuery,
                context.Response.StatusCode,
                elapsedMs);
        }
    }
}
