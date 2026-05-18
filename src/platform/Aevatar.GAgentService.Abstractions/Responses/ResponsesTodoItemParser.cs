using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

/// <summary>
/// Boundary parser for Responses TodoWrite arguments. Lives outside the domain
/// actor so the actor stays Protobuf-only. Used by both the host adapter (when
/// dispatching) and the snapshot preview returned to callers — there is only
/// one parser, so the actor's persisted view never diverges from the preview.
/// Malformed JSON returns an empty list rather than synthesizing a raw-content
/// todo: callers should see "no parse" instead of "one weird todo".
/// </summary>
public static class ResponsesTodoItemParser
{
    public static IReadOnlyList<ResponsesTodoItem> Parse(
        string? argumentsJson,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        var items = new List<ResponsesTodoItem>();
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return items;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("todos", out var todos) &&
                todos.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var todo in todos.EnumerateArray())
                {
                    var item = ParseOne(todo, index, sourceResponseId, observedAt);
                    if (item != null)
                        items.Add(item);
                    index++;
                }

                return items;
            }

            var single = ParseOne(root, 0, sourceResponseId, observedAt);
            if (single != null)
                items.Add(single);
        }
        catch (JsonException)
        {
            // Malformed JSON: return empty rather than synthesize a fake todo.
        }

        return items;
    }

    private static ResponsesTodoItem? ParseOne(
        JsonElement element,
        int index,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        if (element.ValueKind == JsonValueKind.String)
            return Build(null, element.GetString(), "pending", index, sourceResponseId, observedAt);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var content = ReadString(element, "content")
                      ?? ReadString(element, "task")
                      ?? ReadString(element, "title")
                      ?? ReadString(element, "text");
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var id = ReadString(element, "id");
        var status = ReadString(element, "status") ?? "pending";
        return Build(id, content, status, index, sourceResponseId, observedAt);
    }

    private static ResponsesTodoItem Build(
        string? id,
        string? content,
        string status,
        int index,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        var normalizedContent = Normalize(content) ?? string.Empty;
        var normalizedStatus = Normalize(status) ?? "pending";
        var itemId = Normalize(id) ?? $"todo_{index:D4}";
        return new ResponsesTodoItem
        {
            Id = itemId,
            Content = normalizedContent,
            Status = normalizedStatus,
            SourceResponseId = Normalize(sourceResponseId) ?? string.Empty,
            CreatedAt = observedAt.Clone(),
            UpdatedAt = observedAt.Clone(),
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
