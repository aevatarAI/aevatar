using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDeleteMarkerPayload
{
    internal const string TombstoneField = "__projection_tombstone";
    internal const string DeletedAtUtcField = "__projection_deleted_at_utc";
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true));
    private static readonly JsonParser Parser = new(
        JsonParser.Settings.Default
            .WithIgnoreUnknownFields(true));

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

        ProjectionDocumentDeleteMarkerRecord record;
        try
        {
            record = Parser.Parse<ProjectionDocumentDeleteMarkerRecord>(source.GetRawText());
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(record.Id) ||
            string.IsNullOrWhiteSpace(record.ActorId) ||
            record.StateVersion <= 0 ||
            string.IsNullOrWhiteSpace(record.LastEventId))
        {
            return null;
        }

        var updatedAt = record.UpdatedAtUtcValue?.ToDateTimeOffset()
            ?? record.DeletedAtUtcValue?.ToDateTimeOffset()
            ?? DateTimeOffset.MinValue;
        return new ProjectionDocumentDeleteMarker(
            record.Id.Trim(),
            record.ActorId.Trim(),
            record.StateVersion,
            record.LastEventId.Trim(),
            updatedAt);
    }

    internal static string Serialize(ProjectionDocumentDeleteMarker marker, string keyValue)
    {
        var normalized = Normalize(marker);
        var record = new ProjectionDocumentDeleteMarkerRecord
        {
            ProjectionTombstone = true,
            Id = normalized.Id,
            ActorId = normalized.ActorId,
            StateVersion = normalized.StateVersion,
            LastEventId = normalized.LastEventId,
            UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(normalized.UpdatedAt),
            DeletedAtUtcValue = Timestamp.FromDateTimeOffset(normalized.UpdatedAt),
            ProjectionDocumentId = keyValue,
        };
        return Formatter.Format(record);
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
}
