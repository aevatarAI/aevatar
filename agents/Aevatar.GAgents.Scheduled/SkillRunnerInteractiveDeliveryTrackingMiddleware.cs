using System.Text.Json;
using Aevatar.AI.Abstractions.Middleware;

namespace Aevatar.GAgents.Scheduled;

internal sealed class SkillRunnerInteractiveDeliveryTracker
{
    public bool HasSuccessfulInteractiveDelivery { get; private set; }

    public void Reset() => HasSuccessfulInteractiveDelivery = false;

    public void RecordSuccessfulInteractiveDelivery() => HasSuccessfulInteractiveDelivery = true;
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

    private readonly SkillRunnerInteractiveDeliveryTracker _tracker;

    public SkillRunnerInteractiveDeliveryTrackingMiddleware(SkillRunnerInteractiveDeliveryTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        await next().ConfigureAwait(false);

        if (!IsInteractiveDeliveryTool(context.ToolName, context.ArgumentsJson))
            return;
        if (!IsSuccessfulToolResult(context.Result))
            return;

        _tracker.RecordSuccessfulInteractiveDelivery();
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

    private static bool IsSuccessfulToolResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("success", out var success))
                return success.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var value))
            {
                return value == 0;
            }

            if (root.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String)
            {
                var statusValue = status.GetString();
                return string.Equals(statusValue, "queued", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(statusValue, "sent", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(statusValue, "success", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
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
}
