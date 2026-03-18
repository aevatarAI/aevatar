using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed record AppApiErrorResponse(
    string Code,
    string Message,
    string? LoginUrl = null);

internal sealed class AppApiException : InvalidOperationException
{
    public AppApiException(
        int statusCode,
        string code,
        string message,
        string? loginUrl = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = string.IsNullOrWhiteSpace(code) ? "APP_API_ERROR" : code.Trim();
        LoginUrl = string.IsNullOrWhiteSpace(loginUrl) ? null : loginUrl.Trim();
    }

    public int StatusCode { get; }

    public string Code { get; }

    public string? LoginUrl { get; }
}

internal static class AppApiErrors
{
    public const string AuthRequiredCode = "AUTH_REQUIRED";
    public const string BackendAuthRequiredCode = "BACKEND_AUTH_REQUIRED";
    public const string BackendInvalidResponseCode = "BACKEND_INVALID_RESPONSE";

    public static string BuildLoginUrl(string? returnUrl)
    {
        var normalizedReturnUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? "/"
            : returnUrl.Trim();
        return $"/auth/login?returnUrl={Uri.EscapeDataString(normalizedReturnUrl)}";
    }

    public static AppApiErrorResponse CreateAuthRequiredPayload(
        string? returnUrl,
        string message = "Authentication required.",
        string code = AuthRequiredCode) =>
        new(
            code,
            string.IsNullOrWhiteSpace(message) ? "Authentication required." : message.Trim(),
            BuildLoginUrl(returnUrl));

    public static AppApiErrorResponse CreatePayload(
        string code,
        string message,
        string? loginUrl = null) =>
        new(
            string.IsNullOrWhiteSpace(code) ? "APP_API_ERROR" : code.Trim(),
            string.IsNullOrWhiteSpace(message) ? "Request failed." : message.Trim(),
            string.IsNullOrWhiteSpace(loginUrl) ? null : loginUrl.Trim());

    public static AppApiErrorResponse CreatePayload(AppApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return CreatePayload(exception.Code, exception.Message, exception.LoginUrl);
    }

    public static IResult ToResult(
        int statusCode,
        string code,
        string message,
        string? loginUrl = null) =>
        Results.Json(
            CreatePayload(code, message, loginUrl),
            statusCode: statusCode);

    public static IResult ToResult(AppApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Results.Json(CreatePayload(exception), statusCode: exception.StatusCode);
    }
}
