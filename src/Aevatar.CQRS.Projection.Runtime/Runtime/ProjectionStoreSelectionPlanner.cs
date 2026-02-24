namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreSelectionPlanner : IProjectionStoreSelectionPlanner
{
    public ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionStoreRequirements graphRequirements)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readModelType);
        ArgumentNullException.ThrowIfNull(graphRequirements);
        EnsureStoreModeSupported(options.StoreMode);

        var readModelRequirements = BuildDocumentRequirements(readModelType);
        var readModelRequiresGraph = typeof(IGraphReadModel).IsAssignableFrom(readModelType);
        var readModelProvider = NormalizeRequiredProviderName(options.DocumentProvider);
        var readModelSelectionOptions = new ProjectionStoreSelectionOptions
        {
            RequestedProviderName = readModelProvider,
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        var mergedGraphRequirements = MergeGraphRequirements(graphRequirements, readModelRequiresGraph);
        var graphSelectionOptions = new ProjectionStoreSelectionOptions
        {
            RequestedProviderName = NormalizeGraphProviderName(
                options.GraphProvider,
                readModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        return new ProjectionStoreSelectionPlan(
            readModelRequirements,
            readModelSelectionOptions,
            mergedGraphRequirements,
            graphSelectionOptions);
    }

    private static ProjectionStoreRequirements MergeGraphRequirements(
        ProjectionStoreRequirements graphRequirements,
        bool readModelRequiresGraph)
    {
        return new ProjectionStoreRequirements(
            requiresIndexing: graphRequirements.RequiresIndexing,
            requiredIndexKinds: graphRequirements.RequiredIndexKinds,
            requiresAliases: graphRequirements.RequiresAliases,
            requiresSchemaValidation: graphRequirements.RequiresSchemaValidation,
            requiresGraph: graphRequirements.RequiresGraph || readModelRequiresGraph,
            requiresGraphTraversal: graphRequirements.RequiresGraphTraversal || readModelRequiresGraph);
    }

    private static ProjectionStoreRequirements BuildDocumentRequirements(Type readModelType)
    {
        var requiredIndexKinds = new List<ProjectionIndexKind>();

        if (typeof(IDocumentReadModel).IsAssignableFrom(readModelType))
            requiredIndexKinds.Add(ProjectionIndexKind.Document);

        return new ProjectionStoreRequirements(
            requiresIndexing: requiredIndexKinds.Count > 0,
            requiredIndexKinds: requiredIndexKinds);
    }

    private static string NormalizeRequiredProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidOperationException(
                "Projection read-model provider is required and cannot be empty.");
        }

        return providerName.Trim();
    }

    private static string NormalizeGraphProviderName(
        string graphProviderName,
        string fallbackProviderName)
    {
        if (string.IsNullOrWhiteSpace(graphProviderName))
            return fallbackProviderName;

        return graphProviderName.Trim();
    }

    private static void EnsureStoreModeSupported(ProjectionStoreMode readModelMode)
    {
        if (readModelMode != ProjectionStoreMode.StateOnly)
            return;

        throw new InvalidOperationException(
            "Projection store selection does not support Projection:Document:Mode=StateOnly. " +
            "Use Custom or Default.");
    }
}
