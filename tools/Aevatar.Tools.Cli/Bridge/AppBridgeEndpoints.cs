using System.Text.Json;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;

namespace Aevatar.Tools.Cli.Bridge;

internal sealed class AppBridgeRouteOptions
{
    public bool MapCapabilityRoutes { get; init; }
    public bool MapAppAliases { get; init; } = true;
}

internal static class AppBridgeEndpoints
{
    public static void Map(IEndpointRouteBuilder app, AppBridgeRouteOptions? options = null)
    {
        options ??= new AppBridgeRouteOptions();

        if (options.MapCapabilityRoutes)
        {
            app.MapPost("/api/chat", HandleChatStreamAsync);
            app.MapPost("/api/workflows/resume", HandleResumeAsync);
            app.MapPost("/api/workflows/signal", HandleSignalAsync);
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
