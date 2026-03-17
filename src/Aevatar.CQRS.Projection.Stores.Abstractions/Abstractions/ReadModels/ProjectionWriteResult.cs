namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public enum ProjectionWriteDisposition
{
    Applied = 0,
    Duplicate = 1,
    Stale = 2,
    Gap = 3,
    Conflict = 4,
}

public readonly record struct ProjectionWriteResult(ProjectionWriteDisposition Disposition)
{
    public bool IsApplied => Disposition == ProjectionWriteDisposition.Applied;

    public bool IsNonTerminal =>
        Disposition is ProjectionWriteDisposition.Duplicate or ProjectionWriteDisposition.Stale;

    public bool IsRejected =>
        Disposition is ProjectionWriteDisposition.Gap or ProjectionWriteDisposition.Conflict;

    public static ProjectionWriteResult Applied() => new(ProjectionWriteDisposition.Applied);

    public static ProjectionWriteResult Duplicate() => new(ProjectionWriteDisposition.Duplicate);

    public static ProjectionWriteResult Stale() => new(ProjectionWriteDisposition.Stale);

    public static ProjectionWriteResult Gap() => new(ProjectionWriteDisposition.Gap);

    public static ProjectionWriteResult Conflict() => new(ProjectionWriteDisposition.Conflict);
}
