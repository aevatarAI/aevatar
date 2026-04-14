using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.MockNyxId.Endpoints;

public static class LlmGatewayEndpoints
{
    public static IEndpointRouteBuilder MapLlmGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        // OpenAI-compatible chat completions
        app.MapPost("/api/v1/llm/gateway/v1/chat/completions", HandleChatCompletions).WithTags("LLM");

        // Also handle proxy-routed LLM (when NyxIdLLMProvider routes via proxy slug)
        app.MapPost("/api/v1/proxy/s/{slug}/v1/chat/completions", HandleChatCompletions).WithTags("LLM");

        return app;
    }

    private static async Task HandleChatCompletions(
        HttpContext http,
        [FromServices] MockNyxIdOptions options)
    {
        if (AuthEndpoints.ExtractBearer(http) is null)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsJsonAsync(new { error = new { message = "Unauthorized" } });
            return;
        }

        // Parse request to detect streaming
        using var doc = await JsonDocument.ParseAsync(http.Request.Body);
        var root = doc.RootElement;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? options.LlmModel : options.LlmModel;
        var stream = root.TryGetProperty("stream", out var s) && s.GetBoolean();

        if (options.LlmResponseDelayMs > 0)
            await Task.Delay(options.LlmResponseDelayMs);

        var responseId = $"chatcmpl-mock-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (stream)
        {
            await WriteStreamingResponse(http, options.LlmResponseText, responseId, model, created);
        }
        else
        {
            await WriteNonStreamingResponse(http, options.LlmResponseText, responseId, model, created);
        }
    }

    private static async Task WriteNonStreamingResponse(
        HttpContext http, string text, string id, string model, long created)
    {
        var response = new
        {
            id,
            @object = "chat.completion",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = 10,
                completion_tokens = text.Split(' ').Length,
                total_tokens = 10 + text.Split(' ').Length,
            },
        };

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(response);
    }

    private static async Task WriteStreamingResponse(
        HttpContext http, string text, string id, string model, long created)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";

        var words = text.Split(' ');

        for (var i = 0; i < words.Length; i++)
        {
            var content = i == 0 ? words[i] : " " + words[i];
            var chunk = new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content },
                        finish_reason = (string?)null,
                    },
                },
            };

            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await http.Response.Body.FlushAsync();
        }

        // Final chunk with finish_reason
        var finalChunk = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content = (string?)null },
                    finish_reason = "stop",
                },
            },
        };
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(finalChunk)}\n\n");
        await http.Response.WriteAsync("data: [DONE]\n\n");
        await http.Response.Body.FlushAsync();
    }
}
