using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStorePropertyCodec
{
    internal static string SerializeProperties(
        IReadOnlyDictionary<string, string> properties,
        JsonSerializerOptions jsonOptions)
    {
        if (properties.Count == 0)
            return "{}";
        return JsonSerializer.Serialize(properties, jsonOptions);
    }

    internal static Dictionary<string, string> DeserializeProperties(
        string payload,
        JsonSerializerOptions jsonOptions,
        ILogger logger,
        string providerName)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, jsonOptions);
            return parsed == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to deserialize graph properties payload. provider={Provider}",
                providerName);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
