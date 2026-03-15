namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public static class ProjectionWriteResultEvaluator
{
    public static ProjectionWriteResult Evaluate(
        IProjectionReadModel? existing,
        IProjectionReadModel incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (existing == null)
            return ProjectionWriteResult.Applied();

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
