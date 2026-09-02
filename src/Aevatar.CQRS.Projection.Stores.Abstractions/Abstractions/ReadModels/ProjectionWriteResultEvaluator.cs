using Google.Protobuf;

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
}
