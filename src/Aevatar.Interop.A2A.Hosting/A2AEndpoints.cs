// ─────────────────────────────────────────────────────────────
// A2AEndpoints — HTTP endpoints for the A2A protocol
//
// /.well-known/agent.json — Agent Card discovery
// /a2a — JSON-RPC 2.0 dispatch (tasks/send, tasks/get, tasks/cancel)
// /a2a/subscribe/{taskId} — SSE streaming delivery (tasks/sendSubscribe)
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Interop.A2A.Hosting;

public static class A2AEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapA2AEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/agent.json", HandleAgentCardAsync)
            .WithTags("A2A")
            .Produces<AgentCard>();

        app.MapPost("/a2a", HandleJsonRpcAsync)
            .WithTags("A2A")
            .Accepts<JsonRpcRequest>("application/json")
            .Produces<JsonRpcResponse>();

        app.MapGet("/a2a/subscribe/{taskId}", HandleSubscribeAsync)
            .WithTags("A2A");

        return app;
    }

    private static IResult HandleAgentCardAsync(HttpContext context, IA2AAdapterService adapter)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var card = adapter.GetAgentCard(baseUrl);
        return Results.Json(card, JsonOptions);
    }

    private static async Task<IResult> HandleJsonRpcAsync(
        HttpContext context,
        IA2AAdapterService adapter)
    {
        JsonRpcRequest? rpcRequest;
        try
        {
            rpcRequest = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(
                context.Request.Body, JsonOptions, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.Json(
                JsonRpcResponse.Fail(null, A2AErrorCodes.ParseError, "Parse error"),
                JsonOptions);
        }

        if (rpcRequest == null || string.IsNullOrWhiteSpace(rpcRequest.Method))
        {
            return Results.Json(
                JsonRpcResponse.Fail(null, A2AErrorCodes.InvalidRequest, "Invalid request"),
                JsonOptions);
        }

        try
        {
            var result = rpcRequest.Method switch
            {
                "tasks/send" => await HandleTasksSendAsync(rpcRequest, adapter, context.RequestAborted),
                "tasks/get" => await HandleTasksGetAsync(rpcRequest, adapter, context.RequestAborted),
                "tasks/cancel" => await HandleTasksCancelAsync(rpcRequest, adapter, context.RequestAborted),
                _ => JsonRpcResponse.Fail(rpcRequest.Id, A2AErrorCodes.MethodNotFound,
                    $"Method '{rpcRequest.Method}' not found"),
            };
            return Results.Json(result, JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Json(
                JsonRpcResponse.Fail(rpcRequest.Id, A2AErrorCodes.TaskNotFound, ex.Message),
                JsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                JsonRpcResponse.Fail(rpcRequest.Id, A2AErrorCodes.TaskNotCancelable, ex.Message),
                JsonOptions);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                JsonRpcResponse.Fail(rpcRequest.Id, A2AErrorCodes.InvalidParams, ex.Message),
                JsonOptions);
        }
        catch (Exception ex)
        {
            return Results.Json(
                JsonRpcResponse.Fail(rpcRequest.Id, A2AErrorCodes.InternalError, ex.Message),
                JsonOptions);
        }
    }

    private static async Task<JsonRpcResponse> HandleTasksSendAsync(
        JsonRpcRequest rpc, IA2AAdapterService adapter, CancellationToken ct)
    {
        var sendParams = DeserializeParams<TaskSendParams>(rpc);
        var task = await adapter.SendTaskAsync(sendParams, ct);
        return JsonRpcResponse.Success(rpc.Id, task);
    }

    private static async Task<JsonRpcResponse> HandleTasksGetAsync(
        JsonRpcRequest rpc, IA2AAdapterService adapter, CancellationToken ct)
    {
        var queryParams = DeserializeParams<TaskQueryParams>(rpc);
        var task = await adapter.GetTaskAsync(queryParams, ct);
        if (task == null)
            return JsonRpcResponse.Fail(rpc.Id, A2AErrorCodes.TaskNotFound, $"Task '{queryParams.Id}' not found");
        return JsonRpcResponse.Success(rpc.Id, task);
    }

    private static async Task<JsonRpcResponse> HandleTasksCancelAsync(
        JsonRpcRequest rpc, IA2AAdapterService adapter, CancellationToken ct)
    {
        var idParams = DeserializeParams<TaskIdParams>(rpc);
        var task = await adapter.CancelTaskAsync(idParams, ct);
        return JsonRpcResponse.Success(rpc.Id, task);
    }

    private static async Task HandleSubscribeAsync(
        HttpContext context,
        string taskId,
        IA2AAdapterService adapter,
        IA2ATaskStore taskStore)
    {
        var ct = context.RequestAborted;

        // Verify task exists
        var queryParams = new TaskQueryParams { Id = taskId };
        var task = await adapter.GetTaskAsync(queryParams, ct);
        if (task == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Set SSE headers
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(ct);

        // Send current state as initial event
        await WriteSseEventAsync(context.Response, "status", task.Status, ct);

        // If task is already in terminal state, close the stream
        if (task.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
        {
            await WriteSseEventAsync(context.Response, "close", new { reason = "terminal_state" }, ct);
            return;
        }

        // Subscribe to updates via channel (no polling)
        var reader = taskStore.SubscribeAsync(taskId);
        try
        {
            await foreach (var update in reader.ReadAllAsync(ct))
            {
                await WriteSseEventAsync(context.Response, "status", update.Status, ct);

                if (update.Artifact != null)
                    await WriteSseEventAsync(context.Response, "artifact", update.Artifact, ct);

                if (update.IsFinal)
                {
                    await WriteSseEventAsync(context.Response, "close", new { reason = "terminal_state" }, ct);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
    }

    private static async Task WriteSseEventAsync(HttpResponse response, string eventType, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {json}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    private static T DeserializeParams<T>(JsonRpcRequest rpc)
    {
        if (!rpc.Params.HasValue || rpc.Params.Value.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("Missing required params.");

        return JsonSerializer.Deserialize<T>(rpc.Params.Value.GetRawText(), JsonOptions)
               ?? throw new ArgumentException($"Failed to deserialize params as {typeof(T).Name}.");
    }
}
