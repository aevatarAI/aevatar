using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public static class RemoteToolApprovalMessageMapper
{
    private const int MaxArgumentsJsonLength = 64 * 1024;
    private const int MaxArgumentsSummaryLength = 2048;
    private const int MaxSummaryDepth = 4;
    private const int MaxPropertiesPerObject = 24;
    private const int MaxArrayItems = 4;

    public static MessageContent ToMessageContent(RemoteToolApprovalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Tool Approval Required",
            Text = BuildMarkdown(notification),
        };
        card.Actions.Add(BuildDecisionAction("nyxid-approval-approve", "Approve", true, true, false, notification));
        card.Actions.Add(BuildDecisionAction("nyxid-approval-reject", "Reject", false, false, true, notification));

        var content = new MessageContent { Text = string.Empty };
        content.Cards.Add(card);
        return content;
    }

    private static ActionElement BuildDecisionAction(
        string actionId,
        string label,
        bool approved,
        bool primary,
        bool danger,
        RemoteToolApprovalNotification notification) =>
        new()
        {
            Kind = ActionElementKind.Button,
            ActionId = actionId,
            Label = label,
            IsPrimary = primary,
            IsDanger = danger,
            NyxIdApproval = new NyxIdApprovalActionPayload
            {
                RequestId = notification.Submission.RemoteApprovalId,
                Approved = approved,
            },
        };

    private static string BuildMarkdown(RemoteToolApprovalNotification notification)
    {
        var request = notification.Request;
        var lines = new List<string>
        {
            $"Tool: `{request.ToolName}`",
            $"Request: `{notification.Submission.RemoteApprovalId}`",
            $"Local request: `{request.RequestId}`",
            $"Destructive: `{request.IsDestructive.ToString().ToLowerInvariant()}`",
        };

        if (notification.Submission.ExpiresAt is { } expiresAt)
            lines.Add($"Expires: `{expiresAt:O}`");

        if (!string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            lines.Add(string.Empty);
            lines.Add("Arguments (values redacted):");
            lines.Add($"```json\n{BuildArgumentsSummary(request.ArgumentsJson)}\n```");
        }

        return string.Join('\n', lines);
    }

    private static string BuildArgumentsSummary(string argumentsJson)
    {
        if (argumentsJson.Length > MaxArgumentsJsonLength)
            return "{\"summary\":\"payload too large; values omitted\"}";

        try
        {
            using var document = JsonDocument.Parse(argumentsJson, new JsonDocumentOptions { MaxDepth = 32 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedactedShape(document.RootElement, writer, depth: 0);
            }

            var summary = Encoding.UTF8.GetString(stream.ToArray());
            return summary.Length <= MaxArgumentsSummaryLength
                ? summary
                : "{\"summary\":\"argument structure too large; values omitted\"}";
        }
        catch (JsonException)
        {
            return "{\"summary\":\"invalid JSON; values omitted\"}";
        }
    }

    private static void WriteRedactedShape(JsonElement element, Utf8JsonWriter writer, int depth)
    {
        if (depth >= MaxSummaryDepth)
        {
            writer.WriteStringValue("[nested value redacted]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteRedactedObject(element, writer, depth);
                break;
            case JsonValueKind.Array:
                WriteRedactedArray(element, writer, depth);
                break;
            case JsonValueKind.String:
                writer.WriteStringValue("[string redacted]");
                break;
            case JsonValueKind.Number:
                writer.WriteStringValue("[number redacted]");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteStringValue("[boolean redacted]");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteStringValue("[null]");
                break;
            default:
                writer.WriteStringValue("[value redacted]");
                break;
        }
    }

    private static void WriteRedactedObject(JsonElement element, Utf8JsonWriter writer, int depth)
    {
        writer.WriteStartObject();
        var propertyIndex = 0;
        var omitted = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (propertyIndex >= MaxPropertiesPerObject)
            {
                omitted++;
                continue;
            }

            // JSON property names are caller-controlled and can themselves contain secrets.
            writer.WritePropertyName($"field_{propertyIndex + 1}");
            WriteRedactedShape(property.Value, writer, depth + 1);
            propertyIndex++;
        }

        if (omitted > 0)
            writer.WriteString("_omitted_fields", $"[{omitted} field(s) omitted]");
        writer.WriteEndObject();
    }

    private static void WriteRedactedArray(JsonElement element, Utf8JsonWriter writer, int depth)
    {
        writer.WriteStartArray();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (index >= MaxArrayItems)
                break;

            WriteRedactedShape(item, writer, depth + 1);
            index++;
        }

        var omitted = element.GetArrayLength() - index;
        if (omitted > 0)
            writer.WriteStringValue($"[{omitted} item(s) omitted]");
        writer.WriteEndArray();
    }

}
