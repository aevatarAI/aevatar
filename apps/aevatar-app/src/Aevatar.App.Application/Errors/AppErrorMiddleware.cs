using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Errors;

public sealed class AppErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AppErrorMiddleware> _logger;

    public AppErrorMiddleware(RequestDelegate next, ILogger<AppErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            _logger.LogWarning(ex, "Business error: {Code}", ex.Code);
            context.Response.StatusCode = ex.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = ex.Code,
                    message = ex.Message,
                    issues = ex.Issues
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "UNAUTHORIZED", message = "Not authenticated" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "INTERNAL_ERROR", message = "An unexpected error occurred" }
            });
        }
    }
}

public class AppException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }
    public object? Issues { get; }

    public AppException(string code, string message, int statusCode = 400, object? issues = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Issues = issues;
    }
}

public sealed class ValidationException : AppException
{
    public ValidationException(string message, object? issues = null)
        : base("VALIDATION_ERROR", message, 400, issues) { }
}

public sealed class NotFoundException : AppException
{
    public NotFoundException(string resource)
        : base("NOT_FOUND", $"{resource} not found", 404) { }
}

public sealed class ConflictException : AppException
{
    public ConflictException(string message)
        : base("CONFLICT", message, 409) { }
}

public sealed class LimitReachedException : AppException
{
    public string? RetryAfter { get; }

    public LimitReachedException(string code, string message, string? retryAfter = null)
        : base(code, message, 429)
    {
        RetryAfter = retryAfter;
    }
}
