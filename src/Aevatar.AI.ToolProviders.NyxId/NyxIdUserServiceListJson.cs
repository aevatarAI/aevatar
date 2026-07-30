using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Shared reader for the published NyxID user-service list envelope. Both workflow capability
/// admission and the human-session service probe read the same wire shape, so the envelope,
/// collection and entry conventions live in one place.
/// </summary>
internal static class NyxIdUserServiceListJson
{
    private static readonly string[] CollectionNames = ["keys", "services", "items", "data"];

    public static bool IsErrorEnvelope(JsonElement root, out int status)
    {
        status = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind is not (JsonValueKind.True or JsonValueKind.String))
        {
            return false;
        }

        if (root.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsed))
            status = parsed;
        return true;
    }

    public static bool HasServiceCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return true;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return CollectionNames.Any(name =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array);
    }

    public static IEnumerable<JsonElement> EnumerateServiceEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var name in CollectionNames)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    public static string? ReadString(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.ValueKind != JsonValueKind.Object ||
                !owner.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }
}
