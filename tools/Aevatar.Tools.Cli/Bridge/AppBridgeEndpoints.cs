using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Bridge;

internal sealed class AppBridgeRouteOptions
{
    public bool MapCapabilityRoutes { get; init; }
    public bool MapAppAliases { get; init; } = true;
}

internal static class AppBridgeEndpoints
{
    private const string BackendClientName = "AppBridgeBackend";

    public static void Map(IEndpointRouteBuilder app, AppBridgeRouteOptions? options = null)
    {
        options ??= new AppBridgeRouteOptions();

        if (options.MapCapabilityRoutes)
        {
            app.MapPost("/api/chat", HandleChatStreamAsync);
            app.MapPost("/api/workflows/resume", HandleResumeAsync);
            app.MapPost("/api/workflows/signal", HandleSignalAsync);
            app.MapGet("/api/scopes/{scopeId}/workflows", HandleBackendProxyAsync);
            app.MapGet("/api/scopes/{scopeId}/workflows/{workflowId}", HandleBackendProxyAsync);
            app.MapPut("/api/scopes/{scopeId}/workflows/{workflowId}", HandleBackendProxyAsync);
            app.MapPost("/api/scopes/{scopeId}/workflows/{workflowId}/runs:stream", HandleBackendProxyAsync);
            app.MapPost("/api/scopes/{scopeId}/workflow-runs:stream", HandleBackendProxyAsync);
        }

        if (options.MapAppAliases)
        {
            app.MapPost("/api/app/chat", HandleChatStreamAsync);
            app.MapPost("/api/app/resume", HandleResumeAsync);
            app.MapPost("/api/app/signal", HandleSignalAsync);
        }
    }

    private static async Task<IResult> HandleResumeAsync(
        WorkflowResumeRequest request,
        IAevatarWorkflowClient client,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActorId) ||
            string.IsNullOrWhiteSpace(request.RunId) ||
            string.IsNullOrWhiteSpace(request.StepId))
        {
            return Results.BadRequest(new { code = "INVALID_REQUEST", message = "actorId/runId/stepId are required." });
        }

        try
        {
            var response = await client.ResumeAsync(request, cancellationToken);
            return Results.Json(response);
        }
        catch (AevatarWorkflowException ex)
        {
            return Results.BadRequest(new { code = ex.Kind.ToString(), message = ex.Message });
        }
    }

    private static async Task<IResult> HandleSignalAsync(
        WorkflowSignalRequest request,
        IAevatarWorkflowClient client,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActorId) ||
            string.IsNullOrWhiteSpace(request.RunId) ||
            string.IsNullOrWhiteSpace(request.SignalName))
        {
            return Results.BadRequest(new { code = "INVALID_REQUEST", message = "actorId/runId/signalName are required." });
        }

        try
        {
            var response = await client.SignalAsync(request, cancellationToken);
            return Results.Json(response);
        }
        catch (AevatarWorkflowException ex)
        {
            return Results.BadRequest(new { code = ex.Kind.ToString(), message = ex.Message });
        }
    }

    private static async Task HandleChatStreamAsync(
        HttpContext context,
        IAevatarWorkflowClient client,
        CancellationToken cancellationToken)
    {
        ChatRunRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ChatRunRequest>(
                context.Request.Body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
                cancellationToken);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "INVALID_REQUEST", message = ex.Message }, cancellationToken);
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "INVALID_REQUEST", message = "prompt is required." }, cancellationToken);
            return;
        }

        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        var serializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        try
        {
            await foreach (var evt in client.StartRunStreamAsync(request, cancellationToken))
            {
                var frameJson = JsonSerializer.Serialize(evt.Frame, serializerOptions);
                await context.Response.WriteAsync($"data: {frameJson}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AevatarWorkflowException ex)
        {
            await WriteRunErrorFrameAsync(context, serializerOptions, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteRunErrorFrameAsync(context, serializerOptions, ex.Message, cancellationToken);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
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

    private static async Task WriteRunErrorFrameAsync(
        HttpContext context,
        JsonSerializerOptions serializerOptions,
        string message,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var frame = new WorkflowOutputFrame
        {
            Type = WorkflowEventTypes.RunError,
            Message = message,
            Code = "APP_BRIDGE_ERROR",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var json = JsonSerializer.Serialize(frame, serializerOptions);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
