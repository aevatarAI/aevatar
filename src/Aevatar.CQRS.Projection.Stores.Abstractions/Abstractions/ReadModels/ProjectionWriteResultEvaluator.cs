using Google.Protobuf;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public static class ProjectionWriteResultEvaluator
{
    public static ProjectionWriteResult Evaluate(
        IProjectionReadModel? existing,
        IProjectionReadModel incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (string.IsNullOrWhiteSpace(incoming.Id))
            throw new InvalidOperationException("Projection read model id must be non-empty.");
        if (string.IsNullOrWhiteSpace(incoming.ActorId))
            throw new InvalidOperationException("Projection read model actor id must be non-empty.");

        if (existing == null)
            return ProjectionWriteResult.Applied();

        if (!string.Equals(existing.ActorId, incoming.ActorId, StringComparison.Ordinal))
            return ProjectionWriteResult.Conflict();

        if (incoming.StateVersion < existing.StateVersion)
            return ProjectionWriteResult.Stale();

        if (incoming.StateVersion == existing.StateVersion)
        {
            // Route-fenced read models: a strictly higher route epoch takes over the same
            // source version (writer cutover); a lower epoch is stale. Equal epochs fall
            // through to the strict identity and byte rules below.
            if (existing is IProjectionRouteFencedReadModel existingFenced &&
                incoming is IProjectionRouteFencedReadModel incomingFenced &&
                existingFenced.RouteEpoch != incomingFenced.RouteEpoch)
            {
                return incomingFenced.RouteEpoch > existingFenced.RouteEpoch
                    ? ProjectionWriteResult.Applied()
                    : ProjectionWriteResult.Stale();
            }

            var maintenancePrecedence = EvaluateSameVersionMaintenancePrecedence(
                existing.LastEventId,
                incoming.LastEventId);
            if (maintenancePrecedence.HasValue)
                return maintenancePrecedence.Value;

            if (!string.Equals(existing.LastEventId, incoming.LastEventId, StringComparison.Ordinal))
                return ProjectionWriteResult.Conflict();

            if (existing is IMessage existingMessage && incoming is IMessage incomingMessage)
            {
                return existingMessage.Descriptor.FullName == incomingMessage.Descriptor.FullName &&
                       existingMessage.ToByteString().Equals(incomingMessage.ToByteString())
                    ? ProjectionWriteResult.Duplicate()
                    : ProjectionWriteResult.Conflict();
            }

            // Legacy non-protobuf read models cannot prove byte equivalence.
            // Preserve their historical same-event idempotency behavior while
            // every typed projection read model uses the strict branch above.
            return ProjectionWriteResult.Applied();
        }

        return ProjectionWriteResult.Applied();
    }

    /// <summary>
    /// Orders an authoritative committed-state maintenance republish against an
    /// ordinary projection write at the same source version. A republish may
    /// repair a stale replica, and the repaired replica fences delayed ordinary
    /// deliveries for that version. Equal kinds retain the strict event/byte
    /// comparison performed by the caller.
    /// </summary>
    public static ProjectionWriteResult? EvaluateSameVersionMaintenancePrecedence(
        string? existingEventId,
        string? incomingEventId)
    {
        var existingIsMaintenance = CommittedStateRepublish.IsRepublishEventId(existingEventId);
        var incomingIsMaintenance = CommittedStateRepublish.IsRepublishEventId(incomingEventId);
        if (existingIsMaintenance == incomingIsMaintenance)
            return null;

        return incomingIsMaintenance
            ? ProjectionWriteResult.Applied()
            : ProjectionWriteResult.Stale();
    }
}
