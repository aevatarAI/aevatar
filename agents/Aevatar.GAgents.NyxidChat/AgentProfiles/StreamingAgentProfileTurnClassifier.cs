using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public sealed class StreamingAgentProfileTurnClassifier : IAgentProfileTurnClassifier
{
    internal const int MaximumCandidates = 32;
    internal const int MaximumInputUtf8Bytes = 16 * 1024;
    internal const int MaximumOutputUtf8Bytes = 4 * 1024;

    private readonly ILLMProviderFactory _providerFactory;
    private readonly TimeProvider _timeProvider;

    public StreamingAgentProfileTurnClassifier(
        ILLMProviderFactory providerFactory,
        TimeProvider? timeProvider = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentProfileTurnClassificationResult> ClassifyAsync(
        AgentProfileTurnClassificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates is not { Count: > 0 and <= MaximumCandidates })
            return AgentProfileTurnClassificationResult.Failed("candidate_count_out_of_bounds");
        if (request.Timeout <= TimeSpan.Zero)
            return AgentProfileTurnClassificationResult.Failed("timeout_out_of_bounds");

        var input = BuildInput(request);
        if (Encoding.UTF8.GetByteCount(input) > MaximumInputUtf8Bytes)
            return AgentProfileTurnClassificationResult.Failed("input_too_large");

        var llmRequest = new LLMRequest
        {
            Messages =
            [
                ChatMessage.System(
                    "Classify the user message against the supplied intent catalog. " +
                    "Select the intent that directly produces the user's final requested outcome, " +
                    "not an intermediate prerequisite or discovery step. " +
                    "When an external_handoff intent directly fulfills that outcome and a read_only " +
                    "intent only discovers a prerequisite, select the external_handoff intent. " +
                    "Return only JSON with status 'matched' and intent_id, or status 'no_match'."),
                ChatMessage.User(input),
            ],
            Tools = null,
            ResponseFormat = LLMResponseFormat.ForJsonSchema<ClassificationPayload>(
                "agent_profile_turn_classification"),
            MaxTokens = 128,
            Temperature = 0,
        };

        using var timeoutCts = new CancellationTokenSource(request.Timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var output = new StringBuilder();
        var outputBytes = 0;
        try
        {
            await foreach (var chunk in _providerFactory.GetDefault()
                               .ChatStreamAsync(llmRequest, linkedCts.Token)
                               .WithCancellation(linkedCts.Token))
            {
                if (chunk.DeltaToolCall is not null)
                    return AgentProfileTurnClassificationResult.Failed("tool_call_not_allowed");
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;

                outputBytes += Encoding.UTF8.GetByteCount(chunk.DeltaContent);
                if (outputBytes > MaximumOutputUtf8Bytes)
                    return AgentProfileTurnClassificationResult.Failed("output_too_large");
                output.Append(chunk.DeltaContent);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AgentProfileTurnClassificationResult.Failed("timeout");
        }
        catch (JsonException)
        {
            return AgentProfileTurnClassificationResult.Failed("malformed_output");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentProfileTurnClassificationResult.Failed("provider_failure");
        }

        return ParseOutput(output.ToString(), request.Candidates);
    }

    private static string BuildInput(AgentProfileTurnClassificationRequest request) =>
        JsonSerializer.Serialize(new
        {
            user_message = request.UserMessage ?? string.Empty,
            intents = request.Candidates.Select(static candidate => new
            {
                intent_id = candidate.IntentId,
                routing_description = candidate.RoutingDescription,
                side_effect_class = JsonNamingPolicy.SnakeCaseLower.ConvertName(
                    candidate.SideEffectClass.ToString()),
            }),
        });

    private static AgentProfileTurnClassificationResult ParseOutput(
        string output,
        IReadOnlyList<AgentProfileTurnClassificationCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(output))
            return AgentProfileTurnClassificationResult.Failed("empty_output");

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AgentProfileTurnClassificationResult.Failed("malformed_output");

            foreach (var property in root.EnumerateObject())
            {
                if (!property.NameEquals("status") && !property.NameEquals("intent_id"))
                    return AgentProfileTurnClassificationResult.Failed("unexpected_output_field");
            }

            if (!root.TryGetProperty("status", out var statusProperty) ||
                statusProperty.ValueKind != JsonValueKind.String)
            {
                return AgentProfileTurnClassificationResult.Failed("status_missing");
            }

            var status = statusProperty.GetString();
            if (string.Equals(status, "no_match", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("intent_id", out var noMatchIntent) &&
                    noMatchIntent.ValueKind != JsonValueKind.Null)
                {
                    return AgentProfileTurnClassificationResult.Failed("unexpected_intent");
                }

                return AgentProfileTurnClassificationResult.NoMatch();
            }
            if (!string.Equals(status, "matched", StringComparison.Ordinal) ||
                !root.TryGetProperty("intent_id", out var intentProperty) ||
                intentProperty.ValueKind != JsonValueKind.String)
            {
                return AgentProfileTurnClassificationResult.Failed("malformed_output");
            }

            var intentId = intentProperty.GetString();
            if (string.IsNullOrWhiteSpace(intentId) ||
                !candidates.Any(candidate => string.Equals(candidate.IntentId, intentId, StringComparison.Ordinal)))
            {
                return AgentProfileTurnClassificationResult.Failed("unknown_intent");
            }

            return AgentProfileTurnClassificationResult.Matched(intentId);
        }
        catch (JsonException)
        {
            return AgentProfileTurnClassificationResult.Failed("malformed_output");
        }
    }

    private sealed class ClassificationPayload
    {
        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("intent_id")]
        public string? IntentId { get; init; }
    }
}
