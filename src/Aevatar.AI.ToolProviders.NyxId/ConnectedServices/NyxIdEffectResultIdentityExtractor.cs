using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public static class NyxIdEffectResultIdentityExtractor
{
    public static string? Extract(
        AgentToolOperationReadBackPayload? readBack,
        string? effectResultJson) =>
        ExtractAtPointer(effectResultJson, readBack?.EffectResultIdentityJsonPointer);

    public static string? ExtractAtPointer(
        string? effectResultJson,
        string? identityJsonPointer)
    {
        if (string.IsNullOrWhiteSpace(effectResultJson) ||
            string.IsNullOrWhiteSpace(identityJsonPointer) ||
            !AgentToolEffectResultIdentityJsonPointer.IsValid(identityJsonPointer))
        {
            return null;
        }

        try
        {
            JsonNode? value = JsonNode.Parse(effectResultJson);
            foreach (var encoded in identityJsonPointer.Split('/').Skip(1))
            {
                var segment = encoded.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                if (value is JsonObject obj && obj.TryGetPropertyValue(segment, out value))
                    continue;
                if (value is JsonArray array && int.TryParse(segment, out var index) &&
                    index >= 0 && index < array.Count)
                {
                    value = array[index];
                    continue;
                }
                return null;
            }

            return value is JsonValue jsonValue &&
                   jsonValue.TryGetValue<string>(out var resourceId) &&
                   !string.IsNullOrWhiteSpace(resourceId)
                ? resourceId.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
