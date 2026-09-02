using System.Text.Json;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Aevatar.Mainnet.Host.Api.Chat;

internal enum MainnetChatRequestKind
{
    Workflow,
    Assistant,
    Unsupported,
}

internal sealed record MainnetChatRequestClassification(
    MainnetChatRequestKind Kind,
    JsonElement? Body = null);

public static class MainnetChatEndpoints
{
    private static readonly HashSet<string> AssistantTypes = new(StringComparer.Ordinal)
    {
        "text",
        "action.continue",
        "approval.resolve",
        "input.resolve",
        "task.stop",
        "task.steer",
        "step.retry",
        "step.skip",
    };

    public static IEndpointRouteBuilder MapMainnetChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", HandlePostAsync)
            .WithTags("Chat")
            .WithName("StartMainnetChat");
        return app;
    }

    internal static async Task<MainnetChatRequestClassification> ClassifyRequestAsync(
        HttpRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HasFormContentType)
            return new MainnetChatRequestClassification(MainnetChatRequestKind.Workflow);
        if (!IsJson(request.ContentType))
            return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);

            var body = document.RootElement.Clone();
            if (!body.TryGetProperty("type", out var type))
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Workflow);
            if (type.ValueKind != JsonValueKind.String ||
                !AssistantTypes.Contains(type.GetString()?.Trim() ?? string.Empty))
            {
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);
            }

            return new MainnetChatRequestClassification(MainnetChatRequestKind.Assistant, body);
        }
        catch (JsonException)
        {
            return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);
        }
        finally
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
        }
    }

    private static async Task HandlePostAsync(HttpContext http, CancellationToken ct)
    {
        var classification = await ClassifyRequestAsync(http.Request, ct);
        switch (classification.Kind)
        {
            case MainnetChatRequestKind.Workflow:
                await WorkflowCapabilityEndpoints.HandleChatPostAsync(http, ct);
                return;
            case MainnetChatRequestKind.Assistant:
                await NyxIdChatEndpoints.HandlePublicChatAsync(http, classification.Body!.Value, ct);
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "INVALID_CHAT_INPUT",
                    message = "The chat request body or Assistant type is invalid.",
                }, cancellationToken: ct);
                return;
        }
    }

    private static bool IsJson(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) &&
        parsed.MediaType.Value is { } mediaType &&
        (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
