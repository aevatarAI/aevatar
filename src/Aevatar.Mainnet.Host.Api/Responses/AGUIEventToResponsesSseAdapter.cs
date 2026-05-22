using System.Text;
using System.Text.Json;
using Aevatar.Presentation.AGUI;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>
/// Translates AGUI events emitted by a Static GAgent stream invocation into
/// OpenAI Responses SSE frames written directly to the caller's HTTP response.
///
/// AGUI proto and Responses SSE are isomorphic LLM-style event streams: text
/// deltas, tool calls, run lifecycle. This adapter maintains the
/// `sequence_number` + `output_index` invariants the Responses wire requires
/// and emits one Responses event per relevant AGUI event.
///
/// Lifecycle:
///   1. Caller writes SSE headers + calls <see cref="WriteCreatedAsync"/> once
///   2. Each AGUI event is forwarded via <see cref="WriteAsync"/>
///   3. Caller calls <see cref="WriteCompletedAsync"/> when the run finishes
/// </summary>
internal sealed class AGUIEventToResponsesSseAdapter
{
    private readonly HttpResponse _response;
    private readonly string _responseId;
    private readonly string _messageItemId;
    private readonly JsonSerializerOptions _jsonOptions;
    private int _sequenceNumber;
    private int _nextOutputIndex;
    private readonly StringBuilder _aggregatedText = new();
    private readonly Dictionary<string, int> _toolCallOutputIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallNames = new(StringComparer.Ordinal);
    private readonly List<(string ToolCallId, string ToolName, string? Result)> _completedToolCalls = new();

    public AGUIEventToResponsesSseAdapter(
        HttpResponse response,
        string responseId,
        string messageItemId,
        JsonSerializerOptions jsonOptions)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
        _responseId = responseId ?? throw new ArgumentNullException(nameof(responseId));
        _messageItemId = messageItemId ?? throw new ArgumentNullException(nameof(messageItemId));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public string AggregatedText => _aggregatedText.ToString();

    public bool HasFailed { get; private set; }

    public IReadOnlyList<(string ToolCallId, string ToolName, string? Result)> CompletedToolCalls =>
        _completedToolCalls;

    /// <summary>
    /// Emit the initial `response.created` + `response.output_item.added` (in-progress
    /// message) frames. Mirrors what <c>WriteStreamResponseAsync</c> does at run start.
    /// </summary>
    public async Task WriteCreatedAsync(
        object createdResponse,
        object inProgressMessageItem,
        CancellationToken ct)
    {
        await WriteFrameAsync(
            "response.created",
            new
            {
                type = "response.created",
                response = createdResponse,
                sequence_number = ++_sequenceNumber,
            },
            ct);
        await WriteFrameAsync(
            "response.output_item.added",
            new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = inProgressMessageItem,
                sequence_number = ++_sequenceNumber,
            },
            ct);
        _nextOutputIndex = 1;
    }

    /// <summary>
    /// Map a single AGUI event to its Responses SSE equivalent. Idempotent for
    /// events that don't translate (StepStarted/Finished/StateSnapshot/Custom).
    /// </summary>
    public async ValueTask WriteAsync(AGUIEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageStart:
                // No-op on the wire: WriteCreatedAsync already emitted the
                // in-progress message item; multiple TextMessageStart events
                // in one run still target the same message_id / output_index=0.
                break;
            case AGUIEvent.EventOneofCase.TextMessageContent:
                {
                    var delta = evt.TextMessageContent?.Delta ?? string.Empty;
                    if (delta.Length == 0)
                        return;
                    _aggregatedText.Append(delta);
                    await WriteFrameAsync(
                        "response.output_text.delta",
                        new
                        {
                            type = "response.output_text.delta",
                            item_id = _messageItemId,
                            output_index = 0,
                            content_index = 0,
                            delta,
                            sequence_number = ++_sequenceNumber,
                        },
                        ct);
                    break;
                }
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                // No-op: `response.output_text.done` + `response.output_item.done` are
                // emitted from WriteCompletedAsync so BuildCompletedResponse sees the
                // fully aggregated text and tool calls at the same time.
                break;
            case AGUIEvent.EventOneofCase.ToolCallStart:
                {
                    var toolCall = evt.ToolCallStart;
                    if (toolCall is null || string.IsNullOrWhiteSpace(toolCall.ToolCallId))
                        return;
                    if (_toolCallOutputIndex.ContainsKey(toolCall.ToolCallId))
                        return;
                    var outputIndex = _nextOutputIndex++;
                    _toolCallOutputIndex[toolCall.ToolCallId] = outputIndex;
                    _toolCallNames[toolCall.ToolCallId] = toolCall.ToolName ?? string.Empty;
                    break;
                }
            case AGUIEvent.EventOneofCase.ToolCallEnd:
                {
                    var toolCall = evt.ToolCallEnd;
                    if (toolCall is null || string.IsNullOrWhiteSpace(toolCall.ToolCallId))
                        return;
                    if (!_toolCallOutputIndex.ContainsKey(toolCall.ToolCallId))
                        return;
                    var toolName = _toolCallNames.GetValueOrDefault(toolCall.ToolCallId, string.Empty);
                    _completedToolCalls.Add((toolCall.ToolCallId, toolName, toolCall.Result));
                    break;
                }
            case AGUIEvent.EventOneofCase.RunError:
                {
                    var error = evt.RunError;
                    var message = string.IsNullOrWhiteSpace(error?.Message)
                        ? "GAgent invocation failed."
                        : error!.Message;
                    var code = string.IsNullOrWhiteSpace(error?.Code)
                        ? "gagent_invocation_failed"
                        : error!.Code;
                    HasFailed = true;
                    await WriteFrameAsync(
                        "response.failed",
                        new
                        {
                            type = "response.failed",
                            sequence_number = ++_sequenceNumber,
                            response = new
                            {
                                id = _responseId,
                                status = "failed",
                                error = new { code, message },
                            },
                        },
                        ct);
                    break;
                }
            default:
                // RunStarted: WriteCreatedAsync already emitted response.created.
                // StepStarted/StepFinished/StateSnapshot/Custom/HumanInput*: no Responses SSE equivalent.
                break;
        }
    }

    /// <summary>
    /// Emit the closing frames: `response.output_text.done`, `response.output_item.done`,
    /// per-tool-call output items, then `response.completed`.
    /// </summary>
    public async Task WriteCompletedAsync(
        Func<string, object> buildCompletedMessageItem,
        Func<(string ToolCallId, string ToolName, string? Result), object> buildFunctionCallItem,
        Func<string, object> buildCompletedResponse,
        CancellationToken ct)
    {
        var completedText = _aggregatedText.ToString();

        // Always close the in-progress message item that WriteCreatedAsync opened
        // — Responses SSE requires every `output_item.added` to pair with an
        // `output_item.done`, even when the GAgent produced zero TextMessage events
        // (pure tool-call runs). Empty `completedText` is valid per spec.
        await WriteFrameAsync(
            "response.output_text.done",
            new
            {
                type = "response.output_text.done",
                item_id = _messageItemId,
                output_index = 0,
                content_index = 0,
                text = completedText,
                sequence_number = ++_sequenceNumber,
            },
            ct);
        await WriteFrameAsync(
            "response.output_item.done",
            new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = buildCompletedMessageItem(completedText),
                sequence_number = ++_sequenceNumber,
            },
            ct);

        foreach (var toolCall in _completedToolCalls)
        {
            var outputIndex = _toolCallOutputIndex.GetValueOrDefault(toolCall.ToolCallId, _nextOutputIndex++);
            var item = buildFunctionCallItem(toolCall);
            await WriteFrameAsync(
                "response.output_item.added",
                new
                {
                    type = "response.output_item.added",
                    output_index = outputIndex,
                    item,
                    sequence_number = ++_sequenceNumber,
                },
                ct);
            await WriteFrameAsync(
                "response.output_item.done",
                new
                {
                    type = "response.output_item.done",
                    output_index = outputIndex,
                    item,
                    sequence_number = ++_sequenceNumber,
                },
                ct);
        }

        await WriteFrameAsync(
            "response.completed",
            new
            {
                type = "response.completed",
                response = buildCompletedResponse(completedText),
                sequence_number = ++_sequenceNumber,
            },
            ct);
    }

    /// <summary>
    /// Write a structured failure frame and ensure the stream closes with a
    /// `response.failed` event the client can observe.
    /// </summary>
    public async Task WriteFailureAsync(
        string code,
        string message,
        CancellationToken ct)
    {
        HasFailed = true;
        await WriteFrameAsync(
            "response.failed",
            new
            {
                type = "response.failed",
                sequence_number = ++_sequenceNumber,
                response = new
                {
                    id = _responseId,
                    status = "failed",
                    error = new { code, message },
                },
            },
            ct);
    }

    private async Task WriteFrameAsync(string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var headerBytes = Encoding.UTF8.GetBytes($"event: {eventName}\n");
        await _response.Body.WriteAsync(headerBytes, ct);
        var dataBytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _response.Body.WriteAsync(dataBytes, ct);
        await _response.Body.FlushAsync(ct);
    }
}
