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

internal readonly record struct MessagesProtocolMappingResult(
    MessagesCommandRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static MessagesProtocolMappingResult Success(MessagesCommandRequest request) =>
        new(request, null, null);

    public static MessagesProtocolMappingResult Failed(string code, string message) =>
        new(null, code, message);
}

internal static class MessagesProtocolMapper
{
    private const int MaxToolDescriptionLength = 4_096;

    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Application command contracts accepted Anthropic Messages JsonElement bodies and parsed protocol blocks internally.
    //   New principle: Host maps Anthropic JSON wire blocks into typed chat/tool inputs before Application command orchestration.
    public static MessagesProtocolMappingResult ToCommandRequest(MessagesCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryNormalizeToolChoice(request.ToolChoice, out var toolChoiceDisablesTools, out var toolChoiceError))
        {
            return MessagesProtocolMappingResult.Success(new MessagesCommandRequest(
                request.Model,
                request.MaxTokens,
                [],
                [],
                false,
                request.Temperature,
                request.TopP,
                request.TopK,
                request.StopSequences,
                request.Stream,
                false,
                toolChoiceError ?? "tool_choice is not supported."));
        }

        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return MessagesProtocolMappingResult.Failed("invalid_tools", toolsError ?? "tools is invalid.");

        if (!TryExtractChatMessages(request.System, request.Messages, out var chatMessages, out var droppedImages, out var messagesError))
            return MessagesProtocolMappingResult.Failed("invalid_messages", messagesError ?? "messages is invalid.");

        return MessagesProtocolMappingResult.Success(new MessagesCommandRequest(
            request.Model,
            request.MaxTokens,
            chatMessages,
            declaredTools,
            droppedImages,
            request.Temperature,
            request.TopP,
            request.TopK,
            request.StopSequences,
            request.Stream,
            toolChoiceDisablesTools,
            null));
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
        var reasoningBuffer = new System.Text.StringBuilder();
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
                    if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        if (textBuffer.Length > 0) textBuffer.Append('\n');
                        textBuffer.Append(text.GetString());
                    }
                    break;

                case "image":
                    droppedImages = true;
                    break;

                case "tool_use":
                    if (!TryCollectToolUse(role, block, toolCalls, out error))
                        return false;
                    break;

                case "thinking":
                    if (!TryCollectThinking(role, block, reasoningBuffer, out error))
                        return false;
                    break;

                case "tool_result":
                    if (!TryCollectToolResult(role, block, toolResults, out error))
                        return false;
                    break;
            }
        }

        AddCollectedContent(role, collected, textBuffer, reasoningBuffer, toolCalls, toolResults);
        return true;
    }

    private static bool TryCollectToolUse(
        string role,
        JsonElement block,
        ICollection<ToolCall> toolCalls,
        out string? error)
    {
        error = null;
        if (role != "assistant")
        {
            error = "tool_use block is only valid in assistant messages.";
            return false;
        }

        var id = block.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;
        var name = block.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? string.Empty
            : string.Empty;
        var input = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            error = "tool_use block requires non-empty id and name.";
            return false;
        }

        toolCalls.Add(new ToolCall { Id = id, Name = name, ArgumentsJson = input });
        return true;
    }

    private static bool TryCollectThinking(
        string role,
        JsonElement block,
        System.Text.StringBuilder reasoningBuffer,
        out string? error)
    {
        error = null;
        if (role != "assistant")
        {
            error = "thinking block is only valid in assistant messages.";
            return false;
        }

        if (block.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
        {
            if (reasoningBuffer.Length > 0) reasoningBuffer.Append('\n');
            reasoningBuffer.Append(thinking.GetString());
        }
        return true;
    }

    private static bool TryCollectToolResult(
        string role,
        JsonElement block,
        ICollection<(string callId, string output)> toolResults,
        out string? error)
    {
        error = null;
        if (role != "user")
        {
            error = "tool_result block is only valid in user messages.";
            return false;
        }

        var callId = block.TryGetProperty("tool_use_id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String
            ? callIdEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            error = "tool_result.tool_use_id is required.";
            return false;
        }

        var output = string.Empty;
        if (block.TryGetProperty("content", out var content))
        {
            output = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => FlattenToolResultArray(content),
                _ => content.GetRawText(),
            };
        }
        toolResults.Add((callId, output));
        return true;
    }

    private static void AddCollectedContent(
        string role,
        ICollection<ChatMessage> collected,
        System.Text.StringBuilder textBuffer,
        System.Text.StringBuilder reasoningBuffer,
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<(string callId, string output)> toolResults)
    {
        if (role == "assistant")
        {
            var text = textBuffer.ToString();
            var reasoning = reasoningBuffer.Length > 0 ? reasoningBuffer.ToString() : null;
            if (toolCalls.Count > 0)
            {
                collected.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = text,
                    ReasoningContent = reasoning,
                    ToolCalls = toolCalls,
                });
            }
            else if (text.Length > 0 || !string.IsNullOrEmpty(reasoning))
            {
                collected.Add(ChatMessage.Assistant(text, reasoning));
            }
            return;
        }

        foreach (var (callId, output) in toolResults)
            collected.Add(ChatMessage.Tool(callId, output));

        var userText = textBuffer.ToString();
        if (userText.Length > 0)
            collected.Add(ChatMessage.User(userText));
    }

    private static string FlattenToolResultArray(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty("text", out var text) &&
                     text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
            else
            {
                parts.Add(item.GetRawText());
            }
        }
        return string.Join("\n", parts.Where(static x => !string.IsNullOrEmpty(x)));
    }

    private static string ExtractSystemText(JsonElement system)
    {
        if (system.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return string.Empty;
        if (system.ValueKind == JsonValueKind.String)
            return system.GetString() ?? string.Empty;
        if (system.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var block in system.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                parts.Add(block.GetString() ?? string.Empty);
                continue;
            }
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }
        return string.Join("\n", parts.Where(static x => !string.IsNullOrWhiteSpace(x)));
    }

    private static bool TryNormalizeToolChoice(
        JsonElement toolChoice,
        out bool disablesTools,
        out string? error)
    {
        disablesTools = false;
        error = null;
        if (toolChoice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        string? type = null;
        if (toolChoice.ValueKind == JsonValueKind.String)
            type = toolChoice.GetString();
        else if (toolChoice.ValueKind == JsonValueKind.Object &&
                 toolChoice.TryGetProperty("type", out var typeEl) &&
                 typeEl.ValueKind == JsonValueKind.String)
            type = typeEl.GetString();

        if (type == "auto")
            return true;
        if (type == "none")
        {
            disablesTools = true;
            return true;
        }

        error = type is "any" or "tool"
            ? $"tool_choice '{type}' requires provider-level forcing and is not supported by this /v1/messages facade."
            : "tool_choice must be one of auto, none, any, or tool.";
        return false;
    }

    private static bool TryExtractDeclaredTools(
        JsonElement tools,
        out IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        out string? error)
    {
        declaredTools = [];
        error = null;
        if (tools.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (tools.ValueKind != JsonValueKind.Array)
        {
            error = "tools must be an array when provided.";
            return false;
        }

        var result = new List<ResponsesApplicationToolDeclaration>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                error = "tool must be an object.";
                return false;
            }

            string? name;
            string? description;
            JsonElement schema = default;
            if (tool.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                name = nameEl.GetString();
                description = tool.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString()
                    : null;
                if (tool.TryGetProperty("input_schema", out var inputSchema))
                    schema = inputSchema;
            }
            else if (tool.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = function.TryGetProperty("name", out var functionName) && functionName.ValueKind == JsonValueKind.String
                    ? functionName.GetString()
                    : null;
                description = function.TryGetProperty("description", out var functionDesc) && functionDesc.ValueKind == JsonValueKind.String
                    ? functionDesc.GetString()
                    : null;
                if (function.TryGetProperty("parameters", out var parameters))
                    schema = parameters;
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

            var parametersJson = schema.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : schema.GetRawText();
            result.Add(new ResponsesApplicationToolDeclaration(
                name.Trim(),
                trimmedDescription,
                parametersJson,
                ComputeSchemaHash(name, parametersJson)));
        }

        declaredTools = result;
        return true;
    }

    private static string ComputeSchemaHash(string name, string schemaJson)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{name.Trim()}|{schemaJson}");
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
    }
}
