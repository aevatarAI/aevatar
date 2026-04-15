// ─────────────────────────────────────────────────────────────
// MEAILLMProvider — LLM provider based on Microsoft.Extensions.AI
//
// Bridges MEAI's IChatClient to Aevatar's ILLMProvider.
// Supports OpenAI / Azure OpenAI / any provider compatible with the OpenAI API
// (DeepSeek, Moonshot, Tongyi Qianwen, etc. configured via baseUrl).
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// ILLMProvider implementation based on the MEAI IChatClient.
/// Supports OpenAI, Azure OpenAI, and any provider compatible with the OpenAI API.
/// </summary>
public sealed class MEAILLMProvider : ILLMProvider
{
    private static readonly LLMProviderCapabilities ProviderCapabilities = new()
    {
        SupportedInputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
            ContentPartKind.Image,
            ContentPartKind.Audio,
            ContentPartKind.Video,
        },
        SupportedOutputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
            ContentPartKind.Image,
            ContentPartKind.Audio,
            ContentPartKind.Video,
        },
        SupportsStreaming = true,
        SupportsToolCalls = true,
        SupportsReasoningDeltas = true,
    };

    private readonly IChatClient _client;
    private readonly ILogger _logger;

    /// <summary>Provider name.</summary>
    public string Name { get; }

    public LLMProviderCapabilities Capabilities => ProviderCapabilities;

    /// <summary>
    /// Creates the MEAI LLM Provider.
    /// </summary>
    /// <param name="name">Provider name (for example "openai", "deepseek").</param>
    /// <param name="client">MEAI IChatClient instance.</param>
    /// <param name="logger">Logger.</param>
    public MEAILLMProvider(string name, IChatClient client, ILogger? logger = null)
    {
        Name = name;
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── ILLMProvider.ChatAsync ───

    /// <summary>Single-turn LLM call. Converts Aevatar's LLMRequest into a list of MEAI ChatMessage instances.</summary>
    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var messages = ConvertMessages(request.Messages);
        var options = BuildOptions(request);

        _logger.LogDebug("MEAI ChatAsync: {MessageCount} messages, model={Model}",
            messages.Count, options?.ModelId);

        var response = await _client.GetResponseAsync(messages, options, ct);

        return ConvertResponse(response);
    }

    // ─── ILLMProvider.ChatStreamAsync ───

    /// <summary>Streaming LLM call. Returns an async-enumerable stream of chunks.</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ConvertMessages(request.Messages);
        var options = BuildOptions(request);

        _logger.LogDebug("MEAI ChatStreamAsync: {MessageCount} messages", messages.Count);
        var emittedStreamChunk = false;
        string? lastFinishReason = null;

        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            if (update.FinishReason != null)
                lastFinishReason = update.FinishReason.Value.ToString();

            var emittedTextFromContents = false;
            if (update.Contents is { Count: > 0 })
            {
                foreach (var part in update.Contents)
                {
                    switch (part)
                    {
                        case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                            emittedTextFromContents = true;
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaContent = textContent.Text,
                            };
                            break;
                        case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaReasoningContent = reasoningContent.Text,
                            };
                            break;
                        case FunctionCallContent functionCall:
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaToolCall = ConvertFunctionCallDelta(functionCall),
                            };
                            break;
                        case DataContent dataContent when TryConvertDataContent(dataContent, out var dataPart):
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaContentPart = dataPart,
                            };
                            break;
                        case UriContent uriContent when TryConvertUriContent(uriContent, out var uriPart):
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaContentPart = uriPart,
                            };
                            break;
                    }
                }
            }

            if (!emittedTextFromContents && !string.IsNullOrEmpty(update.Text))
            {
                emittedStreamChunk = true;
                yield return new LLMStreamChunk
                {
                    DeltaContent = update.Text,
                };
            }
        }

        // Some providers may terminate a streaming call without emitting any chunk at all.
        // Only in that case do a single non-streaming fallback call.
        if (!emittedStreamChunk)
        {
            _logger.LogWarning(
                "MEAI ChatStreamAsync emitted no chunks for provider={Provider}; fallback to non-streaming response.",
                Name);

            var fallback = ConvertResponse(await _client.GetResponseAsync(messages, options, ct));
            if (!string.IsNullOrEmpty(fallback.Content))
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = fallback.Content,
                };
            }

            if (fallback.ContentParts is { Count: > 0 })
            {
                foreach (var contentPart in fallback.ContentParts)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaContentPart = contentPart,
                    };
                }
            }

            if (fallback.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in fallback.ToolCalls)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = toolCall,
                    };
                }
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = fallback.Usage,
                FinishReason = fallback.FinishReason,
            };
            yield break;
        }

        // The final chunk marks the end
        yield return new LLMStreamChunk { IsLast = true, FinishReason = lastFinishReason };
    }

    // ─── Conversion: Aevatar -> MEAI ───

    private static List<Microsoft.Extensions.AI.ChatMessage> ConvertMessages(
        IEnumerable<Aevatar.AI.Abstractions.LLMProviders.ChatMessage> messages)
    {
        var result = new List<Microsoft.Extensions.AI.ChatMessage>();

        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                "system" => ChatRole.System,
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User,
            };

            var meaiMsg = new Microsoft.Extensions.AI.ChatMessage(role, msg.Content ?? "");
            if (msg.ContentParts is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                AppendContentParts(meaiMsg, msg.ContentParts);
                if (meaiMsg.Contents.Count == 0 && !string.IsNullOrEmpty(msg.Content))
                    meaiMsg.Contents.Add(new TextContent(msg.Content));
            }

            // Handle tool call results
            if (msg.Role == "tool" && msg.ToolCallId != null)
            {
                meaiMsg.Contents.Clear();
                meaiMsg.Contents.Add(new FunctionResultContent(msg.ToolCallId, BuildToolResultPayload(msg)));
            }

            // Handle assistant tool calls
            if (msg.ToolCalls is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                if (msg.ContentParts is { Count: > 0 })
                    AppendContentParts(meaiMsg, msg.ContentParts);
                else if (msg.Content != null)
                    meaiMsg.Contents.Add(new TextContent(msg.Content));

                foreach (var tc in msg.ToolCalls)
                {
                    // Parse JSON arguments into a dictionary
                    Dictionary<string, object?>? args = null;
                    if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                    {
                        try
                        {
                            args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                        }
                        catch { /* If parsing fails, do not pass arguments */ }
                    }

                    meaiMsg.Contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
                }
            }

            result.Add(meaiMsg);
        }

        return result;
    }

    private static void AppendContentParts(
        Microsoft.Extensions.AI.ChatMessage message,
        IReadOnlyList<ContentPart> parts)
    {
        foreach (var part in parts)
        {
            if (part == null || part.Kind == ContentPartKind.Unspecified)
                continue;

            if (part.Kind == ContentPartKind.Text)
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                    message.Contents.Add(new TextContent(part.Text));
                continue;
            }

            if (TryCreateBinaryContent(part, out var content) && content != null)
                message.Contents.Add(content);
        }
    }

    private static bool TryCreateBinaryContent(ContentPart part, out AIContent? content)
    {
        content = null;

        if (!string.IsNullOrWhiteSpace(part.Uri))
        {
            if (Uri.TryCreate(part.Uri, UriKind.Absolute, out var uri))
            {
                content = new UriContent(uri, ResolveMediaType(part));
                return true;
            }

            content = new UriContent(part.Uri, ResolveMediaType(part));
            return true;
        }

        if (string.IsNullOrWhiteSpace(part.DataBase64))
            return false;

        var mediaType = ResolveMediaType(part);
        var base64 = part.DataBase64!.Trim();

        // Accept both plain base64 and data-uri input.
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = base64.IndexOf(',');
            if (commaIndex > 5)
            {
                var meta = base64[5..commaIndex];
                if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    meta = meta[..^7];
                if (!string.IsNullOrWhiteSpace(meta))
                    mediaType = meta;
                base64 = base64[(commaIndex + 1)..];
            }
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            content = new DataContent(bytes, mediaType);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object BuildToolResultPayload(Aevatar.AI.Abstractions.LLMProviders.ChatMessage message)
    {
        if (message.ContentParts is not { Count: > 0 })
            return message.Content ?? string.Empty;

        return new
        {
            text = message.Content,
            content_parts = message.ContentParts.Select(p => new
            {
                kind = p.Kind.ToString(),
                text = p.Text,
                data_base64 = p.DataBase64,
                media_type = p.MediaType,
                uri = p.Uri,
                name = p.Name,
            }).ToArray(),
        };
    }

    private static ChatOptions? BuildOptions(LLMRequest request)
    {
        var options = new ChatOptions();
        var hasOptions = false;

        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            options.ConversationId = request.RequestId.Trim();
            hasOptions = true;
        }

        if (request.Model != null) { options.ModelId = request.Model; hasOptions = true; }
        if (request.Temperature.HasValue) { options.Temperature = (float)request.Temperature.Value; hasOptions = true; }
        if (request.MaxTokens.HasValue) { options.MaxOutputTokens = request.MaxTokens.Value; hasOptions = true; }

        if (!string.IsNullOrWhiteSpace(request.RequestId) || request.Metadata is { Count: > 0 })
        {
            options.AdditionalProperties = BuildAdditionalProperties(request);
            hasOptions = true;
        }

        // Map ResponseFormat
        if (request.ResponseFormat is not null)
        {
            options.ResponseFormat = request.ResponseFormat.Kind switch
            {
                LLMResponseFormatKind.Text => ChatResponseFormat.Text,
                LLMResponseFormatKind.JsonObject => ChatResponseFormat.Json,
                LLMResponseFormatKind.JsonSchema when request.ResponseFormat is LLMResponseFormatJsonSchema jsonSchema =>
                    ChatResponseFormat.ForJsonSchema(
                        jsonSchema.Schema,
                        jsonSchema.SchemaName,
                        jsonSchema.SchemaDescription),
                _ => null,
            };
            if (options.ResponseFormat != null)
                hasOptions = true;
        }

        // Register tools — use each tool's own ParametersSchema so the LLM sees the real parameter structure
        if (request.Tools is { Count: > 0 })
        {
            options.Tools = [];
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(new AgentToolAIFunction(tool));
            }
            hasOptions = true;
        }

        return hasOptions ? options : null;
    }

    private static AdditionalPropertiesDictionary BuildAdditionalProperties(LLMRequest request)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (request.Metadata != null)
        {
            foreach (var pair in request.Metadata)
                properties[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.RequestId))
            properties[LLMRequestMetadataKeys.RequestId] = request.RequestId.Trim();

        return new AdditionalPropertiesDictionary(properties);
    }

    // ─── Conversion: MEAI -> Aevatar ───

    private static LLMResponse ConvertResponse(Microsoft.Extensions.AI.ChatResponse response)
    {
        // ChatResponse.Messages contains all reply messages
        var lastMessage = response.Messages.LastOrDefault();
        var content = ExtractMessageText(lastMessage);
        List<ToolCall>? toolCalls = null;
        List<ContentPart>? contentParts = null;

        // Check whether there are tool calls
        if (lastMessage != null)
        {
            foreach (var part in lastMessage.Contents)
            {
                if (part is FunctionCallContent fcc)
                {
                    toolCalls ??= [];
                    toolCalls.Add(ConvertFunctionCall(fcc));
                }
                else if (part is DataContent dataContent && TryConvertDataContent(dataContent, out var dataPart))
                {
                    contentParts ??= [];
                    contentParts.Add(dataPart);
                }
                else if (part is UriContent uriContent && TryConvertUriContent(uriContent, out var uriPart))
                {
                    contentParts ??= [];
                    contentParts.Add(uriPart);
                }
            }
        }

        TokenUsage? usage = null;
        if (response.Usage != null)
        {
            usage = new TokenUsage(
                (int)(response.Usage.InputTokenCount ?? 0),
                (int)(response.Usage.OutputTokenCount ?? 0),
                (int)(response.Usage.TotalTokenCount ?? 0));
        }

        return new LLMResponse
        {
            Content = content,
            ContentParts = contentParts,
            ToolCalls = toolCalls,
            Usage = usage,
            FinishReason = response.FinishReason?.ToString(),
        };
    }

    private static string? ExtractMessageText(Microsoft.Extensions.AI.ChatMessage? message)
    {
        if (message == null)
            return null;

        if (!string.IsNullOrWhiteSpace(message.Text))
            return message.Text;

        if (message.Contents is not { Count: > 0 })
            return message.Text;

        var textParts = message.Contents
            .OfType<TextContent>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return textParts.Count == 0
            ? message.Text
            : string.Concat(textParts);
    }

    private static ToolCall ConvertFunctionCall(FunctionCallContent functionCall)
    {
        var argsJson = functionCall.Arguments != null
            ? System.Text.Json.JsonSerializer.Serialize(functionCall.Arguments)
            : "{}";
        return new ToolCall
        {
            Id = functionCall.CallId ?? Guid.NewGuid().ToString("N"),
            Name = functionCall.Name ?? string.Empty,
            ArgumentsJson = argsJson,
        };
    }

    // Keep delta semantics: missing callId should stay empty and be resolved by downstream accumulator.
    private static ToolCall ConvertFunctionCallDelta(FunctionCallContent functionCall)
    {
        var argsJson = functionCall.Arguments != null
            ? System.Text.Json.JsonSerializer.Serialize(functionCall.Arguments)
            : string.Empty;
        return new ToolCall
        {
            Id = functionCall.CallId ?? string.Empty,
            Name = functionCall.Name ?? string.Empty,
            ArgumentsJson = argsJson,
        };
    }

    private static bool TryConvertDataContent(DataContent dataContent, out ContentPart part)
    {
        var kind = ResolveKind(dataContent.MediaType);
        if (kind == ContentPartKind.Unspecified)
        {
            part = null!;
            return false;
        }

        part = new ContentPart
        {
            Kind = kind,
            DataBase64 = dataContent.Base64Data.ToString(),
            MediaType = dataContent.MediaType,
            Uri = dataContent.Uri?.ToString(),
            Name = dataContent.Name,
        };
        return true;
    }

    private static bool TryConvertUriContent(UriContent uriContent, out ContentPart part)
    {
        var kind = ResolveKind(uriContent.MediaType);
        if (kind == ContentPartKind.Unspecified)
        {
            part = null!;
            return false;
        }

        part = new ContentPart
        {
            Kind = kind,
            Uri = uriContent.Uri?.ToString(),
            MediaType = uriContent.MediaType,
        };
        return true;
    }

    private static string ResolveMediaType(ContentPart part) =>
        !string.IsNullOrWhiteSpace(part.MediaType)
            ? part.MediaType!
            : part.Kind switch
            {
                ContentPartKind.Audio => "audio/wav",
                ContentPartKind.Video => "video/mp4",
                _ => "image/png",
            };

    private static ContentPartKind ResolveKind(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ContentPartKind.Unspecified;

        var normalized = mediaType.Trim();
        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ContentPartKind.Image;
        if (normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return ContentPartKind.Audio;
        if (normalized.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return ContentPartKind.Video;
        return ContentPartKind.Unspecified;
    }
}
