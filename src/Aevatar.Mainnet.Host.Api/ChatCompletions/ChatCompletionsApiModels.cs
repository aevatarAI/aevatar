using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.ChatCompletions;

internal sealed record ChatCompletionsCreateRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("messages")]
    public JsonElement Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("stream_options")]
    public JsonElement StreamOptions { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; init; }

    [JsonPropertyName("response_format")]
    public JsonElement ResponseFormat { get; init; }

    [JsonPropertyName("n")]
    public int? N { get; init; }
}

// Refactor (iter344/cluster-001):
//   Old pattern: Host validation locals were mixed with caller resolution, route/session setup, and direct provider execution.
//   New principle: Host protocol mapping returns a typed command request or protocol error; Application owns command lifecycle decisions.
internal readonly record struct ChatCompletionsProtocolMappingResult(
    ChatCompletionsCommandRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static ChatCompletionsProtocolMappingResult Success(ChatCompletionsCommandRequest request) =>
        new(request, null, null);

    public static ChatCompletionsProtocolMappingResult Failed(string code, string message) =>
        new(null, code, message);
}

internal static class ChatCompletionsProtocolMapper
{
    private const int MaxToolDescriptionLength = 4_096;

    // Refactor (iter344/cluster-001):
    //   Old pattern: Host handler owns caller resolution, route resolution, session registration, tool planning, direct provider execution, status updates, and protocol rendering in one request stack.
    //   New principle: Host maps HTTP/OpenAI frames only; typed Application facade owns Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt/Observe via the same LlmSessionGAgent run path as Responses/Messages.
    public static ChatCompletionsProtocolMappingResult ToCommandRequest(ChatCompletionsCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxTokens = request.MaxCompletionTokens ?? request.MaxTokens;

        if (request.N is not null and not 1)
        {
            return ChatCompletionsProtocolMappingResult.Failed(
                "unsupported_parameter",
                "n must be 1 when provided.");
        }

        if (!TryNormalizeToolChoice(request.ToolChoice, out var toolChoiceDisablesTools, out var toolChoiceError))
            return ChatCompletionsProtocolMappingResult.Failed("unsupported_parameter", toolChoiceError ?? "tool_choice is not supported.");

        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return ChatCompletionsProtocolMappingResult.Failed("invalid_tools", toolsError ?? "tools is invalid.");

        if (toolChoiceDisablesTools)
            declaredTools = [];

        if (!TryExtractChatMessages(request.Messages, out var messages, out var messagesError))
            return ChatCompletionsProtocolMappingResult.Failed("invalid_messages", messagesError ?? "messages is invalid.");

        if (!TryExtractResponseFormat(request.ResponseFormat, out var responseFormat, out var responseFormatError))
            return ChatCompletionsProtocolMappingResult.Failed("unsupported_parameter", responseFormatError ?? "response_format is invalid.");

        return ChatCompletionsProtocolMappingResult.Success(new ChatCompletionsCommandRequest(
            request.Model,
            request.Stream,
            ExtractIncludeUsage(request.StreamOptions),
            request.Temperature,
            maxTokens,
            messages,
            declaredTools,
            responseFormat));
    }

    private static bool TryExtractChatMessages(
        JsonElement messages,
        out IReadOnlyList<ChatMessage> result,
        [NotNullWhen(false)] out string? error)
    {
        result = [];
        error = null;

        if (messages.ValueKind != JsonValueKind.Array)
        {
            error = "messages must be an array.";
            return false;
        }

        var collected = new List<ChatMessage>();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                error = "each message must be a JSON object.";
                return false;
            }

            var role = GetStringProperty(message, "role");
            if (string.IsNullOrWhiteSpace(role))
            {
                error = "message.role is required.";
                return false;
            }

            switch (role)
            {
                case "system":
                case "developer":
                    collected.Add(ChatMessage.System(ExtractTextContent(message)));
                    break;
                case "user":
                    collected.Add(BuildUserMessage(message));
                    break;
                case "assistant":
                    if (!TryBuildAssistantMessage(message, out var assistant, out error))
                        return false;
                    collected.Add(assistant);
                    break;
                case "tool":
                    if (!TryBuildToolMessage(message, out var tool, out error))
                        return false;
                    collected.Add(tool);
                    break;
                default:
                    error = $"unsupported message role '{role}'.";
                    return false;
            }
        }

        if (collected.Count == 0)
        {
            error = "messages must contain at least one entry.";
            return false;
        }

        result = collected;
        return true;
    }

    private static ChatMessage BuildUserMessage(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return ChatMessage.User(string.Empty);

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<ContentPart>();
            var textParts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (!TryExtractContentPart(block, out var part) || part == null)
                    continue;

                parts.Add(part);
                if (part.Kind == ContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                    textParts.Add(part.Text);
            }

            if (parts.Count > 0)
                return ChatMessage.User(parts, string.Join("\n", textParts));
        }

        return ChatMessage.User(ExtractTextContent(message));
    }

    private static bool TryBuildAssistantMessage(
        JsonElement message,
        out ChatMessage result,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        var content = ExtractTextContent(message);
        var toolCalls = new List<ToolCall>();

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (!TryExtractToolCall(call, out var toolCall, out error))
                {
                    result = null!;
                    return false;
                }

                toolCalls.Add(toolCall);
            }
        }

        result = toolCalls.Count > 0
            ? new ChatMessage { Role = "assistant", Content = content, ToolCalls = toolCalls }
            : ChatMessage.Assistant(content);
        return true;
    }

    private static bool TryBuildToolMessage(
        JsonElement message,
        out ChatMessage result,
        [NotNullWhen(false)] out string? error)
    {
        var callId = GetStringProperty(message, "tool_call_id");
        if (string.IsNullOrWhiteSpace(callId))
        {
            error = "tool message requires tool_call_id.";
            result = null!;
            return false;
        }

        error = null;
        result = ChatMessage.Tool(callId, ExtractTextContent(message));
        return true;
    }

    private static bool TryExtractToolCall(
        JsonElement call,
        out ToolCall result,
        [NotNullWhen(false)] out string? error)
    {
        error = null;
        result = null!;
        if (call.ValueKind != JsonValueKind.Object)
        {
            error = "tool_calls entries must be objects.";
            return false;
        }

        var id = GetStringProperty(call, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "tool_call.id is required.";
            return false;
        }

        if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
        {
            error = "tool_call.function is required.";
            return false;
        }

        var name = GetStringProperty(function, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "tool_call.function.name is required.";
            return false;
        }

        var arguments = GetStringProperty(function, "arguments") ?? "{}";
        result = new ToolCall
        {
            Id = id,
            Name = name,
            ArgumentsJson = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments,
        };
        return true;
    }

    private static string ExtractTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return string.Empty;

        return FlattenContentToText(content);
    }

    private static string FlattenContentToText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Null || content.ValueKind == JsonValueKind.Undefined)
            return string.Empty;

        if (content.ValueKind != JsonValueKind.Array)
            return content.GetRawText();

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                var value = block.GetString();
                if (!string.IsNullOrEmpty(value))
                    parts.Add(value);
                continue;
            }

            if (block.ValueKind != JsonValueKind.Object)
                continue;

            var type = GetStringProperty(block, "type");
            if ((type == "text" || type == "input_text") &&
                block.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return string.Join("\n", parts.Where(static part => part.Length > 0));
    }

    private static bool TryExtractContentPart(JsonElement block, out ContentPart? part)
    {
        part = null;
        if (block.ValueKind != JsonValueKind.Object)
            return false;

        var type = GetStringProperty(block, "type");
        if (type is "text" or "input_text")
        {
            var text = GetStringProperty(block, "text");
            if (string.IsNullOrEmpty(text))
                return false;

            part = ContentPart.TextPart(text);
            return true;
        }

        if (type == "image_url" &&
            block.TryGetProperty("image_url", out var imageUrl) &&
            imageUrl.ValueKind == JsonValueKind.Object)
        {
            var url = GetStringProperty(imageUrl, "url");
            if (string.IsNullOrWhiteSpace(url))
                return false;

            part = ContentPart.ImageUriPart(url);
            return true;
        }

        return false;
    }

    private static bool TryExtractDeclaredTools(
        JsonElement tools,
        out IReadOnlyList<ResponsesApplicationToolDeclaration> result,
        [NotNullWhen(false)] out string? error)
    {
        result = [];
        error = null;
        if (tools.ValueKind != JsonValueKind.Array)
            return true;

        var collected = new List<ResponsesApplicationToolDeclaration>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                continue;

            var type = GetStringProperty(tool, "type");
            if (!string.Equals(type, "function", StringComparison.Ordinal))
                continue;

            if (!tool.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
            {
                error = "function tool requires function object.";
                return false;
            }

            var name = GetStringProperty(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "function.name is required.";
                return false;
            }

            var description = GetStringProperty(function, "description") ?? string.Empty;
            if (description.Length > MaxToolDescriptionLength)
                description = description[..MaxToolDescriptionLength];

            var parametersJson = function.TryGetProperty("parameters", out var parameters)
                ? parameters.GetRawText()
                : "{}";
            collected.Add(new ResponsesApplicationToolDeclaration(
                name.Trim(),
                description,
                parametersJson,
                ComputeSchemaHash(name, parametersJson)));
        }

        result = collected;
        return true;
    }

    private static bool TryNormalizeToolChoice(
        JsonElement toolChoice,
        out bool disablesTools,
        [NotNullWhen(false)] out string? error)
    {
        disablesTools = false;
        error = null;
        if (toolChoice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;

        string? type = null;
        if (toolChoice.ValueKind == JsonValueKind.String)
        {
            type = toolChoice.GetString();
        }
        else if (toolChoice.ValueKind == JsonValueKind.Object &&
                 toolChoice.TryGetProperty("type", out var typeEl) &&
                 typeEl.ValueKind == JsonValueKind.String)
        {
            type = typeEl.GetString();
        }

        switch (type)
        {
            case "auto":
                return true;
            case "none":
                disablesTools = true;
                return true;
            case "required":
            case "function":
                error = $"tool_choice '{type}' requires provider-level forcing and is not supported by this /v1/chat/completions facade.";
                return false;
            default:
                error = "tool_choice must be auto, none, required, or a function choice.";
                return false;
        }
    }

    private static bool TryExtractResponseFormat(
        JsonElement responseFormat,
        out LLMResponseFormat? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;
        if (responseFormat.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;

        if (responseFormat.ValueKind != JsonValueKind.Object)
        {
            error = "response_format must be an object.";
            return false;
        }

        var type = GetStringProperty(responseFormat, "type");
        switch (type)
        {
            case null:
            case "":
            case "text":
                result = LLMResponseFormat.Text;
                return true;
            case "json_object":
                result = LLMResponseFormat.JsonObject;
                return true;
            case "json_schema":
                if (!responseFormat.TryGetProperty("json_schema", out var jsonSchema) ||
                    jsonSchema.ValueKind != JsonValueKind.Object)
                {
                    error = "response_format.json_schema is required.";
                    return false;
                }

                if (!jsonSchema.TryGetProperty("schema", out var schema))
                {
                    error = "response_format.json_schema.schema is required.";
                    return false;
                }

                result = LLMResponseFormat.ForJsonSchema(
                    schema,
                    GetStringProperty(jsonSchema, "name"),
                    GetStringProperty(jsonSchema, "description"));
                return true;
            default:
                error = $"unsupported response_format type '{type}'.";
                return false;
        }
    }

    private static bool ExtractIncludeUsage(JsonElement streamOptions)
    {
        if (streamOptions.ValueKind != JsonValueKind.Object)
            return false;

        return streamOptions.TryGetProperty("include_usage", out var includeUsage) &&
               includeUsage.ValueKind == JsonValueKind.True;
    }

    private static string? GetStringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ComputeSchemaHash(string name, string schemaJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{name.Trim()}|{schemaJson}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

}
