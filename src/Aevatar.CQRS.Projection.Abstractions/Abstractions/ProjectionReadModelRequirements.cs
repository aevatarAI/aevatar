namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelRequirements
{
    private static readonly IReadOnlySet<ProjectionReadModelIndexKind> EmptyIndexKinds =
        new HashSet<ProjectionReadModelIndexKind>();

    public ProjectionReadModelRequirements(
        bool requiresIndexing = false,
        IEnumerable<ProjectionReadModelIndexKind>? requiredIndexKinds = null,
        bool requiresAliases = false,
        bool requiresSchemaValidation = false)
    {
        RequiresIndexing = requiresIndexing;
        RequiresAliases = requiresAliases;
        RequiresSchemaValidation = requiresSchemaValidation;

        var normalizedIndexKinds = (requiredIndexKinds ?? [])
            .Where(x => x != ProjectionReadModelIndexKind.None)
            .ToHashSet();

        RequiredIndexKinds = normalizedIndexKinds.Count == 0
            ? EmptyIndexKinds
            : normalizedIndexKinds;
    }

    public bool RequiresIndexing { get; }

    public IReadOnlySet<ProjectionReadModelIndexKind> RequiredIndexKinds { get; }

    public bool RequiresAliases { get; }

    public bool RequiresSchemaValidation { get; }
}
