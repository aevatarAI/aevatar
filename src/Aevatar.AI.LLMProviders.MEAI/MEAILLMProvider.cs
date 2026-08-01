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
using OpenAIAssistantChatMessage = OpenAI.Chat.AssistantChatMessage;
using OpenAIChatMessageContentPart = OpenAI.Chat.ChatMessageContentPart;
using OpenAIChatToolCall = OpenAI.Chat.ChatToolCall;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// ILLMProvider implementation based on the MEAI IChatClient.
/// Supports OpenAI, Azure OpenAI, and any provider compatible with the OpenAI API.
/// </summary>
public sealed class MEAILLMProvider : ILLMProvider
{
    private IAgentToolExecutionPort? _toolExecutionPort;
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
    public MEAILLMProvider(
        string name,
        IChatClient client,
        ILogger? logger = null,
        IAgentToolExecutionPort? toolExecutionPort = null)
    {
        Name = name;
        _client = client;
        _toolExecutionPort = toolExecutionPort;
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── ILLMProvider.ChatStreamAsync ───

    /// <summary>Streaming LLM call. Returns an async-enumerable stream of chunks.</summary>
    // Refactor (iter18/cluster-001):
    //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
    //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ConvertMessages(request.Messages, _logger);
        var options = BuildOptions(request, includeStreamUsage: true);

        _logger.LogDebug("MEAI ChatStreamAsync: {MessageCount} messages", messages.Count);
        var emittedStreamChunk = false;
        string? lastFinishReason = null;
        UsageDetails? capturedUsage = null;
        var updateCount = 0;
        var textEmits = 0;
        var reasoningEmits = 0;
        var toolCallEmits = 0;

        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            updateCount++;
            if (update.FinishReason != null)
                lastFinishReason = update.FinishReason.Value.ToString();

            var emittedTextFromContents = false;
            var emittedReasoningFromContents = false;
            if (update.Contents is { Count: > 0 })
            {
                foreach (var part in update.Contents)
                {
                    switch (part)
                    {
                        case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                            emittedTextFromContents = true;
                            emittedStreamChunk = true;
                            textEmits++;
                            yield return new LLMStreamChunk
                            {
                                DeltaContent = textContent.Text,
                            };
                            break;
                        case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                            emittedReasoningFromContents = true;
                            emittedStreamChunk = true;
                            reasoningEmits++;
                            yield return new LLMStreamChunk
                            {
                                DeltaReasoningContent = reasoningContent.Text,
                            };
                            break;
                        case FunctionCallContent functionCall:
                            emittedStreamChunk = true;
                            toolCallEmits++;
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
                        case UsageContent usageContent:
                            // Capture only: do NOT yield and do NOT set emittedStreamChunk — a usage-only
                            // final update must not be counted as content (else it skews the zero-chunk
                            // fallback decision). Last non-null wins; OpenAI sends the full usage block once.
                            capturedUsage = usageContent.Details;
                            break;
                    }
                }
            }

            if (!emittedTextFromContents && !string.IsNullOrEmpty(update.Text))
            {
                emittedStreamChunk = true;
                textEmits++;
                yield return new LLMStreamChunk
                {
                    DeltaContent = update.Text,
                };
            }

            if (!emittedReasoningFromContents &&
                TryExtractOpenAIReasoningDelta(update, out var reasoningDelta))
            {
                emittedStreamChunk = true;
                yield return new LLMStreamChunk
                {
                    DeltaReasoningContent = reasoningDelta,
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

            // The non-streaming fallback must NOT carry stream_options (OpenAI rejects it without
            // stream=true), so rebuild options without the streaming-usage opt-in. ConvertResponse still
            // captures usage from the non-streaming response via the same ToTokenUsage helper.
            var fallbackOptions = BuildOptions(request);
            var fallback = ConvertResponse(await _client.GetResponseAsync(messages, fallbackOptions, ct));
            foreach (var fallbackChunk in MapResponseToStreamChunks(fallback))
                yield return fallbackChunk;
            yield break;
        }

        // Stream-shape probe for the 2026-06-12 empty-reply incident: counts only, no content.
        _logger.LogInformation(
            "MEAI stream completed: provider={Provider} updates={Updates} textEmits={TextEmits} reasoningEmits={ReasoningEmits} toolCallEmits={ToolCallEmits} finish={Finish} messages={MessageCount}",
            Name,
            updateCount,
            textEmits,
            reasoningEmits,
            toolCallEmits,
            lastFinishReason ?? "(none)",
            messages.Count);

        // The final chunk marks the end and carries the captured streaming token usage. Usage is null when
        // the provider returned none (non-OpenAI client, or service still omitted it) — identical to the
        // prior behavior, with no extra LLM call.
        yield return new LLMStreamChunk { IsLast = true, FinishReason = lastFinishReason, Usage = ToTokenUsage(capturedUsage) };
    }

    private static bool TryExtractOpenAIReasoningDelta(
        ChatResponseUpdate update,
        out string reasoningDelta)
    {
        reasoningDelta = string.Empty;
        if (update.RawRepresentation is not OpenAI.Chat.StreamingChatCompletionUpdate rawUpdate)
            return false;

#pragma warning disable SCME0001
        return TryGetStringFromPatch(rawUpdate.Patch, "$.choices[0].delta.reasoning_content"u8, out reasoningDelta) ||
               TryGetStringFromPatch(rawUpdate.Patch, "$.delta.reasoning_content"u8, out reasoningDelta);
#pragma warning restore SCME0001
    }

#pragma warning disable SCME0001
    private static bool TryGetStringFromPatch(
        System.ClientModel.Primitives.JsonPatch patch,
        ReadOnlySpan<byte> path,
        out string value)
#pragma warning restore SCME0001
    {
        try
        {
            if (patch.TryGetValue(path, out string? patchValue) &&
                !string.IsNullOrEmpty(patchValue))
            {
                value = patchValue;
                return true;
            }

            value = string.Empty;
            return false;
        }
        catch (Exception)
        {
            value = string.Empty;
            return false;
        }
    }

    private static IEnumerable<LLMStreamChunk> MapResponseToStreamChunks(LLMResponse fallback)
    {
        // Refactor (iter18/cluster-001):
        //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
        //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
        if (!string.IsNullOrEmpty(fallback.Content))
            yield return new LLMStreamChunk { DeltaContent = fallback.Content };

        if (fallback.ContentParts is { Count: > 0 })
        {
            foreach (var contentPart in fallback.ContentParts)
                yield return new LLMStreamChunk { DeltaContentPart = contentPart };
        }

        if (fallback.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in fallback.ToolCalls)
                yield return new LLMStreamChunk { DeltaToolCall = toolCall };
        }

        yield return new LLMStreamChunk
        {
            IsLast = true,
            Usage = fallback.Usage,
            FinishReason = fallback.FinishReason,
        };
    }

    // ─── Conversion: Aevatar -> MEAI ───

    private static List<Microsoft.Extensions.AI.ChatMessage> ConvertMessages(
        IEnumerable<Aevatar.AI.Abstractions.LLMProviders.ChatMessage> messages,
        ILogger logger)
    {
        messages = ChatMessageToolCallTranscript.WithoutInvalidToolCallPairs(messages);
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

            if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ReasoningContent))
                meaiMsg.Contents.Add(new TextReasoningContent(msg.ReasoningContent));

            // Handle assistant tool calls
            if (msg.ToolCalls is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                if (!string.IsNullOrEmpty(msg.ReasoningContent))
                    meaiMsg.Contents.Add(new TextReasoningContent(msg.ReasoningContent));
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
                        catch (JsonException ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Failed to parse MEAI tool call arguments for tool {ToolName}; arguments will be omitted.",
                                tc.Name);
                        }
                    }

                    meaiMsg.Contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
                }
            }

            AttachOpenAIRawRepresentationForReasoning(meaiMsg, msg, logger);
            result.Add(meaiMsg);
        }

        return result;
    }

    private static void AttachOpenAIRawRepresentationForReasoning(
        Microsoft.Extensions.AI.ChatMessage meaiMessage,
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage sourceMessage,
        ILogger logger)
    {
        if (sourceMessage.Role != "assistant" || string.IsNullOrEmpty(sourceMessage.ReasoningContent))
            return;
        if (!HasOpenAIAssistantWireContent(sourceMessage))
            return;

        var rawMessage = BuildOpenAIAssistantMessage(sourceMessage);

        // OpenAI SDK currently exposes no typed reasoning_content property for
        // assistant history messages, so we fall back to its experimental Patch
        // API to inject the field into the serialized payload. The Patch surface
        // is marked SCME0001 (model serialization may evolve), and the
        // reasoning_content field name is also undocumented — any future SDK
        // bump that renames the field, drops Patch, or changes its serializer
        // shape would silently strip reasoning context from request history.
        //
        // Mitigations layered here:
        //   1. SDK version is pinned in Directory.Packages.props
        //      (`OpenAI Version="2.9.1"`) so an unintentional minor/major bump
        //      cannot land without an explicit dependency-bump PR.
        //   2. AIComponentCoverageTests asserts the serialized JSON contains
        //      `"reasoning_content":"..."` after ConvertMessages — that integration
        //      test fails the build the moment the patch stops landing in the
        //      payload, which is the only signal that survives an SDK bump.
        //   3. The try/catch below degrades a wire-format break into "no
        //      reasoning replay" rather than crashing the entire chat call.
        //
        // Long-term: when the OpenAI SDK exposes a typed reasoning_content
        // property (tracked at github.com/openai/openai-dotnet — file an issue
        // referencing this code path before bumping), retire the Patch hack
        // and remove the SCME0001 suppression.
        try
        {
#pragma warning disable SCME0001
            rawMessage.Patch.Set("$.reasoning_content"u8, sourceMessage.ReasoningContent);
#pragma warning restore SCME0001
            meaiMessage.RawRepresentation = rawMessage;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to attach OpenAI reasoning raw representation; reasoning replay will continue without SDK patch data.");
        }
    }

    private static OpenAIAssistantChatMessage BuildOpenAIAssistantMessage(
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage sourceMessage)
    {
        var contentParts = BuildOpenAITextContentParts(sourceMessage);
        var toolCalls = BuildOpenAIToolCalls(sourceMessage);

        OpenAIAssistantChatMessage rawMessage;
        if (contentParts.Count > 0)
        {
            rawMessage = new OpenAIAssistantChatMessage(contentParts);
            foreach (var toolCall in toolCalls)
                rawMessage.ToolCalls.Add(toolCall);
            return rawMessage;
        }

        rawMessage = toolCalls.Count > 0
            ? new OpenAIAssistantChatMessage(toolCalls)
            : new OpenAIAssistantChatMessage(string.Empty);
        return rawMessage;
    }

    private static List<OpenAIChatMessageContentPart> BuildOpenAITextContentParts(
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage sourceMessage)
    {
        var contentParts = new List<OpenAIChatMessageContentPart>();
        if (sourceMessage.ContentParts is { Count: > 0 })
        {
            foreach (var part in sourceMessage.ContentParts)
            {
                if (part.Kind == ContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                    contentParts.Add(OpenAIChatMessageContentPart.CreateTextPart(part.Text));
            }
        }

        if (contentParts.Count == 0 && !string.IsNullOrEmpty(sourceMessage.Content))
            contentParts.Add(OpenAIChatMessageContentPart.CreateTextPart(sourceMessage.Content));

        return contentParts;
    }

    private static bool HasOpenAIAssistantWireContent(
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage sourceMessage)
    {
        if (sourceMessage.ToolCalls is { Count: > 0 })
            return true;
        if (!string.IsNullOrEmpty(sourceMessage.Content))
            return true;
        return sourceMessage.ContentParts is { Count: > 0 }
               && sourceMessage.ContentParts.Any(static part =>
                   part.Kind == ContentPartKind.Text && !string.IsNullOrEmpty(part.Text));
    }

    private static List<OpenAIChatToolCall> BuildOpenAIToolCalls(
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage sourceMessage)
    {
        var toolCalls = new List<OpenAIChatToolCall>();
        if (sourceMessage.ToolCalls is not { Count: > 0 })
            return toolCalls;

        foreach (var toolCall in sourceMessage.ToolCalls)
        {
            toolCalls.Add(OpenAIChatToolCall.CreateFunctionToolCall(
                toolCall.Id,
                toolCall.Name,
                BinaryData.FromString(NormalizeToolArgumentsJson(toolCall.ArgumentsJson))));
        }

        return toolCalls;
    }

    private static string NormalizeToolArgumentsJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "{}";

        try
        {
            using var _ = JsonDocument.Parse(argumentsJson);
            return argumentsJson;
        }
        catch (JsonException)
        {
            return "{}";
        }
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

    private ChatOptions? BuildOptions(LLMRequest request, bool includeStreamUsage = false)
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
            var executionPort = _toolExecutionPort
                ?? throw new InvalidOperationException(
                    "IAgentToolExecutionPort is required when MEAI exposes server-owned tools.");
            options.Tools = [];
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(new AgentToolAIFunction(tool, executionPort));
            }
            hasOptions = true;
        }

        // Opt into streaming token usage for the OpenAI-compatible request. The MEAI OpenAI adapter does
        // NOT request usage by default and OpenAI's StreamOptions is internal, so we set it via the public
        // experimental JsonPatch escape hatch on a base ChatCompletionOptions that ToOpenAIOptions layers
        // model/temperature/tools onto (verified: it only overlays unset fields, never drops ours). Scoped
        // to streaming only — a non-streaming request carrying stream_options would be rejected, so the
        // zero-chunk fallback rebuilds options without this.
        if (includeStreamUsage)
        {
            options.RawRepresentationFactory = static _ => CreateStreamUsageChatCompletionOptions();
            hasOptions = true;
        }

        return hasOptions ? options : null;
    }

#pragma warning disable SCME0001 // ChatCompletionOptions.Patch is an experimental escape hatch (same suppression as TryGetStringFromPatch)
    private static OpenAI.Chat.ChatCompletionOptions CreateStreamUsageChatCompletionOptions()
    {
        var raw = new OpenAI.Chat.ChatCompletionOptions();
        // Set the whole stream_options object so no intermediate node has to be auto-created.
        raw.Patch.Set("$.stream_options"u8, "{\"include_usage\":true}"u8);
        return raw;
    }
#pragma warning restore SCME0001

    // Single UsageDetails -> TokenUsage mapping used by both the streaming terminal chunk and the
    // non-streaming ConvertResponse path. Falls back to prompt+completion when the provider omits the
    // total so the UI never shows non-zero input/output with a zero grand total.
    private static TokenUsage? ToTokenUsage(UsageDetails? usage)
    {
        if (usage == null)
            return null;

        var promptTokens = (int)(usage.InputTokenCount ?? 0);
        var completionTokens = (int)(usage.OutputTokenCount ?? 0);
        var totalTokens = (int)(usage.TotalTokenCount ?? 0);
        if (totalTokens == 0 && (promptTokens > 0 || completionTokens > 0))
            totalTokens = promptTokens + completionTokens;

        return new TokenUsage(promptTokens, completionTokens, totalTokens);
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
        var reasoningContent = ExtractReasoningContent(lastMessage);
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

        var usage = ToTokenUsage(response.Usage);

        return new LLMResponse
        {
            Content = content,
            ReasoningContent = reasoningContent,
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

    private static string? ExtractReasoningContent(Microsoft.Extensions.AI.ChatMessage? message)
    {
        if (message?.Contents is not { Count: > 0 })
            return null;

        var reasoningParts = message.Contents
            .OfType<TextReasoningContent>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return reasoningParts.Count == 0
            ? null
            : string.Concat(reasoningParts);
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
