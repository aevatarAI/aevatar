using System.Text;

namespace Aevatar.GAgents.NyxidChat;

// Defense-in-depth complement to the Kafka transport fix (commit f2c2319e7,
// which added Gzip + a 10MB MessageMaxBytes to KafkaProviderProducer): bound each
// tool-result message — and their aggregate — *before* they are packed into the
// AgentRunNextToolStepRequestedEvent command envelope that the executor produces
// to Kafka. A single run that aggregates large multi-source output (for example
// multi-repo GitHub commits/pulls/issues) can otherwise build an envelope larger
// than the broker's max.message.bytes, failing the whole run with an opaque
// Confluent.Kafka.ProduceException and the generic "Sorry, I wasn't able to
// generate a response" run-echo. Truncating oversized output with a visible
// marker lets the run still complete with a degraded-but-useful reply.
//
// Skill-agnostic by construction: the bound is computed purely from serialized
// payload size; no skill names, tool names, or content shapes are inspected.
internal static class ToolResultPayloadBounds
{
    // Per-tool-result and per-step caps, kept well below the producer's 10MB
    // ceiling so the wrapping EventEnvelope (Any type_url + route + the nested
    // NeedsLlmReplyEvent command) still fits with margin and Gzip has slack.
    internal const int DefaultMaxToolResultMessageBytes = 256 * 1024;
    internal const int DefaultMaxTotalToolResultBytes = 4 * 1024 * 1024;

    // Floor so that, even once the aggregate budget is exhausted, every tool
    // call still receives a (marker-only) result message: the next LLM step
    // requires one tool result per tool_call_id, so dropping a message would
    // corrupt the conversation instead of merely degrading it. Honouring this
    // floor can let the aggregate exceed maxTotalBytes by at most
    // messages.Count * MinMessageBudgetBytes, which stays far below the
    // producer ceiling for any realistic tool-call count.
    private const int MinMessageBudgetBytes = 1024;

    // A proto string field adds a tag byte plus a varint length prefix; reserve a
    // few bytes so the marker-appended content clears the cap on the first try
    // (the shrink-verify loop still guarantees correctness if this is too small).
    private const int ProtoStringFieldSlack = 8;

    internal static void BoundResultMessages(
        IList<AgentRunChatMessage> messages,
        int maxMessageBytes = DefaultMaxToolResultMessageBytes,
        int maxTotalBytes = DefaultMaxTotalToolResultBytes)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long runningTotal = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var remaining = maxTotalBytes - runningTotal;
            var cap = remaining < MinMessageBudgetBytes
                ? MinMessageBudgetBytes
                : (int)Math.Min(maxMessageBytes, remaining);

            var bounded = BoundMessage(messages[i], cap);
            messages[i] = bounded;
            runningTotal += bounded.CalculateSize();
        }
    }

    private static AgentRunChatMessage BoundMessage(AgentRunChatMessage message, int maxBytes)
    {
        if (message.CalculateSize() <= maxBytes)
            return message;

        var bounded = message.Clone();
        var originalContentBytes = Encoding.UTF8.GetByteCount(bounded.Content);
        var droppedParts = bounded.ContentParts.Count;

        // Attachments (e.g. base64 image/audio blobs) cannot be partially shown
        // usefully and are the most likely single oversized contributor, so drop
        // them wholesale and note the omission in the marker.
        bounded.ContentParts.Clear();

        // Fixed overhead = role + tool_call_id + tool_calls (everything but content).
        bounded.Content = string.Empty;
        var overhead = bounded.CalculateSize();

        var markerEstimate = Encoding.UTF8.GetByteCount(BuildMarker(0, originalContentBytes, droppedParts));
        var budget = maxBytes - overhead - markerEstimate - ProtoStringFieldSlack;
        var prefix = TruncateUtf8(message.Content, Math.Max(0, budget));
        bounded.Content = prefix + BuildMarker(Encoding.UTF8.GetByteCount(prefix), originalContentBytes, droppedParts);

        // The marker length grows with the kept-byte count, so the first estimate
        // can still overshoot; shrink the visible prefix until the message fits.
        while (bounded.CalculateSize() > maxBytes && prefix.Length > 0)
        {
            prefix = prefix[..(prefix.Length / 2)];
            bounded.Content = prefix + BuildMarker(Encoding.UTF8.GetByteCount(prefix), originalContentBytes, droppedParts);
        }

        // Last resort when even the marker alone exceeds a very small cap: hard-cap
        // the content to whatever fits after the fixed overhead and the content
        // field's own tag + length prefix.
        if (bounded.CalculateSize() > maxBytes)
            bounded.Content = TruncateUtf8(bounded.Content, Math.Max(0, maxBytes - overhead - ProtoStringFieldSlack));

        return bounded;
    }

    private static string BuildMarker(int keptBytes, int originalBytes, int droppedParts)
    {
        var omittedBytes = Math.Max(0, originalBytes - keptBytes);
        var attachments = droppedParts > 0
            ? $"; {droppedParts} attachment(s) omitted"
            : string.Empty;
        return $"\n\n[tool output truncated: kept {keptBytes} of {originalBytes} bytes, {omittedBytes} omitted{attachments} to stay within the message-size limit]";
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0 || value.Length == 0)
            return string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var bytes = Encoding.UTF8.GetBytes(value);
        var len = maxBytes;
        // Back off so the cut never lands in the middle of a multi-byte UTF-8
        // sequence (continuation bytes match 10xxxxxx).
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }
}
