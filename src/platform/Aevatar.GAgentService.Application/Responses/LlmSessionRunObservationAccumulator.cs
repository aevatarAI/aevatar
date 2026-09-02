using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

internal sealed class LlmSessionRunObservationAccumulator(string responseId)
{
    private readonly string _responseId = responseId;
    private readonly StringBuilder _outputText = new();
    private readonly List<LlmSessionRuntimeToolCall> _toolCalls = [];
    private TokenUsage? _usage;
    private DateTimeOffset? _completedAt;

    public LlmSessionRunObservedDelta? ObserveChunk(LlmStreamChunkObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!string.IsNullOrEmpty(observed.DeltaText))
            _outputText.Append(observed.DeltaText);
        if (observed.Usage != null)
            _usage = ToBoundaryUsage(observed.Usage);
        if (string.IsNullOrEmpty(observed.DeltaText) && observed.Usage == null)
            return null;

        return new LlmSessionRunObservedDelta(
            string.IsNullOrEmpty(observed.DeltaText) ? null : observed.DeltaText,
            observed.Usage == null ? null : ToBoundaryUsage(observed.Usage));
    }

    public void ObserveToolCall(LlmToolCallObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.ToolCall == null)
            return;

        UpsertToolCall(observed.ToolCall);
    }

    public void ObserveCompleted(LlmRunCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        _outputText.Clear();
        _outputText.Append(completed.OutputText ?? string.Empty);
        if (completed.Usage != null)
            _usage = ToBoundaryUsage(completed.Usage);
        _completedAt = completed.CompletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        _toolCalls.Clear();
        _toolCalls.AddRange(completed.ForwardedToolCalls.Select(static call => call.Clone()));
    }

    public LlmSessionCompletionSnapshot BuildCompletion() =>
        new(
            _outputText.ToString(),
            _toolCalls
                .Select(static call => new LlmSessionCompletedToolCallSnapshot(
                    call.CallId,
                    call.ToolName,
                    RuntimeToolArgumentsJson(call)))
                .ToArray(),
            _completedAt ?? DateTimeOffset.UtcNow,
            null,
            null,
            _usage);

    private void UpsertToolCall(LlmSessionRuntimeToolCall call)
    {
        var index = _toolCalls.FindIndex(existing =>
            string.Equals(existing.CallId, call.CallId, StringComparison.Ordinal));
        if (index >= 0)
            _toolCalls[index] = call.Clone();
        else
            _toolCalls.Add(call.Clone());
    }

    private static string RuntimeToolArgumentsJson(LlmSessionRuntimeToolCall call)
    {
        if (call.Arguments is { Fields.Count: > 0 })
            return ResponsesJsonValues.ToBoundaryJson(Value.ForStruct(call.Arguments.Clone())) ?? "{}";
        return string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
    }

    private static TokenUsage ToBoundaryUsage(LlmSessionTokenUsage usage) =>
        new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
}
