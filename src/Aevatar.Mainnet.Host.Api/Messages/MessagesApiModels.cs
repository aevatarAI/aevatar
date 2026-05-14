using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Messages;

// ---- Anthropic Messages wire DTO --------------------------------------------------
//
// Surface mirrors anthropic.com/claude/reference/messages_post. Path B is a
// stateless facade: every POST /v1/messages opens + closes its own LlmSession,
// no previous_response_id equivalent (Messages protocol has no native one).

internal sealed record MessagesCreateRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("system")]
    public JsonElement System { get; init; }

    [JsonPropertyName("messages")]
    public JsonElement Messages { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("stop_sequences")]
    public IReadOnlyList<string>? StopSequences { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; init; }
}

internal sealed record NormalizedMessagesRequest(
    string MessageId,
    string Model,
    int MaxTokens,
    bool Stream,
    double? Temperature,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    bool DroppedImageContent);

internal readonly record struct MessagesRequestNormalizationResult(
    NormalizedMessagesRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static MessagesRequestNormalizationResult Success(NormalizedMessagesRequest request) =>
        new(request, null, null);

    public static MessagesRequestNormalizationResult Failed(string code, string message) =>
        new(null, code, message);
}

internal static class MessagesRequestNormalizer
{
    // Anthropic Messages requires max_tokens. OpenAI / Aevatar intermediate model
    // treats it as optional. We surface that constraint here so the LLM provider
    // never receives a null when the client speaks Messages.
    private const int MaxToolDescriptionLength = 4_096;

    public static MessagesRequestNormalizationResult Normalize(MessagesCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
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

        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return MessagesRequestNormalizationResult.Failed("invalid_tools", toolsError);

        if (!TryExtractChatMessages(request.System, request.Messages, out var chatMessages, out var droppedImages, out var messagesError))
            return MessagesRequestNormalizationResult.Failed("invalid_messages", messagesError);

        var normalized = new NormalizedMessagesRequest(
            MessageId: $"msg_{Guid.NewGuid():N}",
            Model: model,
            MaxTokens: request.MaxTokens.Value,
            Stream: request.Stream ?? false,
            Temperature: request.Temperature,
            ChatMessages: chatMessages,
            DeclaredTools: declaredTools,
            DroppedImageContent: droppedImages);

        return MessagesRequestNormalizationResult.Success(normalized);
    }

    private static bool TryExtractChatMessages(
        JsonElement system,
        JsonElement messages,
        out IReadOnlyList<ChatMessage> result,
        out bool droppedImages,
        out string? error)
    {
        result = [];
        droppedImages = false;
        error = null;
        var collected = new List<ChatMessage>();

        var systemText = ExtractSystemText(system);
        if (!string.IsNullOrEmpty(systemText))
            collected.Add(ChatMessage.System(systemText));

        if (messages.ValueKind != JsonValueKind.Array)
        {
            error = "messages must be an array.";
            return false;
        }

        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                error = "each message must be a JSON object.";
                return false;
            }

            var role = msg.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString()
                : null;
            if (role is not ("user" or "assistant"))
            {
                error = $"unsupported message role '{role}'.";
                return false;
            }

            if (!msg.TryGetProperty("content", out var contentEl))
            {
                error = "message.content is required.";
                return false;
            }

            if (!TryFlattenContent(contentEl, role!, collected, ref droppedImages, out error))
                return false;
        }

        if (collected.Count == 0)
        {
            error = "messages must contain at least one entry.";
            return false;
        }

        result = collected;
        return true;
    }

    private static bool TryFlattenContent(
        JsonElement content,
        string role,
        List<ChatMessage> collected,
        ref bool droppedImages,
        out string? error)
    {
        error = null;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? string.Empty;
            collected.Add(role == "user" ? ChatMessage.User(text) : ChatMessage.Assistant(text));
            return true;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            error = "message.content must be a string or an array of content blocks.";
            return false;
        }

        var textBuffer = new System.Text.StringBuilder();
        var toolCalls = new List<ToolCall>();
        var toolResults = new List<(string callId, string output)>();

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                error = "content block must be an object.";
                return false;
            }

            var type = block.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;
            switch (type)
            {
                case "text":
                {
                    if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        if (textBuffer.Length > 0) textBuffer.Append('\n');
                        textBuffer.Append(t.GetString());
                    }
                    break;
                }
                case "image":
                {
                    // Lossy: Anthropic image blocks can't round-trip through the
                    // OpenAI-Chat intermediate without provider-side image_url
                    // support. v1 drops them and surfaces a single warning per
                    // response in the response metadata.
                    droppedImages = true;
                    break;
                }
                case "tool_use":
                {
                    if (role != "assistant")
                    {
                        error = "tool_use block is only valid in assistant messages.";
                        return false;
                    }
                    var id = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? string.Empty
                        : string.Empty;
                    var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? string.Empty
                        : string.Empty;
                    var input = block.TryGetProperty("input", out var i) ? i.GetRawText() : "{}";
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        error = "tool_use block requires non-empty id and name.";
                        return false;
                    }
                    toolCalls.Add(new ToolCall { Id = id, Name = name, ArgumentsJson = input });
                    break;
                }
                case "tool_result":
                {
                    if (role != "user")
                    {
                        error = "tool_result block is only valid in user messages.";
                        return false;
                    }
                    var callId = block.TryGetProperty("tool_use_id", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(callId))
                    {
                        error = "tool_result.tool_use_id is required.";
                        return false;
                    }
                    string output;
                    if (block.TryGetProperty("content", out var rc))
                    {
                        output = rc.ValueKind switch
                        {
                            JsonValueKind.String => rc.GetString() ?? string.Empty,
                            JsonValueKind.Array => FlattenToolResultArray(rc),
                            _ => rc.GetRawText(),
                        };
                    }
                    else
                    {
                        output = string.Empty;
                    }
                    toolResults.Add((callId, output));
                    break;
                }
                default:
                {
                    // Unknown block types are dropped with a single warning per response.
                    // This stays consistent with Anthropic's own forward-compat behavior.
                    break;
                }
            }
        }

        if (role == "assistant")
        {
            var text = textBuffer.ToString();
            if (toolCalls.Count > 0)
            {
                collected.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = text,
                    ToolCalls = toolCalls,
                });
            }
            else if (text.Length > 0)
            {
                collected.Add(ChatMessage.Assistant(text));
            }
        }
        else
        {
            // user message: tool_result blocks become role=tool messages so the
            // upstream LLM provider can replay them in OpenAI-Chat shape.
            foreach (var (callId, output) in toolResults)
                collected.Add(ChatMessage.Tool(callId, output));

            var text = textBuffer.ToString();
            if (text.Length > 0)
                collected.Add(ChatMessage.User(text));
        }

        return true;
    }

    private static string FlattenToolResultArray(JsonElement array)
    {
        // Anthropic allows tool_result.content to be either a string or an array
        // of text/image blocks. Image inside a tool_result is also lossy here.
        var sb = new System.Text.StringBuilder();
        foreach (var block in array.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                typeEl.GetString() == "text" &&
                block.TryGetProperty("text", out var textEl) &&
                textEl.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(textEl.GetString());
            }
        }
        return sb.ToString();
    }

    private static string ExtractSystemText(JsonElement system)
    {
        if (system.ValueKind == JsonValueKind.String)
            return system.GetString() ?? string.Empty;

        if (system.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var block in system.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (block.TryGetProperty("type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    typeEl.GetString() == "text" &&
                    block.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(textEl.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static bool TryExtractDeclaredTools(
        JsonElement tools,
        out IReadOnlyList<ResponsesApplicationToolDeclaration> result,
        out string? error)
    {
        result = [];
        error = null;
        if (tools.ValueKind != JsonValueKind.Array)
            return true;

        var collected = new List<ResponsesApplicationToolDeclaration>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;

            // Anthropic tools have name/description/input_schema; OpenAI tools have
            // function.{name,description,parameters}. We accept either so clients
            // that proxy OpenAI tool decls through Messages still work.
            string? name;
            string? description;
            JsonElement schema = default;
            if (tool.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                name = nameEl.GetString();
                description = tool.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                if (tool.TryGetProperty("input_schema", out var s))
                    schema = s;
            }
            else if (tool.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                name = fn.TryGetProperty("name", out var fnName) && fnName.ValueKind == JsonValueKind.String
                    ? fnName.GetString()
                    : null;
                description = fn.TryGetProperty("description", out var fnDesc) && fnDesc.ValueKind == JsonValueKind.String
                    ? fnDesc.GetString()
                    : null;
                if (fn.TryGetProperty("parameters", out var p))
                    schema = p;
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "tool.name is required.";
                return false;
            }

            var trimmedDescription = description ?? string.Empty;
            if (trimmedDescription.Length > MaxToolDescriptionLength)
                trimmedDescription = trimmedDescription[..MaxToolDescriptionLength];

            var schemaJson = schema.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : schema.GetRawText();

            collected.Add(new ResponsesApplicationToolDeclaration(
                Name: name!.Trim(),
                Description: trimmedDescription,
                ParametersJson: schemaJson,
                SchemaHash: ComputeSchemaHash(name!, schemaJson)));
        }

        result = collected;
        return true;
    }

    private static string ComputeSchemaHash(string name, string schemaJson)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{name.Trim()}|{schemaJson}");
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
    }
}
