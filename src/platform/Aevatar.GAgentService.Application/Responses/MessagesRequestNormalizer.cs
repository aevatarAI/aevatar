namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Application normalized Anthropic Messages by parsing raw JsonElement protocol bodies.
//   New principle: Host converts Anthropic JSON into typed chat/tool inputs; Application validates command semantics and builds the normalized session request.
public static class MessagesRequestNormalizer
{
    public static MessagesRequestNormalizationResult Normalize(
        MessagesCommandRequest request,
        string? defaultModel = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = defaultModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return MessagesRequestNormalizationResult.Failed("model_required", "model is required.");

        if (request.MaxTokens is null or <= 0)
        {
            return MessagesRequestNormalizationResult.Failed(
                "invalid_max_tokens",
                "max_tokens must be a positive integer.");
        }

        if (request.Temperature is < 0 or > 2)
        {
            return MessagesRequestNormalizationResult.Failed(
                "invalid_temperature",
                "temperature must be between 0 and 2.");
        }

        if (request.TopP.HasValue)
        {
            return MessagesRequestNormalizationResult.Failed(
                "unsupported_parameter",
                "top_p is not supported by this /v1/messages facade.");
        }

        if (request.TopK.HasValue)
        {
            return MessagesRequestNormalizationResult.Failed(
                "unsupported_parameter",
                "top_k is not supported by this /v1/messages facade.");
        }

        if (request.StopSequences is { Count: > 0 })
        {
            return MessagesRequestNormalizationResult.Failed(
                "unsupported_parameter",
                "stop_sequences is not supported by this /v1/messages facade.");
        }

        if (!string.IsNullOrWhiteSpace(request.ToolChoiceError))
            return MessagesRequestNormalizationResult.Failed("unsupported_parameter", request.ToolChoiceError);

        var chatMessages = request.ChatMessages
            .Where(static message =>
                !string.IsNullOrWhiteSpace(message.Role) &&
                (!string.IsNullOrWhiteSpace(message.Content) ||
                 !string.IsNullOrWhiteSpace(message.ReasoningContent) ||
                 message.ToolCalls is { Count: > 0 } ||
                 !string.IsNullOrWhiteSpace(message.ToolCallId)))
            .ToArray();
        if (chatMessages.Length == 0)
            return MessagesRequestNormalizationResult.Failed("invalid_messages", "messages must contain at least one entry.");

        var declaredTools = request.ToolChoiceDisablesTools
            ? []
            : request.DeclaredTools;
        return MessagesRequestNormalizationResult.Success(new NormalizedMessagesRequest(
            MessageId: $"msg_{Guid.NewGuid():N}",
            Model: model,
            MaxTokens: request.MaxTokens.Value,
            Stream: request.Stream ?? false,
            Temperature: request.Temperature,
            ChatMessages: chatMessages,
            DeclaredTools: declaredTools,
            DroppedImageContent: request.DroppedImageContent));
    }
}
