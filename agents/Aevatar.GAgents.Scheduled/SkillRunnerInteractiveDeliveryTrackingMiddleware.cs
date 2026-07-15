using System.Text.Json;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Scheduled;

internal sealed record SkillRunnerInteractiveDeliverySignal(
    DeliveryKind DeliveryKind,
    DeliveryStatus Status,
    string RequestId,
    string SourceEventId,
    string ProviderMessageId,
    string CardId);

internal sealed class SkillRunnerInteractiveDeliverySignalCollector
{
    private readonly List<SkillRunnerInteractiveDeliverySignal> _signals = [];

    public IReadOnlyList<SkillRunnerInteractiveDeliverySignal> Signals => _signals;

    public bool HasSuccessfulInteractiveDelivery =>
        _signals.Any(static signal => signal.Status == DeliveryStatus.Succeeded);

    public void Reset() => _signals.Clear();

    public void Record(SkillRunnerInteractiveDeliverySignal signal) => _signals.Add(signal);
}

/// <summary>
/// Tracks Lark interactive/card sends performed inside a scheduled skill run so the runner
/// does not append a second outer reply after the skill has already delivered the card.
/// </summary>
internal sealed class SkillRunnerInteractiveDeliveryTrackingMiddleware : IToolCallMiddleware
{
    private static readonly HashSet<string> LarkMessageTools =
    [
        "lark_messages_send",
        "lark_messages_reply",
    ];

    private readonly SkillRunnerInteractiveDeliverySignalCollector _collector;

    public SkillRunnerInteractiveDeliveryTrackingMiddleware(SkillRunnerInteractiveDeliverySignalCollector collector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        await next().ConfigureAwait(false);

        if (!IsInteractiveDeliveryTool(context.ToolName, context.ArgumentsJson))
            return;
        if (!TryReadSuccessfulToolResult(context.Result, out var providerMessageId, out var cardId))
            return;

        _collector.Record(new SkillRunnerInteractiveDeliverySignal(
            ResolveDeliveryKind(context.ToolName, context.ArgumentsJson),
            DeliveryStatus.Succeeded,
            NormalizeOptional(AgentToolRequestContext.RequestId) ?? string.Empty,
            NormalizeOptional(context.ToolCallId) ?? NormalizeOptional(AgentToolRequestContext.CallId) ?? string.Empty,
            providerMessageId,
            cardId));
    }

    private static bool IsInteractiveDeliveryTool(string toolName, string? argumentsJson)
    {
        if (string.Equals(toolName, "reply_with_interaction", StringComparison.Ordinal))
            return true;

        if (!LarkMessageTools.Contains(toolName))
            return false;

        return TryReadString(argumentsJson, "message_type", out var messageType) &&
               IsInteractiveMessageType(messageType);
    }

    private static bool IsInteractiveMessageType(string? messageType)
    {
        var normalized = messageType?.Trim();
        return string.Equals(normalized, "interactive", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "interactive_card", StringComparison.OrdinalIgnoreCase);
    }

    private static DeliveryKind ResolveDeliveryKind(string toolName, string? argumentsJson)
    {
        if (string.Equals(toolName, "reply_with_interaction", StringComparison.Ordinal))
            return DeliveryKind.InteractiveCard;

        return TryReadString(argumentsJson, "message_type", out var messageType) &&
               IsInteractiveMessageType(messageType)
            ? DeliveryKind.InteractiveCard
            : DeliveryKind.TextMessage;
    }

    private static bool TryReadSuccessfulToolResult(string? resultJson, out string providerMessageId, out string cardId)
    {
        providerMessageId = string.Empty;
        cardId = string.Empty;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("success", out var success))
            {
                if (success.ValueKind != JsonValueKind.True)
                    return false;

                ReadDeliveryIds(root, out providerMessageId, out cardId);
                return true;
            }

            if (root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var value))
            {
                if (value != 0)
                    return false;

                ReadDeliveryIds(root, out providerMessageId, out cardId);
                return true;
            }

            if (root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String)
            {
                var statusValue = status.GetString();
                if (string.Equals(statusValue, "queued", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(statusValue, "sent", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(statusValue, "success", StringComparison.OrdinalIgnoreCase))
                {
                    ReadDeliveryIds(root, out providerMessageId, out cardId);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static void ReadDeliveryIds(JsonElement root, out string providerMessageId, out string cardId)
    {
        providerMessageId = ReadFirstString(root, "message_id", "lark_message_id", "sent_activity_id", "reply_message_id");
        cardId = ReadFirstString(root, "card_id", "cardId");
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrEmpty(providerMessageId))
                providerMessageId = ReadFirstString(data, "message_id", "lark_message_id", "sent_activity_id", "reply_message_id");
            if (string.IsNullOrEmpty(cardId))
                cardId = ReadFirstString(data, "card_id", "cardId");
        }
    }

    private static string ReadFirstString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                var value = NormalizeOptional(property.GetString());
                if (value is not null)
                    return value;
            }
        }

        return string.Empty;
    }

    private static bool TryReadString(string? json, string propertyName, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
