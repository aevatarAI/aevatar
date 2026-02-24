namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelProviderCapabilities
{
    private static readonly IReadOnlySet<ProjectionReadModelIndexKind> EmptyIndexKinds =
        new HashSet<ProjectionReadModelIndexKind>();

    public ProjectionReadModelProviderCapabilities(
        string providerName,
        bool supportsIndexing,
        IEnumerable<ProjectionReadModelIndexKind>? indexKinds = null,
        bool supportsAliases = false,
        bool supportsSchemaValidation = false,
        bool supportsRelations = false,
        bool supportsRelationTraversal = false)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name must not be empty.", nameof(providerName));

        ProviderName = providerName.Trim();
        SupportsIndexing = supportsIndexing;
        SupportsAliases = supportsAliases;
        SupportsSchemaValidation = supportsSchemaValidation;
        SupportsRelations = supportsRelations;
        SupportsRelationTraversal = supportsRelationTraversal;

        var normalizedIndexKinds = (indexKinds ?? [])
            .Where(x => x != ProjectionReadModelIndexKind.None)
            .ToHashSet();

        if (!supportsIndexing && normalizedIndexKinds.Count > 0)
            throw new ArgumentException(
                "Index kinds cannot be declared when supportsIndexing is false.",
                nameof(indexKinds));

        IndexKinds = normalizedIndexKinds.Count == 0
            ? EmptyIndexKinds
            : normalizedIndexKinds;
    }

    public string ProviderName { get; }

    public bool SupportsIndexing { get; }

    public IReadOnlySet<ProjectionReadModelIndexKind> IndexKinds { get; }

    public bool SupportsAliases { get; }

    public bool SupportsSchemaValidation { get; }

    public bool SupportsRelations { get; }

    public bool SupportsRelationTraversal { get; }
}
