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
            return string.Equals(existing.LastEventId, incoming.LastEventId, StringComparison.Ordinal)
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Conflict();
        }

        if (incoming.StateVersion > existing.StateVersion + 1)
            return ProjectionWriteResult.Gap();

        return ProjectionWriteResult.Applied();
    }
}
