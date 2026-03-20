using System.Net;
using System.Net.Http.Headers;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Bridge;

internal sealed class AppBridgeRouteOptions
{
    public bool MapCapabilityRoutes { get; init; }
}

internal static class AppBridgeEndpoints
{
    private const string BackendClientName = "AppBridgeBackend";

    public static void Map(IEndpointRouteBuilder app, AppBridgeRouteOptions? options = null)
    {
        options ??= new AppBridgeRouteOptions();
        if (!options.MapCapabilityRoutes)
            return;

        app.MapPost("/api/chat", HandleBackendProxyAsync);
        app.MapPost("/api/workflows/resume", HandleBackendProxyAsync);
        app.MapPost("/api/workflows/signal", HandleBackendProxyAsync);
        app.MapGet("/api/scopes/{scopeId}/workflows", HandleBackendProxyAsync);
        app.MapGet("/api/scopes/{scopeId}/workflows/{workflowId}", HandleBackendProxyAsync);
        app.MapPut("/api/scopes/{scopeId}/workflows/{workflowId}", HandleBackendProxyAsync);
        app.MapPost("/api/scopes/{scopeId}/workflows/{workflowId}/runs:stream", HandleBackendProxyAsync);
        app.MapPost("/api/scopes/{scopeId}/workflow-runs:stream", HandleBackendProxyAsync);
        app.MapGet("/api/scopes/{scopeId}/scripts", HandleBackendProxyAsync);
        app.MapGet("/api/scopes/{scopeId}/scripts/{scriptId}", HandleBackendProxyAsync);
        app.MapPut("/api/scopes/{scopeId}/scripts/{scriptId}", HandleBackendProxyAsync);
        app.MapPost("/api/scopes/{scopeId}/scripts/{scriptId}/evolutions/proposals", HandleBackendProxyAsync);
    }

    private static async Task HandleBackendProxyAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(BackendClientName);
        using var request = CreateProxyRequest(context, client.BaseAddress);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (TryCreateProxyErrorResult(response, out var errorResult))
            {
                await errorResult.ExecuteAsync(context);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response);

            if (response.Content != null)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await stream.CopyToAsync(context.Response.Body, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "BACKEND_UNAVAILABLE",
                message = ex.Message,
            }, cancellationToken);
        }
    }

    internal static bool TryCreateProxyErrorResult(
        HttpResponseMessage response,
        out IResult result)
    {
        ArgumentNullException.ThrowIfNull(response);

        var redirectUrl = ResolveRedirectUrl(response);
        if (redirectUrl != null &&
            response.StatusCode is HttpStatusCode.Moved or
                HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
        {
            result = AppApiErrors.ToResult(
                StatusCodes.Status401Unauthorized,
                AppApiErrors.BackendAuthRequiredCode,
                "Backend authentication required.",
                redirectUrl);
            return true;
        }

        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        if (IsHtmlContentType(mediaType))
        {
            result = AppApiErrors.ToResult(
                StatusCodes.Status502BadGateway,
                AppApiErrors.BackendInvalidResponseCode,
                "Backend returned HTML for an API request.",
                loginUrl: null);
            return true;
        }

        result = Results.Empty;
        return false;
    }

    private static HttpRequestMessage CreateProxyRequest(HttpContext context, Uri? baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var target = new Uri(baseAddress, $"{context.Request.Path}{context.Request.QueryString}");

        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (ShouldAttachBody(context.Request.Method))
            request.Content = new StreamContent(context.Request.Body);

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) &&
                request.Content != null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        if (request.Content != null &&
            !string.IsNullOrWhiteSpace(context.Request.ContentType) &&
            request.Content.Headers.ContentType == null)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        return request;
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, HttpResponse target)
    {
        foreach (var header in response.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key))
                continue;

            target.Headers[header.Key] = header.Value.ToArray();
        }

        if (response.Content == null)
            return;

        foreach (var header in response.Content.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key))
                continue;

            target.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static string? ResolveRedirectUrl(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location == null)
            return null;

        if (location.IsAbsoluteUri)
            return location.ToString();

        var requestUri = response.RequestMessage?.RequestUri;
        return requestUri == null
            ? location.ToString()
            : new Uri(requestUri, location).ToString();
    }

    private static bool IsHtmlContentType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldAttachBody(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method);

    private static bool ShouldSkipRequestHeader(string headerName) =>
        string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipResponseHeader(string headerName) =>
        string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
}
