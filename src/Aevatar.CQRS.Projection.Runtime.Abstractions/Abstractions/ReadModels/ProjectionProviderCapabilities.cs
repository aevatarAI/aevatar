namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionProviderCapabilities
{
    private static readonly IReadOnlySet<ProjectionIndexKind> EmptyIndexKinds =
        new HashSet<ProjectionIndexKind>();

    public ProjectionProviderCapabilities(
        string providerName,
        bool supportsIndexing,
        IEnumerable<ProjectionIndexKind>? indexKinds = null,
        bool supportsAliases = false,
        bool supportsSchemaValidation = false,
        bool supportsGraph = false,
        bool supportsGraphTraversal = false)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name must not be empty.", nameof(providerName));

        ProviderName = providerName.Trim();
        SupportsIndexing = supportsIndexing;
        SupportsAliases = supportsAliases;
        SupportsSchemaValidation = supportsSchemaValidation;
        SupportsGraph = supportsGraph;
        SupportsGraphTraversal = supportsGraphTraversal;

        var normalizedIndexKinds = (indexKinds ?? [])
            .Where(x => x != ProjectionIndexKind.None)
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

    public IReadOnlySet<ProjectionIndexKind> IndexKinds { get; }

    public bool SupportsAliases { get; }

    public bool SupportsSchemaValidation { get; }

    public bool SupportsGraph { get; }

    public bool SupportsGraphTraversal { get; }
}
