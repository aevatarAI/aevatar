namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreRequirements
{
    private static readonly IReadOnlySet<ProjectionIndexKind> EmptyIndexKinds =
        new HashSet<ProjectionIndexKind>();

    public ProjectionStoreRequirements(
        bool requiresIndexing = false,
        IEnumerable<ProjectionIndexKind>? requiredIndexKinds = null,
        bool requiresAliases = false,
        bool requiresSchemaValidation = false,
        bool requiresGraph = false,
        bool requiresGraphTraversal = false)
    {
        RequiresIndexing = requiresIndexing;
        RequiresAliases = requiresAliases;
        RequiresSchemaValidation = requiresSchemaValidation;
        RequiresGraph = requiresGraph;
        RequiresGraphTraversal = requiresGraphTraversal;

        var normalizedIndexKinds = (requiredIndexKinds ?? [])
            .Where(x => x != ProjectionIndexKind.None)
            .ToHashSet();

        RequiredIndexKinds = normalizedIndexKinds.Count == 0
            ? EmptyIndexKinds
            : normalizedIndexKinds;
    }

    public bool RequiresIndexing { get; }

    public IReadOnlySet<ProjectionIndexKind> RequiredIndexKinds { get; }

    public bool RequiresAliases { get; }

    public bool RequiresSchemaValidation { get; }

    public bool RequiresGraph { get; }

    public bool RequiresGraphTraversal { get; }
}
