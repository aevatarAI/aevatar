using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgentService.Application.Responses;

public static class MessagesRequestNormalizer
{
    private const int MaxToolDescriptionLength = 4_096;

    public static MessagesRequestNormalizationResult Normalize(MessagesCommandRequest request)
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

        if (!TryNormalizeToolChoice(request.ToolChoice, out var toolChoiceDisablesTools, out var toolChoiceError))
            return MessagesRequestNormalizationResult.Failed("unsupported_parameter", toolChoiceError ?? "tool_choice is not supported.");

        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return MessagesRequestNormalizationResult.Failed("invalid_tools", toolsError ?? "tools is invalid.");

        if (toolChoiceDisablesTools)
            declaredTools = [];

        if (!TryExtractChatMessages(request.System, request.Messages, out var chatMessages, out var droppedImages, out var messagesError))
            return MessagesRequestNormalizationResult.Failed("invalid_messages", messagesError ?? "messages is invalid.");

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

        var textBuffer = new StringBuilder();
        var reasoningBuffer = new StringBuilder();
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
                    if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        if (textBuffer.Length > 0) textBuffer.Append('\n');
                        textBuffer.Append(t.GetString());
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
        return true;
    }

    private static bool TryCollectThinking(
        string role,
        JsonElement block,
        StringBuilder reasoningBuffer,
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

        var callId = block.TryGetProperty("tool_use_id", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            error = "tool_result.tool_use_id is required.";
            return false;
        }

        var output = string.Empty;
        if (block.TryGetProperty("content", out var rc))
        {
            output = rc.ValueKind switch
            {
                JsonValueKind.String => rc.GetString() ?? string.Empty,
                JsonValueKind.Array => FlattenToolResultArray(rc),
                _ => rc.GetRawText(),
            };
        }
        toolResults.Add((callId, output));
        return true;
    }

    private static void AddCollectedContent(
        string role,
        ICollection<ChatMessage> collected,
        StringBuilder textBuffer,
        StringBuilder reasoningBuffer,
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
        {
            type = toolChoice.GetString();
        }
        else if (toolChoice.ValueKind == JsonValueKind.Object &&
                 toolChoice.TryGetProperty("type", out var typeEl) &&
                 typeEl.ValueKind == JsonValueKind.String)
        {
            type = typeEl.GetString();
        }

        if (type == "auto")
            return true;
        if (type == "none")
        {
            disablesTools = true;
            return true;
        }

        if (type is "any" or "tool")
            error = $"tool_choice '{type}' requires provider-level forcing and is not supported by this /v1/messages facade.";
        else
            error = "tool_choice must be one of auto, none, any, or tool.";
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
        var index = -1;
        foreach (var tool in tools.EnumerateArray())
        {
            index++;
            if (tool.ValueKind != JsonValueKind.Object)
            {
                error = $"tool at index {index} must be an object.";
                return false;
            }

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
        var bytes = Encoding.UTF8.GetBytes($"{name.Trim()}|{schemaJson}");
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
    }
}
