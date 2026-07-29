using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDeleteMarkerPayload
{
    internal const string TombstoneField = "__projection_tombstone";
    internal const string DeletedAtUtcField = "__projection_deleted_at_utc";

    internal static bool IsDeleteMarker(JsonElement source)
    {
        if (!source.TryGetProperty(TombstoneField, out var marker))
            return false;

        return marker.ValueKind == JsonValueKind.True ||
               marker.ValueKind == JsonValueKind.String &&
               bool.TryParse(marker.GetString(), out var parsed) &&
               parsed;
    }

    internal static ProjectionDocumentDeleteMarker? TryParse(JsonElement source)
    {
        if (!IsDeleteMarker(source))
            return null;

        var id = ReadString(source, "id");
        var actorId = ReadString(source, "actor_id");
        var stateVersion = ReadInt64(source, "state_version");
        var lastEventId = ReadString(source, "last_event_id");
        var updatedAt = ReadDateTimeOffset(source, "updated_at_utc_value")
            ?? ReadDateTimeOffset(source, DeletedAtUtcField)
            ?? DateTimeOffset.MinValue;

        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(actorId) ||
            stateVersion <= 0 ||
            string.IsNullOrWhiteSpace(lastEventId))
        {
            return null;
        }

        return new ProjectionDocumentDeleteMarker(
            id.Trim(),
            actorId.Trim(),
            stateVersion,
            lastEventId.Trim(),
            updatedAt);
    }

    internal static string Serialize(ProjectionDocumentDeleteMarker marker, string keyValue)
    {
        var normalized = Normalize(marker);
        var payload = new JsonObject
        {
            [TombstoneField] = true,
            ["id"] = normalized.Id,
            ["actor_id"] = normalized.ActorId,
            ["state_version"] = normalized.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["last_event_id"] = normalized.LastEventId,
            ["updated_at_utc_value"] = normalized.UpdatedAt.UtcDateTime.ToString("O"),
            [DeletedAtUtcField] = normalized.UpdatedAt.UtcDateTime.ToString("O"),
            [ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField] = keyValue,
        };
        return payload.ToJsonString();
    }

    internal static ProjectionDocumentDeleteMarker Normalize(ProjectionDocumentDeleteMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var normalized = marker with
        {
            Id = marker.Id?.Trim() ?? string.Empty,
            ActorId = marker.ActorId?.Trim() ?? string.Empty,
            LastEventId = marker.LastEventId?.Trim() ?? string.Empty,
        };
        _ = ProjectionWriteResultEvaluator.Evaluate(null, normalized);
        if (normalized.StateVersion <= 0)
            throw new InvalidOperationException("Projection delete marker state version must be positive.");

        return normalized;
    }

    internal static ProjectionWriteResult EvaluateUpsertAgainstDeleteMarker(
        ProjectionDocumentDeleteMarker existing,
        IProjectionReadModel incoming)
    {
        if (!string.Equals(existing.ActorId, incoming.ActorId, StringComparison.Ordinal))
            return ProjectionWriteResult.Conflict();

        if (incoming.StateVersion < existing.StateVersion)
            return ProjectionWriteResult.Stale();

        if (incoming.StateVersion == existing.StateVersion)
        {
            return string.Equals(existing.LastEventId, incoming.LastEventId, StringComparison.Ordinal)
                ? ProjectionWriteResult.Duplicate()
                : ProjectionWriteResult.Conflict();
        }

        return ProjectionWriteResult.Applied();
    }

    private static string ReadString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long ReadInt64(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
